using SkiaSharp;

namespace Amberglass.Preview.Images;

/// <summary>
/// Which parts of a picture are really there: 255 is picture, 0 is nothing, and values between are
/// the soft edge where the two meet. Stored the way the file stores it — unrotated, at whatever
/// resolution the mask was written — and mapped onto the upright, resized pixels as it is applied.
///
/// A stitched panorama is the case this exists for. Its corners are empty, and what the file
/// carries there is not empty: Lightroom fills the dead space with the edge pixels smeared outwards
/// (which compresses well and looks like a fault), and paints its embedded preview's dead space
/// white. Neither is part of the photograph, and both are loud next to it.
/// </summary>
/// <param name="Orientation">The file's EXIF orientation, 1 to 8, so the mask turns with the picture.</param>
public sealed record TransparencyMask(byte[] Values, int Width, int Height, int Orientation = 1)
{
    /// <summary>
    /// How far the aspect ratios may disagree before the mask is judged not to belong to the
    /// picture. Masks at reduced resolution round their dimensions, so an exact match is too strict;
    /// anything further out than this is a different picture, and clearing pixels by the wrong mask
    /// would punch holes in a photograph.
    /// </summary>
    private const double AspectTolerance = 0.02;

    private bool SwapsAxes => Orientation is >= 5 and <= 8;

    /// <summary>
    /// Clears the dead space in premultiplied 32-bit pixels, in place. Every channel is scaled, so
    /// what was nothing becomes transparent black — which is see-through in the viewer and plain
    /// black once encoded as a JPEG thumbnail.
    /// </summary>
    /// <param name="hard">
    /// Treat any partly-covered pixel as empty. For a source whose dead space is painted a flat
    /// colour — an embedded preview's white — where the soft edge would blend that colour into a
    /// visible pale line. A one-pixel erosion is invisible; the line is not.
    /// </param>
    /// <returns>False when the mask does not fit the picture, which is then left untouched.</returns>
    public bool Apply(Span<byte> pixels, int width, int height, int rowBytes, bool hard = false)
    {
        if (width <= 0 || height <= 0 || !Fits(width, height) || pixels.Length < (long)rowBytes * (height - 1) + (width * 4))
        {
            return false;
        }

        // Fast path: the mask is the picture's own size and the right way up, which is the usual
        // full-resolution case — 47 million pixels is no place for interpolation that does nothing.
        if (Orientation == 1 && Width == width && Height == height)
        {
            for (var y = 0; y < height; y++)
            {
                var row = pixels.Slice(y * rowBytes, width * 4);
                var maskRow = Values.AsSpan(y * Width, Width);

                for (var x = 0; x < width; x++)
                {
                    Cover(row.Slice(x * 4, 4), maskRow[x], hard);
                }
            }

            return true;
        }

        // Each destination axis maps onto one mask axis, flipped or not, so the coordinates can be
        // worked out once per row and once per column rather than once per pixel.
        var (flipColumns, flipRows) = Flips();
        var columns = Axis(width, flipColumns);
        var rows = Axis(height, flipRows);

        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width * 4);

            for (var x = 0; x < width; x++)
            {
                var (u, v) = SwapsAxes ? (rows[y], columns[x]) : (columns[x], rows[y]);
                Cover(row.Slice(x * 4, 4), Sample(u, v), hard);
            }
        }

        return true;
    }

    /// <summary>The same, for a Skia bitmap with four bytes a pixel.</summary>
    public unsafe bool Apply(SKBitmap bitmap, bool hard = false)
    {
        if (bitmap.BytesPerPixel != 4 || bitmap.GetPixels() == IntPtr.Zero)
        {
            return false;
        }

        var span = new Span<byte>((void*)bitmap.GetPixels(), checked(bitmap.RowBytes * bitmap.Height));
        var applied = Apply(span, bitmap.Width, bitmap.Height, bitmap.RowBytes, hard);

        if (applied)
        {
            bitmap.NotifyPixelsChanged();
        }

        return applied;
    }

    /// <summary>Whether a picture of this size, upright, could be the one this mask was made for.</summary>
    internal bool Fits(int width, int height)
    {
        var (storedWidth, storedHeight) = SwapsAxes ? (height, width) : (width, height);
        var picture = storedWidth / (double)storedHeight;
        var mask = Width / (double)Height;

        return Math.Abs((picture / mask) - 1) <= AspectTolerance;
    }

    /// <summary>
    /// Whether the destination's columns (first) and rows (second) run backwards across the mask —
    /// which, for the orientations that swap the axes, means across the mask's other axis.
    /// </summary>
    private (bool Columns, bool Rows) Flips() => Orientation switch
    {
        2 => (true, false),
        3 => (true, true),
        4 => (false, true),
        5 => (false, false),
        6 => (true, false),
        7 => (true, true),
        8 => (false, true),
        _ => (false, false)
    };

    /// <summary>Pixel centres along one destination axis, as 0–1 across the mask.</summary>
    private static float[] Axis(int length, bool flip)
    {
        var coordinates = new float[length];
        for (var i = 0; i < length; i++)
        {
            var t = (i + 0.5f) / length;
            coordinates[i] = flip ? 1 - t : t;
        }

        return coordinates;
    }

    /// <summary>Bilinear, so a mask shrunk to a thumbnail keeps a smooth edge rather than a staircase.</summary>
    private byte Sample(float u, float v)
    {
        var fx = (u * Width) - 0.5f;
        var fy = (v * Height) - 0.5f;

        var x0 = Math.Clamp((int)MathF.Floor(fx), 0, Width - 1);
        var y0 = Math.Clamp((int)MathF.Floor(fy), 0, Height - 1);
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var wx = Math.Clamp(fx - x0, 0, 1);
        var wy = Math.Clamp(fy - y0, 0, 1);

        var top = Values[(y0 * Width) + x0] + ((Values[(y0 * Width) + x1] - Values[(y0 * Width) + x0]) * wx);
        var bottom = Values[(y1 * Width) + x0] + ((Values[(y1 * Width) + x1] - Values[(y1 * Width) + x0]) * wx);

        return (byte)Math.Clamp(MathF.Round(top + ((bottom - top) * wy)), 0, 255);
    }

    private static void Cover(Span<byte> pixel, byte coverage, bool hard)
    {
        if (coverage == 255)
        {
            return;
        }

        if (coverage == 0 || hard)
        {
            pixel.Clear();
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            pixel[i] = (byte)(((pixel[i] * coverage) + 127) / 255);
        }
    }
}
