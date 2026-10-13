using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amberglass.Preview.Images;

/// <summary>Decodes a bare JPEG XL codestream, as a DNG stores one per tile.</summary>
public interface IJpegXlDecoder
{
    bool IsAvailable { get; }

    /// <summary>
    /// One channel at 8 bits, <paramref name="width"/> × <paramref name="height"/>, or null if the
    /// data could not be decoded or is larger than it says it should be.
    /// </summary>
    byte[]? DecodeGrey(byte[] codestream, int width, int height);

    /// <summary>
    /// Colour as premultiplied BGRA, four bytes a pixel, <paramref name="width"/> × <paramref name="height"/>,
    /// or null on the same terms as <see cref="DecodeGrey"/>.
    /// </summary>
    byte[]? DecodeBgra(byte[] codestream, int width, int height);
}

/// <summary>
/// JPEG XL through macOS ImageIO, which has read it since macOS 14. The same reason ImageIO develops
/// these panoramas in the first place: LibRaw as usually built has no JPEG XL, and nothing else here
/// does either.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class ImageIoJpegXlDecoder : IJpegXlDecoder
{
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kCGImageAlphaNone: one grey byte a pixel and nothing else.</summary>
    private const uint BitmapInfoGrey = 0;

    /// <summary>BGRA, premultiplied, little-endian — what the rest of the pipeline works in.</summary>
    private const uint BitmapInfoBgraPremultiplied = 2 | (2u << 12);

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public byte[]? DecodeGrey(byte[] codestream, int width, int height) => Decode(codestream, width, height, colour: false);

    public byte[]? DecodeBgra(byte[] codestream, int width, int height) => Decode(codestream, width, height, colour: true);

    private unsafe byte[]? Decode(byte[] codestream, int width, int height, bool colour)
    {
        if (codestream.Length == 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var data = IntPtr.Zero;
        var source = IntPtr.Zero;
        var image = IntPtr.Zero;
        var colorSpace = IntPtr.Zero;
        var context = IntPtr.Zero;

        try
        {
            fixed (byte* bytes = codestream)
            {
                data = CFDataCreate(IntPtr.Zero, (IntPtr)bytes, codestream.Length);
            }

            source = data == IntPtr.Zero ? IntPtr.Zero : CGImageSourceCreateWithData(data, IntPtr.Zero);
            image = source == IntPtr.Zero ? IntPtr.Zero : CGImageSourceCreateImageAtIndex(source, 0, IntPtr.Zero);
            if (image == IntPtr.Zero)
            {
                return null;
            }

            var decodedWidth = (int)CGImageGetWidth(image);
            var decodedHeight = (int)CGImageGetHeight(image);
            if (decodedWidth <= 0 || decodedHeight <= 0 || decodedWidth > width || decodedHeight > height)
            {
                return null;
            }

            // Drawn into a buffer of the tile's full size, so a tile coded smaller than its slot —
            // which an encoder is entitled to do at the image's edge — lands at its top-left with
            // the rest left empty, exactly as the caller indexes it. Core Graphics' origin is the
            // bottom-left, hence the offset.
            // Rows aligned the way Core Graphics wants them, as the RAW decoder learned the hard way,
            // then copied out tightly.
            var bytesPerPixel = colour ? 4 : 1;
            var stride = ImageIoRawDecoder.AlignStride(width * bytesPerPixel);
            var aligned = new byte[stride * height];
            colorSpace = colour ? CGColorSpaceCreateDeviceRGB() : CGColorSpaceCreateDeviceGray();

            fixed (byte* buffer = aligned)
            {
                context = CGBitmapContextCreate(
                    (IntPtr)buffer, width, height, 8, stride, colorSpace, colour ? BitmapInfoBgraPremultiplied : BitmapInfoGrey);
                if (context == IntPtr.Zero)
                {
                    return null;
                }

                CGContextDrawImage(
                    context, new CGRect(0, height - decodedHeight, decodedWidth, decodedHeight), image);
            }

            var rowBytes = width * bytesPerPixel;
            var pixels = new byte[rowBytes * height];
            for (var row = 0; row < height; row++)
            {
                aligned.AsSpan(row * stride, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes, rowBytes));
            }

            return pixels;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                CGContextRelease(context);
            }

            if (colorSpace != IntPtr.Zero)
            {
                CGColorSpaceRelease(colorSpace);
            }

            if (image != IntPtr.Zero)
            {
                CGImageRelease(image);
            }

            if (source != IntPtr.Zero)
            {
                CFRelease(source);
            }

            if (data != IntPtr.Zero)
            {
                CFRelease(data);
            }
        }
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
    private static extern IntPtr CFDataCreate(IntPtr allocator, IntPtr bytes, nint length);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

    [DllImport(ImageIO)]
    private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nint index, IntPtr options);

    [DllImport(CoreGraphics)]
    private static extern nint CGImageGetWidth(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern nint CGImageGetHeight(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern void CGImageRelease(IntPtr image);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGColorSpaceCreateDeviceGray();

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
