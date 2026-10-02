using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Produces thumbnails for images Skia cannot decode — CR3, NEF, ARW, RAF, DNG and friends — by
/// extracting the JPEG preview the camera embedded, then rendering that.
///
/// This is the same trick Bridge and Photo Mechanic use, and it is why RAW browsing can be fast:
/// normally nothing here develops the RAW. But the embedded preview is whatever the writer chose to
/// put there, and some files carry only a token one — a stitched DNG may hold nothing better than
/// 256px. Blown up to fill the preview pane that is unusable, so when the preview is too small to
/// serve the size being asked for, the RAW is developed instead. See <see cref="IsPreviewAdequate"/>.
/// </summary>
public sealed class RawThumbnailGenerator : IThumbnailGenerator
{
    /// <summary>
    /// How far a preview may be stretched before developing is worth the seconds it costs.
    ///
    /// Not 1: a preview slightly short of the target is barely distinguishable from an exact one, and
    /// insisting would make a 256px preview trigger a develop for a 320px grid tile — turning a
    /// folder of panoramas into minutes of work for a difference nobody can see at tile size. At 2 the
    /// grid still fills from previews and only the large preview pane, where the softness is obvious,
    /// pays for a develop.
    /// </summary>
    internal const double MaxUpscale = 2;

    private readonly IEmbeddedPreviewExtractor _extractor;
    private readonly IRawDecoder _raw;
    private readonly ILogger<RawThumbnailGenerator> _logger;

    public RawThumbnailGenerator(
        IEmbeddedPreviewExtractor extractor,
        IRawDecoder raw,
        ILogger<RawThumbnailGenerator> logger)
    {
        _extractor = extractor;
        _raw = raw;
        _logger = logger;
    }

    /// <summary>
    /// Anything that is an image but not directly decodable. Defining it as the complement rather
    /// than a RAW extension list means new formats are attempted rather than silently unsupported —
    /// and a file neither route can read simply yields null, as it does today.
    /// </summary>
    public bool CanHandle(MediaFile file)
        => file.MediaType == MediaType.Image
           && !SkiaThumbnailRenderer.CanDecode(file.FullPath)
           && (_extractor.IsAvailable || _raw.IsAvailable);

    /// <summary>
    /// Whether a preview of <paramref name="previewEdge"/> pixels can stand in for a thumbnail of
    /// <paramref name="maxEdgePixels"/>. Both are longest edges, so aspect ratio does not come into it.
    /// </summary>
    internal static bool IsPreviewAdequate(int previewEdge, int maxEdgePixels)
        => previewEdge > 0 && previewEdge * MaxUpscale >= maxEdgePixels;

    public async Task<byte[]?> GenerateAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken = default)
    {
        var preview = _extractor.IsAvailable
            ? await _extractor.ExtractAsync(file, cancellationToken).ConfigureAwait(false)
            : null;

        if (preview is not null && !NeedsDevelop(preview, maxEdgePixels, file))
        {
            return RenderPreview(preview, maxEdgePixels, file, cancellationToken);
        }

        // Either there is no preview at all, or it is too small to be worth showing at this size.
        // The result is cached like any other thumbnail, so this is paid once per file and size.
        if (_raw.IsAvailable &&
            await DevelopAsync(file, maxEdgePixels, cancellationToken).ConfigureAwait(false) is { } developed)
        {
            return developed;
        }

        // A small preview still beats nothing.
        return preview is null ? null : RenderPreview(preview, maxEdgePixels, file, cancellationToken);
    }

    private bool NeedsDevelop(EmbeddedPreview preview, int maxEdgePixels, MediaFile file)
    {
        using var stream = new MemoryStream(preview.Jpeg);
        if (SkiaThumbnailRenderer.ReadSize(stream) is not { } size)
        {
            return true;
        }

        if (IsPreviewAdequate(Math.Max(size.Width, size.Height), maxEdgePixels))
        {
            return false;
        }

        _logger.LogDebug(
            "The preview embedded in {File} is {Width}x{Height}, too small for {Target}px; developing instead",
            file.FullPath, size.Width, size.Height, maxEdgePixels);

        return true;
    }

    private async Task<byte[]?> DevelopAsync(MediaFile file, int maxEdgePixels, CancellationToken cancellationToken)
    {
        try
        {
            var developed = await _raw.DevelopAsync(file, cancellationToken).ConfigureAwait(false);
            return developed is null
                ? null
                : SkiaThumbnailRenderer.Render(developed, maxEdgePixels, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to develop {File} for a thumbnail", file.FullPath);
            return null;
        }
    }

    private byte[]? RenderPreview(
        EmbeddedPreview preview, int maxEdgePixels, MediaFile file, CancellationToken cancellationToken)
    {
        try
        {
            // The preview is handed over with the RAW's orientation beside it, because a Canon
            // preview carries no EXIF of its own and would otherwise be shown on its side.
            using var stream = new MemoryStream(preview.Jpeg);
            return SkiaThumbnailRenderer.Render(stream, maxEdgePixels, cancellationToken, preview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render the embedded preview of {File}", file.FullPath);
            return null;
        }
    }
}
