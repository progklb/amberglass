using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BetterDAM.Core.Interfaces;
using BetterDAM.Core.Models;
using Microsoft.Extensions.Logging;

namespace BetterDAM.Preview.Images;

/// <summary>
/// Develops RAW files with macOS ImageIO.
///
/// A complement to LibRaw rather than a replacement, because the two fail on different files:
/// ImageIO cannot decode a Fujifilm X-S20 RAF at all, while LibRaw as Homebrew builds it cannot
/// unpack a JPEG XL compressed DNG — the format Lightroom writes for stitched panoramas — because
/// that needs libjxl linked in. Between them they cover both.
///
/// It renders the file its own way and takes no settings, so anything it produces is Apple's
/// interpretation rather than the develop panel's.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class ImageIoRawDecoder : IRawDecoder
{
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>BGRA, premultiplied, little-endian — what the UI blits without conversion.</summary>
    private const uint BitmapInfoBgraPremultiplied = 2 | (2u << 12);

    /// <summary>
    /// Matches the still decoder's ceiling, about 80MP. ImageIO scales while drawing, so an
    /// oversized file costs a smaller destination rather than a refusal.
    /// </summary>
    private const long MaxPixels = 80_000_000;

    private readonly ILogger<ImageIoRawDecoder> _logger;

    public ImageIoRawDecoder(ILogger<ImageIoRawDecoder> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public Task<DecodedImage?> DevelopAsync(MediaFile file, CancellationToken cancellationToken = default)
        => Task.Run(() => Develop(file), cancellationToken);

    private unsafe DecodedImage? Develop(MediaFile file)
    {
        var path = IntPtr.Zero;
        var url = IntPtr.Zero;
        var source = IntPtr.Zero;
        var image = IntPtr.Zero;
        var colorSpace = IntPtr.Zero;
        var context = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            path = CFStringCreateWithCString(IntPtr.Zero, file.FullPath, 0x08000100 /* UTF-8 */);
            if (path == IntPtr.Zero)
            {
                return null;
            }

            url = CFURLCreateWithFileSystemPath(IntPtr.Zero, path, 0 /* POSIX */, false);
            source = CGImageSourceCreateWithURL(url, IntPtr.Zero);
            if (source == IntPtr.Zero)
            {
                return null;
            }

            image = CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
            if (image == IntPtr.Zero)
            {
                // Recognised but undecodable: an unsupported camera, which is the usual case here.
                return null;
            }

            var sourceWidth = (int)CGImageGetWidth(image);
            var sourceHeight = (int)CGImageGetHeight(image);
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return null;
            }

            var (width, height) = FitWithinBudget(sourceWidth, sourceHeight);

            // Core Graphics wants its rows aligned, and a width that does not divide evenly leaves
            // it writing on a different pitch to the one asked for — which shows up as coloured
            // streaks down the edges of the picture rather than as an error. So the context gets a
            // buffer on its own terms, and the rows are copied out tightly afterwards.
            var stride = AlignStride(width * 4);

            // Zeroed, not merely allocated. A stitched panorama has ragged transparent edges, and
            // drawing leaves whatever was already in the buffer showing through them — which looks
            // like coloured streaks down the sides of the picture, not like a memory bug.
            buffer = (IntPtr)NativeMemory.AllocZeroed((nuint)((long)stride * height));

            colorSpace = CGColorSpaceCreateDeviceRGB();
            context = CGBitmapContextCreate(
                buffer, width, height, 8, stride, colorSpace, BitmapInfoBgraPremultiplied);

            if (context == IntPtr.Zero)
            {
                return null;
            }

            // Drawing into a smaller rect is how the budget is applied: ImageIO scales as it renders
            // rather than producing everything and shrinking it afterwards.
            CGContextDrawImage(context, new CGRect(0, 0, width, height), image);

            var pixels = new byte[(long)width * height * 4];
            var rowBytes = width * 4;

            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(buffer + ((nint)row * stride), pixels, row * rowBytes, rowBytes);
            }

            // ImageIO hands back the image as the file stores it, with the orientation left as a
            // property rather than applied, so a frame shot in portrait arrives on its side.
            return SkiaThumbnailRenderer.Orient(
                new DecodedImage(pixels, width, height, DecodedImage.Platform), ReadOrientation(source));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageIO could not render {File}", file.FullPath);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NativeMemory.Free((void*)buffer);
            }

            Release(context, CGContextRelease);
            Release(colorSpace, CGColorSpaceRelease);
            Release(image, CGImageRelease);
            Release(source, CFRelease);
            Release(url, CFRelease);
            Release(path, CFRelease);
        }
    }

    private static void Release(IntPtr handle, Action<IntPtr> release)
    {
        if (handle != IntPtr.Zero)
        {
            release(handle);
        }
    }

    /// <summary>
    /// Rounds a row up to a 64-byte boundary, which is what Core Graphics wants for its fast paths
    /// and what it quietly assumes on some of them.
    /// </summary>
    internal static int AlignStride(int rowBytes) => (rowBytes + 63) / 64 * 64;

    /// <summary>Scales the destination down when the source is larger than the pixel budget.</summary>
    internal static (int Width, int Height) FitWithinBudget(int width, int height)
    {
        var pixels = (long)width * height;
        if (pixels <= MaxPixels)
        {
            return (width, height);
        }

        var scale = Math.Sqrt(MaxPixels / (double)pixels);
        return (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFURLCreateWithFileSystemPath(IntPtr allocator, IntPtr path, nint pathStyle, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nint index, IntPtr options);

    /// <summary>
    /// The file's EXIF orientation as ImageIO reports it, 1 to 8, or 1 when it says nothing.
    ///
    /// Read from the image source's properties rather than with ExifTool: it is already open, and a
    /// decoder that needed a second tool to know which way up its own output goes would be the wrong
    /// shape. The key is looked up by content — CFDictionary compares keys with CFEqual, so a string
    /// made here matches ImageIO's own constant without having to resolve the symbol.
    /// </summary>
    private static int ReadOrientation(IntPtr source)
    {
        var properties = IntPtr.Zero;
        var key = IntPtr.Zero;

        try
        {
            properties = CGImageSourceCopyPropertiesAtIndex(source, 0, IntPtr.Zero);
            if (properties == IntPtr.Zero)
            {
                return 1;
            }

            key = CFStringCreateWithCString(IntPtr.Zero, "Orientation", 0x08000100 /* UTF-8 */);
            if (key == IntPtr.Zero)
            {
                return 1;
            }

            var value = CFDictionaryGetValue(properties, key);
            if (value == IntPtr.Zero)
            {
                return 1;
            }

            return CFNumberGetValue(value, 9 /* kCFNumberIntType */, out var orientation) && orientation is >= 1 and <= 8
                ? orientation
                : 1;
        }
        finally
        {
            Release(key, CFRelease);
            Release(properties, CFRelease);
        }
    }

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr source, nint index, IntPtr options);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFNumberGetValue(IntPtr number, nint type, out int value);

    [DllImport(CoreGraphics)]
    private static extern nint CGImageGetWidth(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern nint CGImageGetHeight(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern void CGImageRelease(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(CoreGraphics)]
    private static extern void CGColorSpaceRelease(IntPtr space);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGBitmapContextCreate(IntPtr data, nint width, nint height, nint bitsPerComponent, nint bytesPerRow, IntPtr space, uint bitmapInfo);

    [DllImport(CoreGraphics)]
    private static extern void CGContextRelease(IntPtr context);

    [DllImport(CoreGraphics)]
    private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
}
