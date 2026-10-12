using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Amberglass.Core.Models;

namespace Amberglass.UI.Controls;

/// <summary>
/// Displays decoded video frames.
///
/// Frames arrive as raw BGRA several times a second, so they are blitted into a single reused
/// <see cref="WriteableBitmap"/> rather than allocating a bitmap per frame. Presenting is a copy
/// plus an invalidate; nothing here decodes or paces.
/// </summary>
public sealed class VideoSurface : Control
{
    private WriteableBitmap? _bitmap;
    private PixelSize _size;

    /// <summary>
    /// The decoded frame size, or empty before the first frame. Fullscreen needs it to know what
    /// 100% means; it is the decoded size, which is not necessarily the source's.
    /// </summary>
    public Size FrameSize => new(_size.Width, _size.Height);

    /// <summary>Copies a frame into the backing bitmap. Must be called on the UI thread.</summary>
    public void Present(VideoFrame frame)
    {
        EnsureBitmap(frame.Width, frame.Height);

        using (var locked = _bitmap!.Lock())
        {
            var sourceStride = frame.Width * 4;

            if (locked.RowBytes == sourceStride)
            {
                Marshal.Copy(frame.Buffer, 0, locked.Address, frame.Length);
            }
            else
            {
                // The bitmap's rows can be padded for alignment, so copy row by row rather than
                // assuming a contiguous match.
                for (var row = 0; row < frame.Height; row++)
                {
                    Marshal.Copy(
                        frame.Buffer,
                        row * sourceStride,
                        locked.Address + (row * locked.RowBytes),
                        sourceStride);
                }
            }
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _size = default;
        InvalidateVisual();
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null && _size.Width == width && _size.Height == height)
        {
            return;
        }

        _bitmap?.Dispose();
        _size = new PixelSize(width, height);
        _bitmap = new WriteableBitmap(_size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_bitmap is null)
        {
            return;
        }

        var source = new Rect(_bitmap.Size);
        context.DrawImage(_bitmap, source, FitCentred(_bitmap.Size, Bounds.Size));
    }

    /// <summary>Uniform fit, centred — the same letterboxing the still preview uses.</summary>
    private static Rect FitCentred(Size content, Size available)
    {
        if (content.Width <= 0 || content.Height <= 0 || available.Width <= 0 || available.Height <= 0)
        {
            return default;
        }

        var scale = Math.Min(available.Width / content.Width, available.Height / content.Height);
        var width = content.Width * scale;
        var height = content.Height * scale;

        return new Rect((available.Width - width) / 2, (available.Height - height) / 2, width, height);
    }
}
