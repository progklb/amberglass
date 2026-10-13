using System.Buffers.Binary;

namespace Amberglass.Preview.Images;

/// <summary>The TIFF and DNG tags the readers here need, and nothing else.</summary>
internal static class TiffTags
{
    public const ushort NewSubfileType = 254;
    public const ushort ImageWidth = 256;
    public const ushort ImageLength = 257;
    public const ushort BitsPerSample = 258;
    public const ushort Compression = 259;
    public const ushort PhotometricInterpretation = 262;
    public const ushort StripOffsets = 273;
    public const ushort Orientation = 274;
    public const ushort SamplesPerPixel = 277;
    public const ushort RowsPerStrip = 278;
    public const ushort StripByteCounts = 279;
    public const ushort Predictor = 317;
    public const ushort TileWidth = 322;
    public const ushort TileLength = 323;
    public const ushort TileOffsets = 324;
    public const ushort TileByteCounts = 325;
    public const ushort SubIfds = 330;

    /// <summary>NewSubfileType bit 0: a reduced-resolution copy of the main image.</summary>
    public const uint ReducedResolutionBit = 1;

    /// <summary>NewSubfileType bit 2: this image is a transparency mask.</summary>
    public const uint TransparencyMaskBit = 4;

    public const ushort CompressionNone = 1;
    public const ushort CompressionJpeg = 7;
    public const ushort CompressionDeflate = 8;
    public const ushort CompressionAdobeDeflate = 32946;
    public const ushort CompressionJpegXl = 52546;

    public const ushort PhotometricRgb = 2;
    public const ushort PhotometricYCbCr = 6;

    /// <summary>A file claiming more entries than this in one IFD is damaged, not ambitious.</summary>
    public const int MaxEntries = 4096;
}

/// <summary>
/// Reassembles an image stored as strips or tiles, decoding the pieces in parallel.
///
/// Read in order from the one stream, then decoded concurrently: a full-resolution DNG image is
/// dozens or hundreds of tiles, each its own JPEG XL codestream, and one at a time that was most of
/// a second for a mask alone.
/// </summary>
internal static class TiffChunks
{
    /// <param name="decode">
    /// Turns one chunk's bytes into <paramref name="bytesPerPixel"/>-byte pixels at the chunk's full
    /// size (width × height as given), or null to abandon the whole image.
    /// </param>
    public static byte[]? Assemble(
        TiffReader tiff,
        TiffIfd ifd,
        int width,
        int height,
        int bytesPerPixel,
        Func<byte[], int, int, byte[]?> decode)
    {
        // Strips are tiles the width of the image, so both are read the same way.
        IReadOnlyList<uint> offsets, counts;
        int chunkWidth, chunkHeight;

        if (ifd.Has(TiffTags.TileOffsets))
        {
            offsets = ifd.Values(TiffTags.TileOffsets);
            counts = ifd.Values(TiffTags.TileByteCounts);
            chunkWidth = (int)ifd.Single(TiffTags.TileWidth);
            chunkHeight = (int)ifd.Single(TiffTags.TileLength);
        }
        else
        {
            offsets = ifd.Values(TiffTags.StripOffsets);
            counts = ifd.Values(TiffTags.StripByteCounts);
            chunkWidth = width;
            chunkHeight = (int)Math.Min(ifd.Single(TiffTags.RowsPerStrip, (uint)height), (uint)height);
        }

        if (width <= 0 || height <= 0 || chunkWidth <= 0 || chunkHeight <= 0
            || offsets.Count == 0 || offsets.Count != counts.Count)
        {
            return null;
        }

        var across = (width + chunkWidth - 1) / chunkWidth;
        var down = (height + chunkHeight - 1) / chunkHeight;
        var chunks = Math.Min(offsets.Count, across * down);

        var encoded = new byte[chunks][];
        for (var i = 0; i < chunks; i++)
        {
            encoded[i] = tiff.ReadBytes(offsets[i], counts[i]);
        }

        var image = new byte[(long)width * height * bytesPerPixel];
        var failed = 0;

        Parallel.For(0, chunks, (i, state) =>
        {
            var chunk = decode(encoded[i], chunkWidth, chunkHeight);
            if (chunk is null || chunk.Length < chunkWidth * chunkHeight * bytesPerPixel)
            {
                Interlocked.Exchange(ref failed, 1);
                state.Stop();
                return;
            }

            // Each chunk owns its own rectangle of the image, so the copies never overlap.
            var left = i % across * chunkWidth;
            var top = i / across * chunkHeight;
            var rows = Math.Min(chunkHeight, height - top);
            var rowBytes = Math.Min(chunkWidth, width - left) * bytesPerPixel;

            for (var row = 0; row < rows; row++)
            {
                chunk.AsSpan(row * chunkWidth * bytesPerPixel, rowBytes)
                    .CopyTo(image.AsSpan((int)((((long)(top + row) * width) + left) * bytesPerPixel), rowBytes));
            }
        });

        return failed == 0 ? image : null;
    }
}

