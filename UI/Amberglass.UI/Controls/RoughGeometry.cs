using Avalonia;
using Avalonia.Media;

namespace Amberglass.UI.Controls;

/// <summary>
/// The pencil itself: points sampled around a shape and pushed off course, smoothed into cubics.
///
/// Separate from <see cref="RoughMark"/> because the loupe draws its own frame in
/// <c>Render</c> rather than hosting a control, and two implementations of the same wobble would
/// drift apart the first time either was tuned.
/// </summary>
internal static class RoughGeometry
{
    /// <summary>
    /// One pass around a shape. <paramref name="box"/> false gives an ellipse; true gives a
    /// superellipse — the same maths with a higher exponent, so corners square off while the edges
    /// stay very slightly convex and the line never looks ruled.
    /// </summary>
    public static Point[] Enclosing(
        Rect area, int seed, double roughness, double startAngle, double sweep, bool box)
    {
        var random = new Random(seed);
        var cx = area.Center.X;
        var cy = area.Center.Y;
        var rx = area.Width / 2;
        var ry = area.Height / 2;

        // A straight edge shows a wobble far more than a curve does, so a box wanders much less —
        // and in points rather than as a fraction of the shape, which for anything tile-sized would
        // leave the pencil wandering across the picture.
        var amplitude = box
            ? Math.Clamp(roughness * 2.2, 0, 6)
            : roughness * Math.Min(rx, ry) * 0.09;

        var exponent = box ? 2.0 / 4.5 : 1.0;

        // A box needs more samples: its corners turn sharply, and the parameter bunches there.
        var steps = box ? 40 : 26;
        var points = new Point[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = startAngle + (sweep * i / steps);

            // Tapered at both ends, so the overshoot settles instead of flying off.
            var taper = Math.Sin(Math.PI * i / steps);
            var jx = (random.NextDouble() - 0.5) * 2 * amplitude * taper;
            var jy = (random.NextDouble() - 0.5) * 2 * amplitude * taper;

            var cos = Math.Cos(t);
            var sin = Math.Sin(t);

            points[i] = new Point(
                cx + (rx * Math.Sign(cos) * Math.Pow(Math.Abs(cos), exponent)) + jx,
                cy + (ry * Math.Sign(sin) * Math.Pow(Math.Abs(sin), exponent)) + jy);
        }

        return points;
    }

    /// <summary>
    /// A single stroke under a label. One pass, not two: a second would read as a deliberate double
    /// underline rather than as the lighter-weight sibling of an enclosing mark.
    /// </summary>
    public static Point[] Underline(Rect area, int seed, double roughness)
    {
        var random = new Random(seed);

        // Wobble in points rather than as a fraction of the row: an underline that scaled its
        // waviness with the label's length would ripple wildly under a long one.
        var amplitude = roughness * 1.4;

        const int steps = 14;
        var points = new Point[steps + 1];

        // Runs a little past the label at both ends, the way a hand does not stop exactly on a mark.
        var left = area.Left - 2;
        var right = area.Right + 4;
        var baseline = area.Bottom - 1;

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var taper = Math.Sin(Math.PI * t);

            points[i] = new Point(
                left + ((right - left) * t),
                baseline + ((random.NextDouble() - 0.5) * 2 * amplitude * taper));
        }

