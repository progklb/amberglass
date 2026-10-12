using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Amberglass.UI.Controls;

/// <summary>What shape a mark takes.</summary>
public enum RoughMarkKind
{
    None,

    /// <summary>An ellipse round a short label. What the folder tree uses for its selection.</summary>
    Ring,

    /// <summary>
    /// A rounded box. What a thumbnail uses: an ellipse round a tall rectangle cuts across the
    /// corners of the picture, and a tile is already a box — the pencil should agree with it rather
    /// than argue.
    /// </summary>
    Box,

    /// <summary>A single stroke beneath. The lighter mark, for hover and for a chosen tab.</summary>
    Underline,

    /// <summary>
    /// Two strokes corner to corner: a rejected frame on a contact sheet. The one mark that is a
    /// judgement rather than a highlight, and the only one drawn across a picture on purpose.
    /// </summary>
    Cross
}

/// <summary>
/// Marks a control as selected or hovered, drawn as if by pencil, for the hand-drawn selection
/// experiment.
///
/// One control rather than two because the two marks are the same gesture at different weights, and
/// because it lets the control decide between them: a row that is both selected and hovered gets the
/// ring only. Two separate controls would need a converter to express that, and would occasionally
/// draw both.
///
/// Two details carry the effect, and both are only wrong in motion:
///
/// <list type="bullet">
/// <item>
/// The geometry is built at the control's real size rather than a fixed path being stretched. A
/// stretched path gets a fat stroke on its long axis and a thin one on its short.
/// </item>
/// <item>
/// The wobble is seeded from that size, so a given row draws the same line every frame. Unseeded
/// noise re-rolls on each repaint and the line crawls during scrolls and resizes.
/// </item>
/// </list>
/// </summary>
public sealed class RoughMark : Control
{
    /// <summary>The selected row. Drawn as a ring around the name.</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(IsSelected));

