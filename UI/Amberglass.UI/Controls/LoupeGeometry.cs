using Avalonia;

namespace Amberglass.UI.Controls;

/// <summary>
/// The arithmetic behind the loupe, kept apart from the control so it can be tested without a window
/// — the same split as <see cref="ZoomState"/>.
///
/// Three separate questions, and getting any of them slightly wrong shows up as the magnified region
/// not matching what is under the cursor, which is the one thing a loupe must get right:
/// where the preview actually sits inside its pane, where in the picture the pointer is, and how far
/// to slide the source to bring that spot to the middle of the loupe.
/// </summary>
internal static class LoupeGeometry
{
    /// <summary>
    /// Where uniformly-stretched content ends up inside a viewport. The preview is letterboxed —
    /// a panorama in a squarish pane leaves large empty margins — so pointer coordinates cannot be
    /// mapped against the pane's own bounds.
    /// </summary>
    public static Rect FitRect(Size content, Size viewport)
    {
        if (content.Width <= 0 || content.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return default;
        }

        var scale = Math.Min(viewport.Width / content.Width, viewport.Height / content.Height);
        var width = content.Width * scale;
        var height = content.Height * scale;

        return new Rect((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    /// <summary>
    /// Where <paramref name="pointer"/> falls within the picture, as a fraction of its width and
    /// height. Null when the pointer is over the letterbox rather than the image: there is nothing
    /// to magnify there, and the loupe should not appear.
    ///
    /// A fraction rather than a pixel coordinate so the answer stays valid when the source changes
    /// resolution underneath it — which happens the moment a RAW develop lands mid-press.
    /// </summary>
    public static Point? ToRelative(Point pointer, Size content, Size viewport)
    {
        var fit = FitRect(content, viewport);
        if (fit.Width <= 0 || fit.Height <= 0 || !fit.Contains(pointer))
        {
            return null;
        }

        return new Point((pointer.X - fit.X) / fit.Width, (pointer.Y - fit.Y) / fit.Height);
    }

    /// <summary>
    /// Top-left at which to draw <paramref name="source"/> inside a loupe of
    /// <paramref name="loupe"/>, at one source pixel per screen pixel, so that
    /// <paramref name="relative"/> lands in the middle.
    ///
    /// Clamped so the loupe stays full of picture near the edges — it slides its view rather than
    /// showing a band of empty space, which is what Bridge does and what makes checking a corner for
    /// focus possible at all. Content smaller than the loupe in an axis is centred instead.
    /// </summary>
    public static Point SourceOffset(Point relative, Size source, Size loupe)
    {
        return new Point(
            OffsetOnAxis(relative.X, source.Width, loupe.Width),
            OffsetOnAxis(relative.Y, source.Height, loupe.Height));
    }

    private static double OffsetOnAxis(double relative, double source, double loupe)
    {
        if (source <= loupe)
        {
            return (loupe - source) / 2;
        }

        // Negative: the source is wider than the window onto it, so it is pulled left/up.
        var centred = (loupe / 2) - (relative * source);
        return Math.Clamp(centred, loupe - source, 0);
    }

    /// <summary>
    /// How many source pixels the loupe should span, given its size in device-independent pixels and
    /// the display's render scaling.
    ///
    /// The distinction is the whole difference between a loupe that inspects and one that does not.
    /// Drawing coordinates are in DIPs, so a 340 DIP loupe covers 680 physical pixels on a Retina
    /// display; filling it with 340 source pixels magnifies them 2× and resamples, which turns a
    /// JPEG's compression blocks into visible mush and softens everything else. Taking 680 source
    /// pixels instead puts one image pixel on one physical pixel — no resampling at all, and the same
    /// thing "100%" means in Photoshop or Lightroom.
    /// </summary>
    public static Size SourceWindow(Size loupeBounds, double renderScaling)
        => SourceWindow(loupeBounds, renderScaling, sourceWidth: 1, targetWidth: 1);

    /// <summary>
    /// The same, but expressed in the pixels of a source that may not be the one the magnification is
    /// defined against.
    ///
    /// <paramref name="targetWidth"/> is the width 100% means — the developed RAW. While that is still
    /// being decoded the loupe is drawing the embedded preview, which is a quarter of the size, and
    /// filling the loupe with preview pixels at 1:1 would show the picture four times smaller. The
    /// magnification would then visibly jump the moment the develop landed, throwing away the spot
    /// being examined.
    ///
    /// So the window is scaled into the source's own pixels: fewer of them, stretched, which is soft
    /// but is the *same size* as what replaces it. The develop then sharpens the picture without
    /// moving it.
    /// </summary>
    public static Size SourceWindow(Size loupeBounds, double renderScaling, double sourceWidth, double targetWidth)
    {
        var scaling = renderScaling > 0 ? renderScaling : 1;
        var ratio = sourceWidth > 0 && targetWidth > 0 ? sourceWidth / targetWidth : 1;

        return new Size(loupeBounds.Width * scaling * ratio, loupeBounds.Height * scaling * ratio);
    }

    /// <summary>
    /// How much bigger a feature looks in the loupe than in the pane.
    ///
    /// Measured against <paramref name="targetWidth"/> — the resolution 100% is defined by — rather
    /// than against whichever bitmap is currently loaded, so the figure does not change when a
    /// develop lands. Both scales are in DIPs per image pixel, so the render scaling divides out of
    /// the loupe's side: at 100% on a 2× display each image pixel occupies half a DIP.
    /// </summary>
    public static double Magnification(double targetWidth, Size content, Size viewport, double renderScaling)
    {
        var fit = FitRect(content, viewport);
        if (fit.Width <= 0 || targetWidth <= 0)
        {
            return 1;
        }

        var scaling = renderScaling > 0 ? renderScaling : 1;
        return targetWidth / fit.Width / scaling;
    }
}