        return points;
    }

    /// <summary>
    /// A drawn border that follows the edges of a rectangle, returned as one stroke per side.
    ///
    /// Four strokes rather than one loop, and that is the whole point of it. A single smoothed loop
    /// rounds its own corners — the spline has no way to know a corner was meant to be sharp — which
    /// produces a rounded shape sitting inside a square frame rather than a border on it. Separate
    /// strokes keep the corners square, and letting each run a little past its corner gives the
    /// crossed ends a hand-drawn box actually has.
    ///
    /// The wobble is perpendicular to each edge and tapers to nothing at both ends, so the sides bow
    /// gently while the corners still meet.
    /// </summary>
    public static IReadOnlyList<Point[]> BorderEdges(Rect area, int seed, double roughness)
    {
        // In points, and modest: this line is meant to read as the edge of the window it is drawn
        // on, so it may wander a little without ever losing the edge.
        var amplitude = Math.Clamp(roughness * 1.8, 0, 4.5);
        const double overshoot = 3.5;
        const int steps = 12;

        var corners = new[]
        {
            area.TopLeft, area.TopRight, area.BottomRight, area.BottomLeft
        };

        var edges = new List<Point[]>(4);

        for (var edge = 0; edge < 4; edge++)
        {
            if (Stroke(corners[edge], corners[(edge + 1) % 4], seed + (edge * 131), amplitude, overshoot, steps)
                is { } points)
            {
                edges.Add(points);
            }
        }

        return edges;
    }

    /// <summary>
    /// A chinagraph cross through a frame: two strokes, corner to corner, the way a contact sheet
    /// is marked up. Returned as two strokes rather than one path because they are drawn as two —
    /// the hand lifts between them.
    ///
    /// Heavier-handed than a border. This is a judgement written across a picture, not an edge
    /// following a window, so it may wander more and it runs well past the corners: a grease
    /// pencil is pressed hard and does not stop where the frame does.
    /// </summary>
    public static (Point[] First, Point[] Second) Cross(Rect area, int seed, double roughness)
    {
        var amplitude = Math.Clamp(roughness * 2.6, 0, 7);

        // In proportion to the frame rather than fixed: a tile can be anything from a postage stamp
        // to a quarter of the screen, and an overshoot right for one is invisible or absurd on the
        // other.
        var overshoot = Math.Min(area.Width, area.Height) * 0.06;

        const int steps = 12;

        var first = Stroke(area.TopLeft, area.BottomRight, seed, amplitude, overshoot, steps);
        var second = Stroke(area.TopRight, area.BottomLeft, seed + 71, amplitude, overshoot, steps);

        return (first ?? [], second ?? []);
    }

    /// <summary>
    /// One stroke between two points, wobbling only sideways and tapering to nothing at both ends
    /// so it lands where it was aimed. The one pencil that every straight mark is drawn with.
    /// </summary>
    private static Point[]? Stroke(Point from, Point to, int seed, double amplitude, double overshoot, int steps)
    {
        var random = new Random(seed);

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length <= 0)
        {
            return null;
        }

        // Unit vector along the stroke, and its normal — the wobble only ever goes sideways.
        var ux = dx / length;
        var uy = dy / length;
        var nx = -uy;
        var ny = ux;

        var points = new Point[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;

            // Runs from just before the first point to just past the second.
            var along = -overshoot + (t * (length + (overshoot * 2)));
            var taper = Math.Sin(Math.PI * t);
            var offset = (random.NextDouble() - 0.5) * 2 * amplitude * taper;

            points[i] = new Point(
                from.X + (ux * along) + (nx * offset),
                from.Y + (uy * along) + (ny * offset));
        }

        return points;
    }

    /// <summary>
    /// Smooths the points into cubics, drawing only the first <paramref name="portion"/> of them so
    /// a mark can be animated on without its geometry being rebuilt per frame.
    /// </summary>
    public static Geometry? Build(Point[] points, double portion)
    {
        var count = (int)Math.Round((points.Length - 1) * portion);
        if (count < 2)
        {
            return null;
        }

        var geometry = new StreamGeometry();

        using var sink = geometry.Open();
        sink.BeginFigure(points[0], isFilled: false);

        for (var i = 0; i < count; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Length - 1)];

            // Catmull-Rom through the sampled points, written as the cubics Avalonia takes.
            sink.CubicBezierTo(
                new Point(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6)),
                new Point(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6)),
                p2);
        }

        sink.EndFigure(isClosed: false);

        return geometry;
    }

    /// <summary>
    /// A checkmark, as two crossing strokes.
    ///
    /// Two rather than one for the same reason the loupe's border is four: a single smoothed run
    /// rounds its own elbow, and a tick with a rounded elbow looks like a swoosh. Drawn separately
    /// and each overshooting the join, they cross the way two strokes of a pen do.
    ///
    /// Authored in a square of <paramref name="size"/> and scaled by the Viewbox Fluent already
    /// wraps its glyph in, which is safe here in a way it would not be for a mark fitted to an
    /// arbitrary label — a checkbox is always the same shape, so the stroke cannot be stretched
    /// unevenly.
    /// </summary>
    public static Geometry Tick(double size, int seed, double roughness)
    {
        // Vertices of an ordinary tick: start left of centre, down to the elbow, up past the top
        // right. Fractions of the square so the shape survives a change of authoring size.
        var start = new Point(size * 0.12, size * 0.50);
        var elbow = new Point(size * 0.38, size * 0.78);
        var end = new Point(size * 0.88, size * 0.14);

        var geometry = new StreamGeometry();
        using var sink = geometry.Open();

        // Both strokes stop exactly on the elbow. Letting them run past it — which is what a bigger
        // hand-drawn mark wants — hangs a spur below the join, because the up-stroke extended
        // backwards points down and to the left. At this size that spur is the most conspicuous
        // thing about the tick, so the crossing goes and only the flick past the tip stays.
        Stroke(sink, start, elbow, seed, roughness, size);
        Stroke(sink, elbow, end, seed + 61, roughness, size, overshootEnd: true);

        return geometry;
    }

    private static void Stroke(
        StreamGeometryContext sink, Point from, Point to, int seed, double roughness, double size,
        bool overshootEnd = false)
    {
        var random = new Random(seed);

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var ux = dx / length;
        var uy = dy / length;

        // Scaled to the authoring square, so the wobble is the same relative to the tick whatever
        // size it is drawn at.
        //
        // These are far larger fractions than the enclosing marks use, and deliberately: the Viewbox
        // scales this geometry down to roughly two thirds before it reaches the screen, and a tick
        // is a couple of centimetres of line rather than a lap of a tile. A wander that reads well
        // on a ring disappears entirely here — the first attempt came out at about a third of a
        // pixel, which is to say perfectly straight.
        var amplitude = roughness * size * 0.075;

        // Only ever used to run past the tip, where it reads as a pen being lifted late.
        var over = size * 0.085;

        const double begin = 0;
        var finish = length + (overshootEnd ? over : 0);

        const int steps = 6;
        var points = new Point[steps + 1];

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (double)steps;
            var along = begin + (t * (finish - begin));
            var taper = Math.Sin(Math.PI * t);
            var offset = (random.NextDouble() - 0.5) * 2 * amplitude * taper;

            points[i] = new Point(
                from.X + (ux * along) - (uy * offset),
                from.Y + (uy * along) + (ux * offset));
        }

        sink.BeginFigure(points[0], isFilled: false);

        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = points[Math.Max(i - 1, 0)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(i + 2, points.Length - 1)];

            sink.CubicBezierTo(
                new Point(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6)),
                new Point(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6)),
                p2);
        }

        sink.EndFigure(isClosed: false);
    }

    /// <summary>
    /// A seed taken from a shape's size, so a given control draws the same wobble every frame.
    /// Unseeded noise re-rolls on each repaint and the line crawls.
    /// </summary>
    public static int SeedFor(Rect area)
        => HashCode.Combine(Math.Round(area.Width), Math.Round(area.Height));
}
