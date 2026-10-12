using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Amberglass.UI.Controls;

/// <summary>
/// The perforated edge of a strip of film, for the contact sheet experiment.
///
/// Drawn where the layout already has a strip — the splitters between panes — and nowhere else.
/// Sprocket holes on every tile were the first idea and the wrong one: a grid of two hundred
/// frames would carry four hundred bright rectangles, all arguing with the photographs. A divider
/// is a strip whether or not it is dressed as one, so dressing it costs the pictures nothing.
///
/// The holes run along whichever axis is longer, so one control serves a column splitter and a
/// row splitter without being told which it is.
/// </summary>
public sealed class FilmEdge : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<FilmEdge, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> HoleFillProperty =
        AvaloniaProperty.Register<FilmEdge, IBrush?>(nameof(HoleFill));

    public static readonly StyledProperty<IBrush?> HoleStrokeProperty =
        AvaloniaProperty.Register<FilmEdge, IBrush?>(nameof(HoleStroke));

    static FilmEdge()
    {
        AffectsRender<FilmEdge>(FillProperty, HoleFillProperty, HoleStrokeProperty);
    }

    /// <summary>The strip itself.</summary>
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>What shows through a hole — whatever is behind the film.</summary>
    public IBrush? HoleFill { get => GetValue(HoleFillProperty); set => SetValue(HoleFillProperty, value); }

    /// <summary>The cut edge of each hole, a hair lighter than the strip.</summary>
    public IBrush? HoleStroke { get => GetValue(HoleStrokeProperty); set => SetValue(HoleStrokeProperty, value); }

    /// <summary>
    /// Where the holes fall along a strip, as the offset of each hole's leading edge.
    ///
    /// Spaced so the run is centred, with the same margin at both ends — a strip cut to an
    /// arbitrary length would otherwise end on a half hole. The proportions are those of 35mm
    /// perforations, near enough: each hole is a little longer than it is wide and sits in a pitch
    /// of about one and three-quarter times its length.
    /// </summary>
    public static double[] Layout(double length, double hole, double pitch)
    {
        if (length <= 0 || hole <= 0 || pitch <= 0 || hole > length)
        {
            return [];
        }

        var count = (int)Math.Floor((length - hole) / pitch) + 1;
        var run = hole + ((count - 1) * pitch);
        var margin = (length - run) / 2;

        var offsets = new double[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = margin + (i * pitch);
        }

        return offsets;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (Fill is not null)
        {
            context.FillRectangle(Fill, bounds);
        }

        if (HoleFill is null && HoleStroke is null)
        {
            return;
        }

        var vertical = bounds.Height >= bounds.Width;
        var across = vertical ? bounds.Width : bounds.Height;
        var length = vertical ? bounds.Height : bounds.Width;

        var holeAcross = Math.Round(across * 0.5);
        var holeAlong = Math.Round(holeAcross * 1.4);
        var pitch = holeAlong * 1.75;
        var inset = (across - holeAcross) / 2;

        var pen = HoleStroke is null ? null : new Pen(HoleStroke, 1);

        foreach (var offset in Layout(length, holeAlong, pitch))
        {
            var hole = vertical
                ? new Rect(inset, offset, holeAcross, holeAlong)
                : new Rect(offset, inset, holeAlong, holeAcross);

            // Snapped to the half pixel so a one-point stroke lands on one row of pixels rather
            // than smearing across two.
            hole = new Rect(Math.Round(hole.X) + 0.5, Math.Round(hole.Y) + 0.5, hole.Width - 1, hole.Height - 1);

            context.DrawRectangle(HoleFill, pen, hole, 1.5, 1.5);
        }
    }
}