    /// <summary>Under the pointer. Marked only when not selected — the stronger mark wins.</summary>
    public static readonly StyledProperty<bool> IsHoveredProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(IsHovered));

    /// <summary>The mark drawn when selected.</summary>
    public static readonly StyledProperty<RoughMarkKind> KindProperty =
        AvaloniaProperty.Register<RoughMark, RoughMarkKind>(nameof(Kind), RoughMarkKind.Ring);

    /// <summary>
    /// The mark drawn when hovered but not selected. <see cref="RoughMarkKind.None"/> leaves hover
    /// to whatever the control already did — which is what the inspector's tabs want, since their
    /// standard highlight is already unobtrusive and only the chosen tab needed redrawing.
    /// </summary>
    public static readonly StyledProperty<RoughMarkKind> HoverKindProperty =
        AvaloniaProperty.Register<RoughMark, RoughMarkKind>(nameof(HoverKind), RoughMarkKind.Underline);

    public static readonly StyledProperty<double> RoughnessProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(Roughness), 1.0);

    public static readonly StyledProperty<bool> AnimatesProperty =
        AvaloniaProperty.Register<RoughMark, bool>(nameof(Animates), true);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<RoughMark, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(StrokeThickness), 1.5);

    /// <summary>How far the drawing has got, 0 to 1. Animated; not usually set by hand.</summary>
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<RoughMark, double>(nameof(Progress), 1.0);

    /// <summary>
    /// The ring takes a moment; the underline does not. Hover has to keep up with a pointer moving
    /// down a list, where a leisurely draw would still be finishing as the pointer left.
    /// </summary>
    private static readonly TimeSpan RingDuration = TimeSpan.FromMilliseconds(520);

    private static readonly TimeSpan UnderlineDuration = TimeSpan.FromMilliseconds(190);

    static RoughMark()
    {
        AffectsRender<RoughMark>(
            IsSelectedProperty, IsHoveredProperty, KindProperty, HoverKindProperty,
            RoughnessProperty, StrokeProperty, StrokeThicknessProperty, ProgressProperty);
    }

    public RoughMark()
    {
        // Purely decorative: it sits over the folder name and must never eat a click meant for it.
        IsHitTestVisible = false;
    }

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsHovered { get => GetValue(IsHoveredProperty); set => SetValue(IsHoveredProperty, value); }
    public RoughMarkKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public RoughMarkKind HoverKind { get => GetValue(HoverKindProperty); set => SetValue(HoverKindProperty, value); }
    public double Roughness { get => GetValue(RoughnessProperty); set => SetValue(RoughnessProperty, value); }
    public bool Animates { get => GetValue(AnimatesProperty); set => SetValue(AnimatesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    /// <summary>What is actually drawn right now. The selected mark wins when a row is both.</summary>
    private RoughMarkKind Current =>
        IsSelected ? Kind
        : IsHovered ? HoverKind
        : RoughMarkKind.None;

    /// <summary>The mark currently on screen, so a redraw is only started when it actually changes.</summary>
    private RoughMarkKind _drawn = RoughMarkKind.None;

    private DispatcherTimer? _timer;
    private DateTime _startedAt;
    private TimeSpan _duration = RingDuration;

    // Cached points. The draw-on changes only how many are used, so rebuilding per frame would be
    // waste — and re-seeding per frame would reintroduce the crawl this design exists to avoid.
    private Rect _cachedFor;
    private double _cachedRoughness = double.NaN;
    private RoughMarkKind _cachedKind = RoughMarkKind.None;
    private Point[]? _first;
    private Point[]? _second;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsSelectedProperty && change.Property != IsHoveredProperty)
        {
            return;
        }

        var next = Current;

        // Only a change of mark is worth drawing.
        //
        // Hovering a row that is already selected does not change what is drawn — the selected mark
        // wins either way — but the pointer still moves in and out of it constantly, and redrawing
        // on each crossing made the ring rub itself out and draw itself again under the cursor. The
        // mark a selection wears must not depend on where the pointer happens to be.
        if (next == _drawn)
        {
            return;
        }

        _drawn = next;

        switch (next)
        {
            case RoughMarkKind.None:
                _timer?.Stop();
                break;

            // The lighter mark is quick, the enclosing ones take a moment. Hover has to keep up with
            // a pointer moving down a list, where a leisurely draw would still be finishing as the
            // pointer left.
            case RoughMarkKind.Underline when !IsSelected:
                Begin(UnderlineDuration);
                break;

            default:
                Begin(RingDuration);
                break;
        }
    }

    private void Begin(TimeSpan duration)
    {
        if (!Animates)
        {
            _timer?.Stop();
            Progress = 1;
            return;
        }

        _duration = duration;
        Progress = 0;
        _startedAt = DateTime.UtcNow;

        _timer ??= CreateTimer();
        _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            var t = (DateTime.UtcNow - _startedAt).TotalMilliseconds / _duration.TotalMilliseconds;

            if (t >= 1)
            {
                Progress = 1;
                timer.Stop();
                return;
            }

            // Smoothstep rather than an ease-out. An ease-out was tried first and is wrong for a pen
            // stroke: it spends most of the duration on the last sliver of line, so the mark looks
            // almost finished immediately and then creeps. A hand accelerates in and decelerates
            // out, at roughly even speed between.
            Progress = t * t * (3 - (2 * t));
        };

        return timer;
    }

    public override void Render(DrawingContext context)
    {
        if (Stroke is null || Bounds.Width <= 8 || Bounds.Height <= 8)
        {
            return;
        }

        var kind = Current;
        if (kind == RoughMarkKind.None)
        {
            return;
        }

        var box = new Rect(Bounds.Size).Deflate(StrokeThickness);
        EnsurePoints(box, kind);

        var pen = new Pen(Stroke, StrokeThickness, lineCap: PenLineCap.Round);
        var progress = Math.Clamp(Progress, 0, 1);

        if (kind == RoughMarkKind.Underline)
        {
            DrawPass(context, pen, _first!, progress);
            return;
        }

        if (kind == RoughMarkKind.Cross)
        {
            // Two separate strokes, and the second waits for the first: a hand lifts between them.
            // Overlapping them, as the rings below do, would read as a machine drawing an X.
            DrawPass(context, pen, _first!, Window(progress, 0.0, 0.48));
            DrawPass(context, pen, _second!, Window(progress, 0.52, 1.0));
            return;
        }

        // The second pass sets off before the first has finished, the way a hand comes back round
        // without pausing at the top.
        DrawPass(context, pen, _first!, Window(progress, 0.0, 0.72));
        DrawPass(context, pen, _second!, Window(progress, 0.28, 1.0));
    }

    private static double Window(double t, double from, double to)
        => Math.Clamp((t - from) / (to - from), 0, 1);

    private static void DrawPass(DrawingContext context, IPen pen, Point[] points, double portion)
    {
        if (RoughGeometry.Build(points, portion) is { } geometry)
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private void EnsurePoints(Rect box, RoughMarkKind kind)
    {
        if (_first is not null
            && _cachedFor == box
            && _cachedRoughness.Equals(Roughness)
            && _cachedKind == kind)
        {
            return;
        }

        var seed = RoughGeometry.SeedFor(box);

        if (kind == RoughMarkKind.Underline)
        {
            _first = RoughGeometry.Underline(box, seed, Roughness);
            _second = null;
        }
        else if (kind == RoughMarkKind.Cross)
        {
            (_first, _second) = RoughGeometry.Cross(box, seed, Roughness);
        }
        else
        {
            var isBox = kind == RoughMarkKind.Box;
            var start = new Random(seed).NextDouble() * Math.PI * 2;

            _first = RoughGeometry.Enclosing(box, seed, Roughness, start, (Math.PI * 2) + 0.35, isBox);
            _second = RoughGeometry.Enclosing(box, seed + 977, Roughness, start + 0.5, (Math.PI * 2) + 0.15, isBox);
        }

        _cachedFor = box;
        _cachedRoughness = Roughness;
        _cachedKind = kind;
    }
}
