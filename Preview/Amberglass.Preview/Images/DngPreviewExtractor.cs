using System.Runtime.InteropServices;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Amberglass.Preview.Images;

/// <summary>
/// Finds the best rendered preview a DNG carries, before falling back to what ExifTool extracts.
///
/// ExifTool's <c>PreviewImage</c> for a DNG is whatever sits in IFD0, and Lightroom puts a token
/// 256px JPEG there. The useful preview is elsewhere: a stitched panorama also holds a 1024px
/// rendered RGB image among its SubIFDs, JPEG XL compressed, which ExifTool does not hand over.
/// At 256px the preview pane had to develop the whole RAW to show anything sharp — about ten seconds
/// for a 47MP panorama — where the 1024px preview decodes in about ten milliseconds and is
/// adequate for the pane and the grid alike.
///
/// Every consumer of embedded previews goes through this, so the grid, the preview pane and the
/// viewer's "embedded JPEG" rendering all get the larger image without knowing where it came from.
/// </summary>
public sealed class DngPreviewExtractor : IEmbeddedPreviewExtractor
{
    /// <summary>
    /// Large enough that nothing ExifTool might find elsewhere in the file is worth asking for.
    /// Below this the two are compared, because a small IFD0 preview may not be all there is.
    /// </summary>
    internal const int SufficientEdge = 1024;

    /// <summary>
    /// Re-encoding quality for a preview that was not stored as JPEG. The result is a working preview
    /// and is cached as a thumbnail afterwards, so this only has to be indistinguishable at a glance.
    /// </summary>
    private const int ReencodeQuality = 92;

    private readonly IEmbeddedPreviewExtractor _inner;
    private readonly IJpegXlDecoder? _jpegXl;
    private readonly ILogger<DngPreviewExtractor> _logger;

    public DngPreviewExtractor(
        IEmbeddedPreviewExtractor inner, ILogger<DngPreviewExtractor> logger, IJpegXlDecoder? jpegXl = null)
    {
        _inner = inner;
        _logger = logger;
        _jpegXl = jpegXl is { IsAvailable: true } ? jpegXl : null;
    }

    /// <summary>Available without ExifTool: a DNG's own previews can be read without it.</summary>
    public bool IsAvailable => _inner.IsAvailable || _jpegXl is not null;

    public async Task<EmbeddedPreview?> ExtractAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        var own = file.FullPath.EndsWith(".dng", StringComparison.OrdinalIgnoreCase)
            ? await Task.Run(() => Read(file), cancellationToken).ConfigureAwait(false)
            : null;

        if (own is { } found && found.Edge >= SufficientEdge)
        {
            return found.Preview;
        }

        var extracted = _inner.IsAvailable
            ? await _inner.ExtractAsync(file, cancellationToken).ConfigureAwait(false)
            : null;

        if (own is null)
        {
            return extracted;
        }

        if (extracted is null)
        {
            return own.Value.Preview;
        }

        using var stream = new MemoryStream(extracted.Jpeg);
        var extractedEdge = SkiaThumbnailRenderer.ReadSize(stream) is { } size ? Math.Max(size.Width, size.Height) : 0;

        return extractedEdge > own.Value.Edge ? extracted : own.Value.Preview;
    }

    private (EmbeddedPreview Preview, int Edge)? Read(MediaFile file)
    {
        try
        {
            using var stream = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.RandomAccess);

            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A preview this cannot read is not an error; ExifTool's is still there to fall back on.
            _logger.LogDebug(ex, "Could not read the rendered preview inside {File}", file.FullPath);
            return null;
        }
    }

    internal (EmbeddedPreview Preview, int Edge)? Read(Stream stream)
    {
        var tiff = new TiffReader(stream);
        if (!tiff.IsValid)
        {
            return null;
        }

        var ifd0 = tiff.ReadIfd(tiff.FirstIfdOffset);
        var subs = ifd0.Values(TiffTags.SubIfds).Select(offset => tiff.ReadIfd(offset)).ToList();

        // The main image gives the stored size the preview's orientation is judged against.
        var main = subs.Prepend(ifd0).FirstOrDefault(ifd => ifd.Single(TiffTags.NewSubfileType) == 0);

        var candidates = subs.Prepend(ifd0)
            .Where(IsRenderedPreview)
            .Select(ifd => (Ifd: ifd, Width: (int)ifd.Single(TiffTags.ImageWidth), Height: (int)ifd.Single(TiffTags.ImageLength)))
            .Where(c => c.Width > 0 && c.Height > 0)
            .OrderByDescending(c => Math.Max(c.Width, c.Height));

        // Largest first, settling for a smaller one only when a larger cannot be decoded here.
        foreach (var candidate in candidates)
        {
            if (Encode(tiff, candidate.Ifd, candidate.Width, candidate.Height) is not { } jpeg)
            {
                continue;
            }

            var preview = new EmbeddedPreview(
                jpeg,
                ExifOrientation: (int)ifd0.Single(TiffTags.Orientation, 1),
                SourceWidth: main is null ? null : (int)main.Single(TiffTags.ImageWidth),
                SourceHeight: main is null ? null : (int)main.Single(TiffTags.ImageLength));

            return (preview, Math.Max(candidate.Width, candidate.Height));
        }

        return null;
    }

    /// <summary>
    /// A reduced-resolution image in display colours — not raw sensor data, not a mask. DNG stores
    /// its reduced-resolution <i>raw</i> images with the same subfile type, and those need a full
    /// develop to look at, so the photometric interpretation is what tells the two apart.
    /// </summary>
    private static bool IsRenderedPreview(TiffIfd ifd)
        => (ifd.Single(TiffTags.NewSubfileType) & (TiffTags.ReducedResolutionBit | TiffTags.TransparencyMaskBit))
               == TiffTags.ReducedResolutionBit
           && ifd.Single(TiffTags.PhotometricInterpretation) is TiffTags.PhotometricRgb or TiffTags.PhotometricYCbCr
           && ifd.Single(TiffTags.SamplesPerPixel, 1) == 3
           && ifd.Single(TiffTags.BitsPerSample) == 8;

    /// <summary>The preview as JPEG bytes, or null when its compression is not one handled here.</summary>
    private byte[]? Encode(TiffReader tiff, TiffIfd ifd, int width, int height)
    {
        switch (ifd.Single(TiffTags.Compression))
        {
            // Already a JPEG, provided it is one piece: handed over exactly as stored.
            case TiffTags.CompressionJpeg when ifd.Values(TiffTags.StripOffsets) is [var offset]
                                               && ifd.Values(TiffTags.StripByteCounts) is [var count]:
                return tiff.ReadBytes(offset, count);

            case TiffTags.CompressionJpegXl when _jpegXl is not null:
                var pixels = TiffChunks.Assemble(tiff, ifd, width, height, bytesPerPixel: 4, _jpegXl.DecodeBgra);
                return pixels is null ? null : EncodeJpeg(pixels, width, height);

            default:
                return null;
        }
    }

    private static byte[]? EncodeJpeg(byte[] pixels, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes))
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, ReencodeQuality);
            return data?.ToArray();
        }
        finally
        {
            pinned.Free();
        }
    }
}
