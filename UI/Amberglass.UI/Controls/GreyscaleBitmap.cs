using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Amberglass.Core.Services;

namespace Amberglass.UI.Controls;

/// <summary>
/// Makes a grey copy of a decoded bitmap for the viewer's black-and-white preview.
///
/// A copy rather than a draw-time effect because Avalonia offers no colour filter: its only built-in
/// effects are blur and drop shadow, so there is nothing to hang a saturation matrix on without
/// replacing the Image with a custom-drawn control and reimplementing the sizing the zoom viewer
/// depends on. Converting once per photograph and keeping both is the smaller change, and toggling
/// afterwards is then free.
/// </summary>
public static class GreyscaleBitmap
{
    /// <summary>
    /// Returns a grey version of <paramref name="source"/>, or null if it cannot be made.
    ///
    /// The caller owns the result and should dispose it when the picture changes.
    /// </summary>
    public static Bitmap? From(Bitmap? source)
    {
        if (source is null)
        {
            return null;
        }

        var target = new WriteableBitmap(
            source.PixelSize,
            source.Dpi,
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        try
        {
            using var frame = target.Lock();

            // Converts into the framebuffer's format whatever the source's own happens to be, which
            // is why the copy goes through here rather than reading the source's pixels directly.
            source.CopyPixels(frame, AlphaFormat.Premul);

            // A row at a time: a full-frame managed buffer for a large RAW is around a hundred
            // megabytes, and this runs while the picture is already on screen.
            var row = new byte[frame.RowBytes];

            for (var y = 0; y < frame.Size.Height; y++)
            {
                var line = frame.Address + (y * frame.RowBytes);

                Marshal.Copy(line, row, 0, row.Length);
                Greyscale.GreyRowBgra(row);
                Marshal.Copy(row, 0, line, row.Length);
            }
        }
        catch (Exception)
        {
            // A preview that cannot be greyed is not worth failing the viewer for; the colour one
            // stays on screen.
            target.Dispose();
            return null;
        }

        return target;
    }
}