/// <summary>The tags of one image file directory, each as its list of values.</summary>
internal sealed class TiffIfd(Dictionary<ushort, uint[]> tags)
{
    public bool Has(ushort tag) => tags.ContainsKey(tag);

    public IReadOnlyList<uint> Values(ushort tag) => tags.TryGetValue(tag, out var values) ? values : [];

    public uint Single(ushort tag, uint fallback = 0)
        => tags.TryGetValue(tag, out var values) && values.Length > 0 ? values[0] : fallback;
}

/// <summary>Just enough of classic TIFF to walk IFDs and read integer tags.</summary>
internal sealed class TiffReader
{
    private readonly Stream _stream;
    private readonly bool _littleEndian;

    public TiffReader(Stream stream)
    {
        _stream = stream;

        var header = ReadBytes(0, 8);
        if (header.Length < 8)
        {
            return;
        }

        _littleEndian = header[0] == 'I' && header[1] == 'I';
        var bigEndian = header[0] == 'M' && header[1] == 'M';

        IsValid = (_littleEndian || bigEndian) && U16(header, 2) == 42;
        FirstIfdOffset = IsValid ? U32(header, 4) : 0;
    }

    public bool IsValid { get; }

    public bool LittleEndian => _littleEndian;

    public uint FirstIfdOffset { get; }

    public TiffIfd ReadIfd(uint offset)
    {
        var tags = new Dictionary<ushort, uint[]>();

        var countBytes = ReadBytes(offset, 2);
        if (countBytes.Length < 2)
        {
            return new TiffIfd(tags);
        }

        var count = Math.Min((int)U16(countBytes, 0), TiffTags.MaxEntries);
        var entries = ReadBytes(offset + 2, (uint)(count * 12));

        for (var i = 0; i + 12 <= entries.Length; i += 12)
        {
            var tag = U16(entries, i);
            var type = U16(entries, i + 2);
            var valueCount = U32(entries, i + 4);

            var size = type switch
            {
                1 or 6 or 7 => 1, // BYTE, SBYTE, UNDEFINED
                3 or 8 => 2,      // SHORT, SSHORT
                4 or 9 or 13 => 4, // LONG, SLONG, IFD
                _ => 0            // Rationals, strings and the rest: nothing the mask needs.
            };

            if (size == 0 || valueCount == 0 || valueCount > 1_000_000)
            {
                continue;
            }

            var total = (uint)(size * valueCount);
            var data = total <= 4 ? entries.AsSpan(i + 8, 4).ToArray() : ReadBytes(U32(entries, i + 8), total);
            if (data.Length < total)
            {
                continue;
            }

            var values = new uint[valueCount];
            for (var v = 0; v < valueCount; v++)
            {
                values[v] = size switch
                {
                    1 => data[v],
                    2 => U16(data, v * 2),
                    _ => U32(data, v * 4)
                };
            }

            tags[tag] = values;
        }

        return new TiffIfd(tags);
    }

    public byte[] ReadBytes(uint offset, uint count)
    {
        if (offset >= _stream.Length)
        {
            return [];
        }

        var length = (int)Math.Min(count, _stream.Length - offset);
        var buffer = new byte[length];

        _stream.Position = offset;
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private ushort U16(ReadOnlySpan<byte> data, int at) => _littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..])
        : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private uint U32(ReadOnlySpan<byte> data, int at) => _littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..])
        : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
}
