using System.Runtime.InteropServices;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using Amberglass.Preview.Cache;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Amberglass.Preview.Images;

/// <summary>
/// Decodes an image at native resolution for full-quality viewing.
///
/// Nothing here downscales or re-encodes: the preview pipeline already produces a 1600px JPEG, which
/// is what makes the grid fast, and looking at that fullscreen is looking at a rendition rather than
/// the photograph. RAW files go through their embedded preview, which is the largest thing available
/// without a demosaicing library — usually the camera's own full-size JPEG.
/// </summary>
public sealed class SkiaFullImageDecoder : IFullImageDecoder
{
    /// <summary>
    /// Ceiling on decoded pixels, about 80MP. Beyond this the image is decoded downscaled: 80MP is
    /// already 320 MB of BGRA, and a panorama that large would otherwise be able to exhaust memory
    /// from a double-click.
    /// </summary>
    private const long MaxPixels = 80_000_000;

    private readonly IEmbeddedPreviewExtractor _previews;
    private readonly IRawDecoder _raw;
    private readonly ISettingsService _settings;
    private readonly ILogger<SkiaFullImageDecoder> _logger;
    private readonly RenderCache? _renders;
    private readonly IRenderCacheMaintenance? _renderMaintenance;
    private readonly ITransparencyMaskReader? _masks;

    public SkiaFullImageDecoder(
        IEmbeddedPreviewExtractor previews,
        IRawDecoder raw,
        ISettingsService settings,
        ILogger<SkiaFullImageDecoder> logger,
        RenderCache? renders = null,
        IRenderCacheMaintenance? renderMaintenance = null,
        ITransparencyMaskReader? masks = null)
    {
        _masks = masks;
        _previews = previews;
        _raw = raw;
        _settings = settings;
        _logger = logger;
        _renders = renders;
        _renderMaintenance = renderMaintenance;
    }

    public Task<DecodedImage?> DecodeAsync(MediaFile file, CancellationToken cancellationToken = default)
        => Task.Run(() => DecodeCore(file, cancellationToken), cancellationToken);

    private async Task<DecodedImage?> DecodeCore(MediaFile file, CancellationToken cancellationToken)
    {
        if (file.MediaType != MediaType.Image)
        {
            return null;
        }

        try
        {
            if (SkiaThumbnailRenderer.CanDecode(file.FullPath))
            {
                await using var stream = File.OpenRead(file.FullPath);
                return Decode(stream, cancellationToken);
            }

            // RAW. Developing is the real thing but costs seconds; the embedded preview is instant.
            // Which one is wanted is a setting, and a failed develop falls back rather than showing
            // nothing.
            if (_settings.Current.DevelopRawFiles && _raw.IsAvailable)
            {
                var cacheKey = RenderCacheKey(file);

                if (cacheKey is not null &&
                    await ReadCachedAsync(cacheKey, file, cancellationToken).ConfigureAwait(false) is { } cached)
                {
                    return Masked(cached, file, hard: false);
                }

                if (await _raw.DevelopAsync(file, cancellationToken).ConfigureAwait(false) is { } developed)
                {
                    if (cacheKey is not null)
                    {
                        await StoreAsync(cacheKey, developed, file, cancellationToken).ConfigureAwait(false);
                    }

                    return Masked(developed, file, hard: false);
                }

                _logger.LogDebug("Developing {File} produced nothing; falling back to its preview", file.FullPath);
            }

            var preview = await _previews.ExtractAsync(file, cancellationToken).ConfigureAwait(false);
            if (preview is null)
            {
                return null;
            }

            using var memory = new MemoryStream(preview.Jpeg);
            return Decode(memory, cancellationToken, preview) is { } decoded
                ? Masked(decoded, file, hard: true)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogDebug("Cannot open {File} at full size; it no longer exists", file.FullPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode {File} at full size", file.FullPath);
            return null;
        }
    }

    /// <summary>
    /// Clears a stitched panorama's dead space to transparent, so the viewer shows its own
    /// background there rather than the edge pixels a develop smears outwards or the white an
    /// embedded preview is painted with.
    ///
    /// Applied after the render cache rather than stored in it: the cache is JPEG and would keep
    /// only black, where transparent is what lets the theme show through.
    /// </summary>
    /// <param name="hard">True for an embedded preview, for the reason the thumbnail renderer gives.</param>
    private DecodedImage Masked(DecodedImage image, MediaFile file, bool hard)
    {
        if (_masks?.Read(file, Math.Max(image.Width, image.Height)) is { } mask
            && !mask.Apply(image.Pixels, image.Width, image.Height, image.Width * 4, hard))
        {
            _logger.LogDebug(
                "The transparency mask of {File} ({MaskWidth}x{MaskHeight}) does not fit its {Width}x{Height} picture",
                file.FullPath, mask.Width, mask.Height, image.Width, image.Height);
        }

        return image;
    }

    /// <summary>
    /// The cache key for this file, or null when the render cache is off, absent, or the file is not
    /// one worth storing.
    /// </summary>
    private string? RenderCacheKey(MediaFile file)
    {
        var settings = _settings.Current;

        return _renders is not null
               && settings.RenderCacheEnabled
               && RenderCache.IsWorthCaching(file, settings.DevelopRawFiles)
            ? _renders.GetCacheKey(file, settings.RawDevelop)
            : null;
    }

    /// <summary>
    /// A stored rendition, decoded back to pixels. Null on a miss, and on a hit that turns out to be
    /// unreadable — a corrupt entry should cost a develop, not the picture.
    /// </summary>
    private async Task<DecodedImage?> ReadCachedAsync(string key, MediaFile file, CancellationToken cancellationToken)
    {
        if (_renders is null ||
            await _renders.TryReadAsync(key, cancellationToken).ConfigureAwait(false) is not { } entry)
        {
            return null;
        }

        using var memory = new MemoryStream(entry.Data);
        if (Decode(memory, cancellationToken) is not { } decoded)
        {
            _logger.LogWarning("The cached rendition of {File} could not be decoded; developing again", file.FullPath);
            return null;
        }

        _logger.LogDebug("Served {File} from the render cache", file.FullPath);

        // The renderer travels with the entry: only a LibRaw develop answers to the develop
        // settings, and the viewer has to keep saying so on a cache hit.
        return decoded with { Renderer = entry.Renderer };
    }

    private async Task StoreAsync(string key, DecodedImage image, MediaFile file, CancellationToken cancellationToken)
    {
        if (_renders is null)
        {
            return;
        }

        try
        {
            var encoded = Encode(image);
            if (encoded is null)
            {
                return;
            }

            await _renders.WriteAsync(key, image.Renderer, encoded, cancellationToken).ConfigureAwait(false);
            _renderMaintenance?.NotifyBytesWritten(encoded.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The picture is already decoded and about to be shown; failing to cache it is not a
            // reason to fail the decode.
            _logger.LogWarning(ex, "Could not cache the developed rendition of {File}", file.FullPath);
        }
    }

    /// <summary>Re-encodes decoded pixels for storage, wrapping the buffer rather than copying it.</summary>
    private static byte[]? Encode(DecodedImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (image.Pixels.Length < info.BytesSize)
        {
            return null;
        }

        var pinned = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);

        try
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes))
            {
                return null;
            }

