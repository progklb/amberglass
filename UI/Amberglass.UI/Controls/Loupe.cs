using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Amberglass.UI.Controls;

/// <summary>
/// A magnifier over the inline preview: press and hold to open a window onto the picture at one
/// source pixel per screen pixel, drag to move it, release to dismiss.
///
/// Drawn rather than composed from an <see cref="Image"/> and a transform. The whole control is one
/// blit of a rectangle out of the source, so doing it directly is both simpler than persuading the
/// layout system not to scale anything and free of the rounding that would put the magnified region
/// a pixel or two away from the cursor.
///
/// It renders whatever bitmap it is given at 1:1 and does not care where that came from. That is what
/// lets the full-resolution decode arrive mid-press and simply sharpen what is already on screen:
/// the position is held as a fraction of the picture, so it survives the change of resolution.
/// </summary>
public sealed class Loupe : Control
{
    /// <summary>
    /// Big enough to judge focus, small enough to leave the picture around it visible — the point is
    /// to inspect a detail in context, not to take the pane over.
    /// </summary>
    public const double DefaultSize = 340;

    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<Loupe, IImage?>(nameof(Source));

    /// <summary>Where in the picture to centre, as a fraction of its width and height.</summary>
    public static readonly StyledProperty<Point> RelativeProperty =
        AvaloniaProperty.Register<Loupe, Point>(nameof(Relative), new Point(0.5, 0.5));

    /// <summary>Corner badge — the magnification, and whether it is of the full-resolution decode.</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<Loupe, string?>(nameof(Caption));

    /// <summary>Set from the hand-drawn selection setting, like every other pencil in the app.</summary>
    public static readonly StyledProperty<bool> IsHandDrawnProperty =
        AvaloniaProperty.Register<Loupe, bool>(nameof(IsHandDrawn));

    public static readonly StyledProperty<double> RoughnessProperty =
        AvaloniaProperty.Register<Loupe, double>(nameof(Roughness), 1.0);

    public bool IsHandDrawn
    {
        get => GetValue(IsHandDrawnProperty);
        set => SetValue(IsHandDrawnProperty, value);
    }

    public double Roughness
    {
        get => GetValue(RoughnessProperty);
        set => SetValue(RoughnessProperty, value);
    }

    /// <summary>
    /// The pixel width that 100% refers to. Zero means the source's own width, which is right for
    /// anything already at its final resolution.
    /// </summary>
    public static readonly StyledProperty<double> TargetWidthProperty =
        AvaloniaProperty.Register<Loupe, double>(nameof(TargetWidth));

    /// <summary>True while pinned by Inspect rather than held open by the pointer.</summary>
    public static readonly StyledProperty<bool> IsPinnedProperty =
        AvaloniaProperty.Register<Loupe, bool>(nameof(IsPinned));

    private static readonly IBrush Backdrop = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
    private static readonly IPen Frame = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));
    private static readonly IBrush BadgeBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
    private static readonly IBrush BadgeText = Brushes.White;

    static Loupe()
    {
        AffectsRender<Loupe>(
            SourceProperty, RelativeProperty, CaptionProperty, TargetWidthProperty, IsPinnedProperty,
            IsHandDrawnProperty, RoughnessProperty);
    }

    public Loupe()
    {
        Width = DefaultSize;
        Height = DefaultSize;

        // The pointer is already captured by the preview; a loupe that could take it would break the
        // drag the moment it slid under the cursor.
        IsHitTestVisible = false;
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Point Relative
    {
        get => GetValue(RelativeProperty);
        set => SetValue(RelativeProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public double TargetWidth
    {
        get => GetValue(TargetWidthProperty);
        set => SetValue(TargetWidthProperty, value);
    }

    public bool IsPinned
    {
        get => GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Backdrop, bounds);

        if (Source is { } source && source.Size.Width > 0 && source.Size.Height > 0)
        {
            DrawMagnified(context, source, bounds);
        }

        DrawFrame(context, bounds);

        if (!string.IsNullOrEmpty(Caption))
        {
            DrawCaption(context, bounds, Caption);
        }
    }

    /// <summary>
    /// The frame, ruled or drawn depending on the experiment.
    ///
    /// Kept white either way rather than taking the theme's ink. The loupe is the one piece of
    /// chrome that sits <i>on top of a photograph</i>, and a themed line has no guaranteed contrast
    /// against an arbitrary image — a dark teal frame would vanish into a dark teal photograph. The
    /// shape follows the setting; the colour cannot afford to.
    /// </summary>
    private void DrawFrame(DrawingContext context, Rect bounds)
    {
        if (!IsHandDrawn)
        {
            context.DrawRectangle(null, Frame, bounds);
            return;
        }

        // Inset enough that the wobble and the corner overshoot both stay inside the window, so no
        // part of the border is clipped away by the edge it is drawn against.
        var area = bounds.Deflate(6);

        foreach (var edge in RoughGeometry.BorderEdges(area, RoughGeometry.SeedFor(area), Roughness))
        {
            if (RoughGeometry.Build(edge, 1) is { } geometry)
            {
                context.DrawGeometry(null, Frame, geometry);
            }
        }
    }

    private void DrawMagnified(DrawingContext context, IImage source, Rect bounds)
    {
        // Physical pixels, not DIPs: on a Retina display the loupe covers twice as many source
        // pixels as its width in drawing units, and taking only the latter would upscale everything
        // by two and resample it.
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1;

        // Measured against the resolution 100% is defined by, so that a develop landing mid-inspection
        // sharpens the picture without resizing it.
        var target = TargetWidth > 0 ? TargetWidth : source.Size.Width;
        var window = LoupeGeometry.SourceWindow(bounds.Size, scaling, source.Size.Width, target);

        var offset = LoupeGeometry.SourceOffset(Relative, source.Size, window);

        var width = Math.Min(source.Size.Width, window.Width);
        var height = Math.Min(source.Size.Height, window.Height);

        var sourceRect = new Rect(Math.Max(0, -offset.X), Math.Max(0, -offset.Y), width, height);

        // Back into drawing units. At the target resolution the source rectangle and the physical
        // pixels it lands on are the same count, so the draw is a copy; below it the same rectangle
        // is stretched, which is the deliberately soft stand-in.
        var magnify = target / source.Size.Width;
        var destinationRect = new Rect(
            Math.Max(0, offset.X) * magnify / scaling,
            Math.Max(0, offset.Y) * magnify / scaling,
            width * magnify / scaling,
            height * magnify / scaling);

        context.DrawImage(source, sourceRect, destinationRect);
    }

    /// <summary>
    /// The badge, at the top-left where the eye already is rather than tucked into a corner it has no
    /// reason to look at. It answers the only question the magnified pixels cannot: whether this is
    /// the photograph or a stand-in for it while the develop runs.
    /// </summary>
    private void DrawCaption(DrawingContext context, Rect bounds, string caption)
    {
        var text = new FormattedText(
            IsPinned ? caption + "  ·  Esc" : caption,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
            12,
            BadgeText);

        const double padding = 6;
        const double inset = 8;

        var badge = new Rect(
            inset,
            inset,
            Math.Min(text.Width + padding * 2, bounds.Width - inset * 2),
            text.Height + padding * 2);

        context.FillRectangle(BadgeBackground, badge, 4);
        context.DrawText(text, new Point(badge.X + padding, badge.Y + padding));
    }
}
