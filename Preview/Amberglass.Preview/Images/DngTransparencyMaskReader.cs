using System.Buffers.Binary;
using System.IO.Compression;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Images;

/// <summary>Finds the transparency mask a file carries, if it carries one.</summary>
public interface ITransparencyMaskReader
{
    /// <summary>
    /// The mask, at the smallest resolution that still covers <paramref name="targetEdge"/> pixels
    /// along the longest side, or null when the file has none this can read.
    /// </summary>
    TransparencyMask? Read(MediaFile file, int targetEdge);
}

/// <summary>
/// Reads the transparency mask out of a DNG.
///
/// DNG 1.4 gave the format a way to say "there is no picture here": a one-channel image stored
/// beside the main one, with a reduced-resolution copy beside each reduced-resolution image.
/// Lightroom writes one into every stitched panorama. Nothing else in the pipeline reads it — LibRaw
/// and ImageIO both develop the raw data straight through the mask, and the embedded preview has the
/// dead space flattened to white — so it is found here and applied afterwards.
///
/// A small TIFF walker rather than a library: only IFD0's SubIFDs are visited, and only the tags
/// that locate and describe the mask are read. Strips and tiles are both handled, uncompressed or
/// Deflate in managed code, and JPEG XL — which is what Lightroom actually writes — through the
/// platform decoder where there is one.
/// </summary>
public sealed class DngTransparencyMaskReader : ITransparencyMaskReader
{
    private const ushort TagNewSubfileType = 254;
    private const ushort TagImageWidth = 256;
    private const ushort TagImageLength = 257;
    private const ushort TagBitsPerSample = 258;
    private const ushort TagCompression = 259;
    private const ushort TagStripOffsets = 273;
    private const ushort TagOrientation = 274;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPredictor = 317;
    private const ushort TagTileWidth = 322;
    private const ushort TagTileLength = 323;
    private const ushort TagTileOffsets = 324;
    private const ushort TagTileByteCounts = 325;
    private const ushort TagSubIfds = 330;

    /// <summary>NewSubfileType bit 2: this image is a transparency mask.</summary>
    private const uint TransparencyMaskBit = 4;

    private const ushort CompressionNone = 1;
    private const ushort CompressionDeflate = 8;
    private const ushort CompressionAdobeDeflate = 32946;
    private const ushort CompressionJpegXl = 52546;

    /// <summary>A file claiming more IFDs or entries than this is damaged, not ambitious.</summary>
    private const int MaxEntries = 4096;

    private readonly IJpegXlDecoder? _jpegXl;
    private readonly ILogger<DngTransparencyMaskReader> _logger;

    public DngTransparencyMaskReader(ILogger<DngTransparencyMaskReader> logger, IJpegXlDecoder? jpegXl = null)
    {
        _logger = logger;
        _jpegXl = jpegXl is { IsAvailable: true } ? jpegXl : null;
    }