            // Encoded through the pixmap so the chroma sampling can be set. Skia's default is 4:2:0,
            // which throws away three quarters of the colour resolution — invisible in a thumbnail
            // and exactly the wrong trade for something examined at 100%.
            if (bitmap.PeekPixels() is not { } pixels)
            {
                return null;
            }

            using var stream = new SKDynamicMemoryWStream();
            var options = new SKJpegEncoderOptions(
                RenderCache.Quality,
                SKJpegEncoderDownsample.Downsample444,
                SKJpegEncoderAlphaOption.Ignore);

            if (!pixels.Encode(stream, options))
            {
                return null;
            }

            using var data = stream.DetachAsData();
            return data?.ToArray();
        }
        finally
        {
            pinned.Free();
        }
    }

    private static DecodedImage? Decode(
        Stream source, CancellationToken cancellationToken, EmbeddedPreview? fallback = null)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var decoded = DecodeWithinBudget(codec);
        if (decoded is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var origin = SkiaThumbnailRenderer.OriginFor(
            codec.EncodedOrigin, codec.Info.Width, codec.Info.Height, fallback);

        // Null means it is already the right way up, and the decoded bitmap stands as it is.
        using var oriented = SkiaThumbnailRenderer.ApplyOrientationTo(decoded, origin);
        var upright = oriented ?? decoded;

        // BGRA to match what the UI blits, so handing it over is a copy rather than a conversion.
        var info = new SKImageInfo(upright.Width, upright.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var target = new SKBitmap(info);

        if (!upright.CopyTo(target, SKColorType.Bgra8888))
        {
            return null;
        }

        return new DecodedImage(target.Bytes, target.Width, target.Height);
    }

    /// <summary>
    /// Decodes at full size, or at the nearest scale the codec offers when the image is enormous.
    /// Asking the codec to downscale is far cheaper than decoding everything and shrinking it.
    /// </summary>
    private static SKBitmap? DecodeWithinBudget(SKCodec codec)
    {
        var info = codec.Info;
        var pixels = (long)info.Width * info.Height;

        if (pixels <= MaxPixels)
        {
            return SKBitmap.Decode(codec);
        }

        var scale = Math.Sqrt(MaxPixels / (double)pixels);
        var supported = codec.GetScaledDimensions((float)scale);

        return SKBitmap.Decode(codec, new SKImageInfo(supported.Width, supported.Height));
    }
}
