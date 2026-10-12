using Avalonia;

namespace Amberglass.UI.Controls;

/// <summary>
/// The zoom and pan arithmetic, kept apart from the control that hosts it so it can be reasoned
/// about and tested without a window.
///
/// Scale is in real terms: 1 means one content pixel per screen pixel, which is what "quality
/// inspection" needs to mean something. Fitting is therefore a computed scale rather than a
/// separate mode.
/// </summary>
public sealed class ZoomState
{
    /// <summary>
    /// Below this, inspecting is pointless and the content is a speck; above it, one source pixel
    /// covers a large block of screen and there is nothing further to see.
    /// </summary>
    public const double MinScale = 0.05;
    public const double MaxScale = 32;

    /// <summary>One wheel notch. Multiplicative so each step feels the same at any magnification.</summary>
    public const double WheelStep = 1.15;

    public Size Content { get; private set; }

    public Size Viewport { get; private set; }

    public double Scale { get; private set; } = 1;

    /// <summary>Top-left of the content in viewport coordinates.</summary>
    public Point Offset { get; private set; }

    public bool HasContent => Content.Width > 0 && Content.Height > 0
                              && Viewport.Width > 0 && Viewport.Height > 0;

    /// <summary>The scale at which the whole of the content is visible.</summary>
    public double FitScale => HasContent
        ? Math.Min(Viewport.Width / Content.Width, Viewport.Height / Content.Height)
        : 1;

    /// <summary>True when showing everything, which is when panning should be disabled.</summary>
    public bool IsFitted => Math.Abs(Scale - FitScale) < 0.0001;

    public Size ScaledContent => new(Content.Width * Scale, Content.Height * Scale);

    /// <summary>
    /// Points the view at new content while keeping the same part of the picture in view.
    ///
    /// The content changing does not always mean a different photograph: re-developing a RAW, or
    /// switching between the developed file and its embedded JPEG, produces a different number of
    /// pixels showing the same scene. Refitting there would throw away the region being examined,
    /// which is the whole reason for having zoomed in. A genuinely new image is fitted by the
    /// caller, which is the only thing that knows the difference.
    /// </summary>
    public void SetContent(Size content, Size viewport)
    {
        var hadContent = HasContent;
        var resized = !Content.NearlyEquals(content);

        if (!hadContent)
        {
            Content = content;
            Viewport = viewport;
            Fit();
            return;
        }

        if (!resized)
        {
            Viewport = viewport;
            Offset = ClampOffset(Offset);
            return;
        }

        // Where the middle of the view sits within the picture, and how far zoomed in relative to
        // fitting — both proportions, so they survive a change of resolution.
        var centre = new Point(Viewport.Width / 2, Viewport.Height / 2);
        var relativeX = (centre.X - Offset.X) / Scale / Content.Width;
        var relativeY = (centre.Y - Offset.Y) / Scale / Content.Height;
        var relativeScale = Scale / FitScale;

        Content = content;
        Viewport = viewport;

        Scale = Clamp(FitScale * relativeScale);
        Offset = ClampOffset(new Point(
            centre.X - (relativeX * Content.Width * Scale),
            centre.Y - (relativeY * Content.Height * Scale)));
    }

    public void Fit()
    {
        Scale = Clamp(FitScale);
        Offset = Centre();
    }

    /// <summary>Jumps to one content pixel per screen pixel, keeping the middle of the view fixed.</summary>
    public void ActualSize() => ZoomTo(1, new Point(Viewport.Width / 2, Viewport.Height / 2));

    /// <summary>
    /// Zooms by <paramref name="factor"/> about <paramref name="anchor"/>, a point in viewport
    /// coordinates. Anchoring on the pointer is what makes wheel zoom feel like it is pulling the
    /// image rather than scrolling past it.
    /// </summary>
    public void ZoomBy(double factor, Point anchor) => ZoomTo(Scale * factor, anchor);

    public void ZoomTo(double scale, Point anchor)
    {
        var target = Clamp(scale);
        if (Math.Abs(target - Scale) < double.Epsilon)
        {
            return;
        }

        // Where the anchor sits within the content, before the change.
        var contentX = (anchor.X - Offset.X) / Scale;
        var contentY = (anchor.Y - Offset.Y) / Scale;

        Scale = target;

        // Put that same content point back under the anchor.
        Offset = ClampOffset(new Point(anchor.X - (contentX * Scale), anchor.Y - (contentY * Scale)));
    }

    public void PanBy(Vector delta) => Offset = ClampOffset(Offset + delta);

    /// <summary>
    /// Keeps the content sensibly placed: centred on any axis where it is smaller than the
    /// viewport, and otherwise prevented from being dragged past its own edges — so it can never be
    /// flung off screen and lost.
    /// </summary>
    private Point ClampOffset(Point offset)
    {
        var scaled = ScaledContent;

        var x = scaled.Width <= Viewport.Width
            ? (Viewport.Width - scaled.Width) / 2
            : Math.Clamp(offset.X, Viewport.Width - scaled.Width, 0);

        var y = scaled.Height <= Viewport.Height
            ? (Viewport.Height - scaled.Height) / 2
            : Math.Clamp(offset.Y, Viewport.Height - scaled.Height, 0);

        return new Point(x, y);
    }

    private Point Centre()
    {
        var scaled = ScaledContent;
        return new Point((Viewport.Width - scaled.Width) / 2, (Viewport.Height - scaled.Height) / 2);
    }

    private static double Clamp(double scale) => Math.Clamp(scale, MinScale, MaxScale);
}

internal static class SizeExtensions
{
    public static bool NearlyEquals(this Size a, Size b)
        => Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
}