    public TransparencyMask? Read(MediaFile file, int targetEdge)
    {
        if (!file.FullPath.EndsWith(".dng", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.RandomAccess);

            return Read(stream, targetEdge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A damaged mask must never cost the picture: it is shown as it would have been without it.
            _logger.LogDebug(ex, "Could not read the transparency mask of {File}", file.FullPath);
            return null;
        }
    }

    internal TransparencyMask? Read(Stream stream, int targetEdge)
    {
        var tiff = new TiffReader(stream);
        if (!tiff.IsValid)
        {
            return null;
        }

        var ifd0 = tiff.ReadIfd(tiff.FirstIfdOffset);
        var orientation = (int)ifd0.Single(TagOrientation, 1);

        var masks = ifd0.Values(TagSubIfds)
            .Select(offset => tiff.ReadIfd(offset))
            .Where(ifd => (ifd.Single(TagNewSubfileType) & TransparencyMaskBit) != 0)
            .Select(ifd => (Ifd: ifd, Width: (int)ifd.Single(TagImageWidth), Height: (int)ifd.Single(TagImageLength)))
            .Where(m => m.Width > 0 && m.Height > 0)
            .OrderBy(m => Math.Max(m.Width, m.Height))
            .ToList();

        if (masks.Count == 0)
        {
            return null;
        }

        // The smallest that covers the target, or failing that the largest there is.
        var chosen = masks.FirstOrDefault(m => Math.Max(m.Width, m.Height) >= targetEdge);
        if (chosen.Ifd is null)
        {
            chosen = masks[^1];
        }

        var values = Decode(tiff, chosen.Ifd, chosen.Width, chosen.Height);
        return values is null ? null : new TransparencyMask(values, chosen.Width, chosen.Height, orientation);
    }

    private byte[]? Decode(TiffReader tiff, Ifd ifd, int width, int height)
    {
        var bits = (int)ifd.Single(TagBitsPerSample, 8);
        var compression = (ushort)ifd.Single(TagCompression, CompressionNone);
        var predictor = (int)ifd.Single(TagPredictor, 1);

        if (bits is not (8 or 16))
        {
            return null;
        }

        // Strips are tiles the width of the image, so both are read the same way.
        IReadOnlyList<uint> offsets, counts;
        int chunkWidth, chunkHeight;

        if (ifd.Has(TagTileOffsets))
        {
            offsets = ifd.Values(TagTileOffsets);
            counts = ifd.Values(TagTileByteCounts);
            chunkWidth = (int)ifd.Single(TagTileWidth);
            chunkHeight = (int)ifd.Single(TagTileLength);
        }
        else
        {
            offsets = ifd.Values(TagStripOffsets);
            counts = ifd.Values(TagStripByteCounts);
            chunkWidth = width;
            chunkHeight = (int)Math.Min(ifd.Single(TagRowsPerStrip, (uint)height), (uint)height);
        }

        if (chunkWidth <= 0 || chunkHeight <= 0 || offsets.Count == 0 || offsets.Count != counts.Count)
        {
            return null;
        }

        var across = (width + chunkWidth - 1) / chunkWidth;
        var down = (height + chunkHeight - 1) / chunkHeight;
        var chunks = Math.Min(offsets.Count, across * down);

        // Read in order from the one stream, then decode in parallel: a full-resolution mask is
        // dozens of tiles, each its own JPEG XL codestream, and one at a time that was most of a
        // second added to every visit — longer than the cached rendition it is applied to.
        var encoded = new byte[chunks][];
        for (var i = 0; i < chunks; i++)
        {
            encoded[i] = tiff.ReadBytes(offsets[i], counts[i]);
        }

        var mask = new byte[(long)width * height];
        var failed = 0;

        Parallel.For(0, chunks, (i, state) =>
        {
            var chunk = DecodeChunk(encoded[i], compression, predictor, bits, tiff.LittleEndian, chunkWidth, chunkHeight);
            if (chunk is null)
            {
                Interlocked.Exchange(ref failed, 1);
                state.Stop();
                return;
            }

            // Each chunk owns its own rectangle of the mask, so the copies never overlap.
            var left = i % across * chunkWidth;
            var top = i / across * chunkHeight;
            var rows = Math.Min(chunkHeight, height - top);
            var columns = Math.Min(chunkWidth, width - left);

            for (var row = 0; row < rows; row++)
            {
                chunk.AsSpan(row * chunkWidth, columns).CopyTo(mask.AsSpan(((top + row) * width) + left, columns));
            }
        });

        if (failed != 0)
        {
            return null;
        }

        return mask;
    }

    /// <summary>One tile or strip as 8-bit values, <paramref name="width"/> × <paramref name="height"/>.</summary>
    private byte[]? DecodeChunk(
        byte[] encoded, ushort compression, int predictor, int bits, bool littleEndian, int width, int height)
    {
        var fullHeight = height;

        if (compression == CompressionJpegXl)
        {
            return _jpegXl?.DecodeGrey(encoded, width, height);
        }

        byte[] raw;
        switch (compression)
        {
            case CompressionNone:
                raw = encoded;
                break;

            case CompressionDeflate or CompressionAdobeDeflate:
                using (var inflater = new ZLibStream(new MemoryStream(encoded), CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    inflater.CopyTo(output);
                    raw = output.ToArray();
                }

                break;

            default:
                return null;
        }

        var bytesPerSample = bits / 8;
        var rowBytes = width * bytesPerSample;
        if (raw.Length < (long)rowBytes * height)
        {
            // A final strip may stop at the image's last row rather than fill the strip.
            height = raw.Length / rowBytes;
        }

        // Horizontal differencing, the only predictor a mask is likely to use. 8-bit only: a 16-bit
        // mask with a predictor is unheard of, and guessing at it would be worse than declining.
        if (predictor == 2)
        {
            if (bits != 8)
            {
                return null;
            }

            for (var row = 0; row < height; row++)
            {
                for (var x = 1; x < width; x++)
                {
                    raw[(row * width) + x] += raw[(row * width) + x - 1];
                }
            }
        }
        else if (predictor != 1)
        {
            return null;
        }

        // Always a whole chunk, so the caller can index it the same way whether or not the file
        // stopped short; rows it did not supply stay empty and are clipped away regardless.
        var chunk = new byte[width * fullHeight];

        for (var i = 0; i < width * height; i++)
        {
            // Keep the high byte of a 16-bit sample; the mask is only ever applied at 8 bits.
            chunk[i] = bits == 8 ? raw[i] : raw[(i * 2) + (littleEndian ? 1 : 0)];
        }

        return chunk;
    }

    /// <summary>The tags of one image file directory, each as its list of values.</summary>
    private sealed class Ifd(Dictionary<ushort, uint[]> tags)
    {
        public bool Has(ushort tag) => tags.ContainsKey(tag);

        public IReadOnlyList<uint> Values(ushort tag) => tags.TryGetValue(tag, out var values) ? values : [];

        public uint Single(ushort tag, uint fallback = 0)
            => tags.TryGetValue(tag, out var values) && values.Length > 0 ? values[0] : fallback;
    }

    /// <summary>Just enough of classic TIFF to walk IFDs and read integer tags.</summary>
    private sealed class TiffReader
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

        public Ifd ReadIfd(uint offset)
        {
            var tags = new Dictionary<ushort, uint[]>();

            var countBytes = ReadBytes(offset, 2);
            if (countBytes.Length < 2)
            {
                return new Ifd(tags);
            }

            var count = Math.Min((int)U16(countBytes, 0), MaxEntries);
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

            return new Ifd(tags);
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
}
