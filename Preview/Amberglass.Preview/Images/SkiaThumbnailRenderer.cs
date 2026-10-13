using System.Runtime.InteropServices;
using Amberglass.Core.Interfaces;
using Amberglass.Core.Models;
using SkiaSharp;

namespace Amberglass.Preview.Images;

/// <summary>
/// Turns encoded image bytes into a downscaled, correctly oriented JPEG thumbnail.
///
/// Shared by the still-image generator and the RAW generator: a RAW's embedded preview is just a
/// JPEG, and it carries the same EXIF orientation as the RAW itself, so both paths need identical
/// decode/orient/resize handling.
/// </summary>
internal static class SkiaThumbnailRenderer
{
    private const int JpegQuality = 85;

    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// For the orientation transforms, which map every source pixel onto exactly one destination
    /// pixel. Filtering an exact permutation can only soften it — and on a small image it visibly
    /// bleeds neighbouring colours together — so turning a picture the right way up is lossless.
    /// </summary>
    private static readonly SKSamplingOptions NoResampling = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>Formats Skia can decode directly. Anything else needs its preview extracting first.</summary>
    private static readonly HashSet<string> DecodableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    public static bool CanDecode(string filePath)
        => DecodableExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// The pixel dimensions of an encoded image, read from its header without decoding it. Null when
    /// it cannot be decoded at all.
    /// </summary>
    public static (int Width, int Height)? ReadSize(Stream source)
    {
        using var codec = SKCodec.Create(source);
        return codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
            ? null
            : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// Renders already-decoded pixels, for the case where the picture came from a RAW develop rather
    /// than an encoded file. The develop has applied the orientation, so there is none to correct.
    /// </summary>
    /// <param name="mask">Where the picture is not, applied after resizing so it costs tile pixels only.</param>
    public static byte[]? Render(
        DecodedImage image, int maxEdgePixels, CancellationToken cancellationToken, TransparencyMask? mask = null)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (image.Pixels.Length < info.BytesSize)
        {
            return null;
        }

        // Wrapped rather than copied: a developed panorama is well over 100 MB, and the resize that
        // follows immediately is what actually needs the memory.
        var pinned = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);

