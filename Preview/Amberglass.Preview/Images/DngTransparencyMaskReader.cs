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
/// Only IFD0's SubIFDs are visited (see <see cref="TiffReader"/>). Strips and tiles are both
/// handled, uncompressed or Deflate in managed code, and JPEG XL — which is what Lightroom actually
/// writes — through the platform decoder where there is one.
/// </summary>
public sealed class DngTransparencyMaskReader : ITransparencyMaskReader
{
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
        var orientation = (int)ifd0.Single(TiffTags.Orientation, 1);

        var masks = ifd0.Values(TiffTags.SubIfds)
            .Select(offset => tiff.ReadIfd(offset))
            .Where(ifd => (ifd.Single(TiffTags.NewSubfileType) & TiffTags.TransparencyMaskBit) != 0)
            .Select(ifd => (Ifd: ifd, Width: (int)ifd.Single(TiffTags.ImageWidth), Height: (int)ifd.Single(TiffTags.ImageLength)))
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

    private byte[]? Decode(TiffReader tiff, TiffIfd ifd, int width, int height)
    {
        var bits = (int)ifd.Single(TiffTags.BitsPerSample, 8);
        var compression = (ushort)ifd.Single(TiffTags.Compression, TiffTags.CompressionNone);
        var predictor = (int)ifd.Single(TiffTags.Predictor, 1);

        return bits is 8 or 16
            ? TiffChunks.Assemble(tiff, ifd, width, height, bytesPerPixel: 1, (encoded, chunkWidth, chunkHeight) =>
                DecodeChunk(encoded, compression, predictor, bits, tiff.LittleEndian, chunkWidth, chunkHeight))
            : null;
    }

    /// <summary>One tile or strip as 8-bit values, <paramref name="width"/> × <paramref name="height"/>.</summary>
    private byte[]? DecodeChunk(
        byte[] encoded, ushort compression, int predictor, int bits, bool littleEndian, int width, int height)
    {
        var fullHeight = height;

        if (compression == TiffTags.CompressionJpegXl)
        {
            return _jpegXl?.DecodeGrey(encoded, width, height);
        }

        byte[] raw;
        switch (compression)
        {
            case TiffTags.CompressionNone:
                raw = encoded;
                break;

            case TiffTags.CompressionDeflate or TiffTags.CompressionAdobeDeflate:
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
}