        try
        {
            using var source = new SKBitmap();
            if (!source.InstallPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var resized = ResizeIfNeeded(source, maxEdgePixels);

            // The source wraps the caller's pixels, which may be a develop shared with others, so a
            // mask is only ever applied to a bitmap of our own.
            using var masked = mask is not null && resized is null ? source.Copy() : null;
            var target = resized ?? masked ?? source;

            mask?.Apply(target);
            return Encode(target);
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Returns encoded JPEG bytes, or null when the source cannot be decoded.</summary>
    /// <param name="fallback">
    /// Orientation to use when the image does not state its own, from the file it was extracted from.
    /// Null for an ordinary image file, which is the only authority on which way up it goes.
    /// </param>
    /// <param name="mask">
    /// Where the picture is not. Applied hard: this path renders embedded previews, whose dead space
    /// is painted white, and a soft edge would blend that white into a pale line around the picture.
    /// </param>
    public static byte[]? Render(
        Stream source,
        int maxEdgePixels,
        CancellationToken cancellationToken,
        EmbeddedPreview? fallback = null,
        TransparencyMask? mask = null)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var origin = OriginFor(codec.EncodedOrigin, codec.Info.Width, codec.Info.Height, fallback);

        using var decoded = DecodeDownsampled(codec, maxEdgePixels);
        if (decoded is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Each step hands back null when it has nothing to do, so an image that is already upright
        // and already small enough reaches the encoder without being copied at all. The copies were
        // measured at 2ms a file for a grid tile and 12ms for a full preview, spent on nothing.
        using var oriented = OrientIfNeeded(decoded, origin);
        var upright = oriented ?? decoded;

        using var resized = ResizeIfNeeded(upright, maxEdgePixels);
        mask?.Apply(resized ?? upright, hard: true);
        return Encode(resized ?? upright);
    }

    /// <summary>
    /// Which way up to turn a decoded image, given what it says about itself and what the file it came
    /// out of says about it.
    ///
    /// The image wins whenever it states an origin, so a preview that does carry EXIF is never turned
    /// twice. Only when it is silent — the usual case for a RAW's preview — does the container's
    /// orientation apply.
    /// </summary>
    /// <remarks>
    /// The one trap is a camera that rotates the preview's pixels and then records no EXIF on it: the
    /// picture is already upright and turning it again would lay it on its side. That shows up as the
    /// preview's aspect already disagreeing with the RAW's stored aspect, since a quarter turn is the
    /// only thing that could have exchanged them, so those are left alone. Orientations that do not
    /// swap the axes leave no such trace and are applied as stated.
    /// </remarks>
    internal static SKEncodedOrigin OriginFor(
        SKEncodedOrigin stated, int width, int height, EmbeddedPreview? fallback)
    {
        if (stated is not (SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft))
        {
            return stated;
        }

        // Nothing to do when the container is silent, says the picture is already upright (1), or
        // gives a value outside the standard eight.
        if (fallback?.ExifOrientation is not { } exif || exif is < 2 or > 8)
        {
            return SKEncodedOrigin.Default;
        }

        // Skia numbers its origins exactly as EXIF does, which a test pins rather than assumes.
        var origin = (SKEncodedOrigin)exif;

        if (SwapsAxes(origin) &&
            fallback is { SourceWidth: > 0, SourceHeight: > 0 } &&
            width > 0 && height > 0 &&
            IsPortrait(width, height) != IsPortrait(fallback.SourceWidth.Value, fallback.SourceHeight.Value))
        {
            // Already turned by whoever wrote it.
            return SKEncodedOrigin.Default;
        }

        return origin;
    }

    private static bool IsPortrait(int width, int height) => height > width;

    private static bool SwapsAxes(SKEncodedOrigin origin)
        => origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    private static byte[] Encode(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }

    /// <summary>
    /// Asks the codec for the nearest scale it can decode natively. For JPEG this avoids decoding a
    /// 50MP frame at full resolution just to shrink it, which is the difference between a browsable
    /// grid and a stalled one — and it matters doubly for RAW previews, which are often full-size.
    /// </summary>
    private static SKBitmap? DecodeDownsampled(SKCodec codec, int maxEdgePixels)
    {
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var longestEdge = Math.Max(info.Width, info.Height);
        var scale = Math.Min(1f, (float)maxEdgePixels / longestEdge);
        var supported = codec.GetScaledDimensions(scale);

        // The codec only offers discrete scales (JPEG: eighths), and rounds down — asking for 320px
        // of a 2400px image yields 300px. Since the renderer never upscales, that would quietly
        // produce thumbnails smaller and softer than requested. Step back up until the decode is at
        // least the target size, then resize down to it precisely.
        while (Math.Max(supported.Width, supported.Height) < maxEdgePixels && scale < 1f)
        {
            scale = Math.Min(1f, scale * 2f);
            supported = codec.GetScaledDimensions(scale);
        }

        var decodeInfo = new SKImageInfo(supported.Width, supported.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(decodeInfo);

        var result = codec.GetPixels(decodeInfo, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }

        return bitmap;
    }

    /// <summary>
    /// The image scaled to fit, or null when it already fits and the caller should keep what it has.
    /// </summary>
    private static SKBitmap? ResizeIfNeeded(SKBitmap source, int maxEdgePixels)
    {
        var longestEdge = Math.Max(source.Width, source.Height);
        if (longestEdge <= maxEdgePixels)
        {
            return null;
        }

        var scale = (float)maxEdgePixels / longestEdge;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        return source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), Sampling)
            ?? source.Copy();
    }

    /// <summary>
    /// Shared with the full-size decoder: orientation handling is identical. Null means the image is
    /// already the right way up and the caller should keep the bitmap it already has.
    /// </summary>
    internal static SKBitmap? ApplyOrientationTo(SKBitmap source, SKEncodedOrigin origin)
        => OrientIfNeeded(source, origin);

    /// <summary>
    /// Turns already-decoded pixels the right way up, for a decoder that hands back the image as the
    /// file stores it rather than as it should be seen. Returns the image unchanged when there is
    /// nothing to do, so the common case copies nothing.
    /// </summary>
    internal static DecodedImage Orient(DecodedImage image, int exifOrientation)
    {
        if (exifOrientation is < 2 or > 8)
        {
            return image;
        }

        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (image.Pixels.Length < info.BytesSize)
        {
            return image;
        }

        var pinned = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);

        try
        {
            using var source = new SKBitmap();
            if (!source.InstallPixels(info, pinned.AddrOfPinnedObject(), info.RowBytes))
            {
                return image;
            }

            using var oriented = OrientIfNeeded(source, (SKEncodedOrigin)exifOrientation);
            if (oriented is null)
            {
                return image;
            }

            using var target = new SKBitmap(
                new SKImageInfo(oriented.Width, oriented.Height, SKColorType.Bgra8888, SKAlphaType.Premul));

            if (!oriented.CopyTo(target, SKColorType.Bgra8888))
            {
                return image;
            }

            return image with { Pixels = target.Bytes, Width = target.Width, Height = target.Height };
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// The image turned the right way up, or null when it already is.
    /// </summary>
    private static SKBitmap? OrientIfNeeded(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.Default or SKEncodedOrigin.TopLeft)
        {
            return null;
        }

        float w = source.Width;
        float h = source.Height;

        // Each matrix maps a source pixel to its place in the upright image. Origins 5-8 transpose
        // the axes, so the target bitmap swaps width and height.
        var (matrix, swapsAxes) = origin switch
        {
            SKEncodedOrigin.TopRight => (Affine(-1, 0, w, 0, 1, 0), false),
            SKEncodedOrigin.BottomRight => (Affine(-1, 0, w, 0, -1, h), false),
            SKEncodedOrigin.BottomLeft => (Affine(1, 0, 0, 0, -1, h), false),
            SKEncodedOrigin.LeftTop => (Affine(0, 1, 0, 1, 0, 0), true),
            SKEncodedOrigin.RightTop => (Affine(0, -1, h, 1, 0, 0), true),
            SKEncodedOrigin.RightBottom => (Affine(0, -1, h, -1, 0, w), true),
            SKEncodedOrigin.LeftBottom => (Affine(0, 1, 0, -1, 0, w), true),
            _ => (SKMatrix.Identity, false)
        };

        var targetWidth = swapsAxes ? source.Height : source.Width;
        var targetHeight = swapsAxes ? source.Width : source.Height;

        var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(target);
        using var image = SKImage.FromBitmap(source);
        canvas.SetMatrix(matrix);
        canvas.DrawImage(image, 0, 0, NoResampling);
        canvas.Flush();
        return target;
    }

    private static SKMatrix Affine(float scaleX, float skewX, float transX, float skewY, float scaleY, float transY)
        => new(scaleX, skewX, transX, skewY, scaleY, transY, 0, 0, 1);
}
