using Avalonia;
using Amberglass.UI.Controls;
using Xunit;

namespace Amberglass.Tests;

public class LoupeGeometryTests
{
    // ---- FitRect ------------------------------------------------------------------------------

    /// <summary>
    /// A panorama in a squarish pane leaves most of the pane empty. Mapping pointer positions
    /// against the pane instead of the letterboxed picture is the mistake this exists to prevent.
    /// </summary>
    [Fact]
    public void Letterboxes_wide_content_in_a_square_viewport()
    {
        var fit = LoupeGeometry.FitRect(new Size(2000, 1000), new Size(800, 800));

        Assert.Equal(new Rect(0, 200, 800, 400), fit);
    }

    [Fact]
    public void Pillarboxes_tall_content()
    {
        var fit = LoupeGeometry.FitRect(new Size(1000, 2000), new Size(800, 800));

        Assert.Equal(new Rect(200, 0, 400, 800), fit);
    }

    [Fact]
    public void Fills_the_viewport_when_the_aspect_ratios_match()
        => Assert.Equal(
            new Rect(0, 0, 800, 400),
            LoupeGeometry.FitRect(new Size(4000, 2000), new Size(800, 400)));

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    public void Degenerate_sizes_produce_an_empty_rect(double cw, double ch, double vw, double vh)
        => Assert.Equal(default, LoupeGeometry.FitRect(new Size(cw, ch), new Size(vw, vh)));

    // ---- ToRelative ---------------------------------------------------------------------------

    [Fact]
    public void Maps_the_middle_of_the_picture_to_the_middle()
    {
        var relative = LoupeGeometry.ToRelative(new Point(400, 400), new Size(2000, 1000), new Size(800, 800));

        Assert.Equal(new Point(0.5, 0.5), relative);
    }

    [Fact]
    public void Maps_the_corners_of_the_picture_rather_than_of_the_pane()
    {
        var content = new Size(2000, 1000);
        var viewport = new Size(800, 800);

        // The picture occupies y 200..600, so its top-left is at (0, 200), not (0, 0).
        Assert.Equal(new Point(0, 0), LoupeGeometry.ToRelative(new Point(0, 200), content, viewport));
        Assert.Equal(new Point(1, 1), LoupeGeometry.ToRelative(new Point(800, 600), content, viewport));
    }

    /// <summary>Nothing to magnify in the letterbox, so the loupe must not open there.</summary>
    [Theory]
    [InlineData(400, 100)]
    [InlineData(400, 700)]
    public void Rejects_a_pointer_over_the_letterbox(double x, double y)
        => Assert.Null(LoupeGeometry.ToRelative(new Point(x, y), new Size(2000, 1000), new Size(800, 800)));

    // ---- SourceOffset -------------------------------------------------------------------------

    /// <summary>The middle of a large picture lands in the middle of the loupe.</summary>
    [Fact]
    public void Centres_the_requested_point()
    {
        var offset = LoupeGeometry.SourceOffset(new Point(0.5, 0.5), new Size(6000, 4000), new Size(340, 340));

        Assert.Equal(new Point(170 - 3000, 170 - 2000), offset);
    }

    /// <summary>
    /// Near an edge the loupe slides rather than showing empty space — checking a corner for focus
    /// is exactly when it is wanted, and a quarter-full loupe would defeat that.
    /// </summary>
    [Fact]
    public void Clamps_at_the_edges_so_the_loupe_stays_full()
    {
        var source = new Size(6000, 4000);
        var loupe = new Size(340, 340);

        Assert.Equal(new Point(0, 0), LoupeGeometry.SourceOffset(new Point(0, 0), source, loupe));
        Assert.Equal(new Point(-5660, -3660), LoupeGeometry.SourceOffset(new Point(1, 1), source, loupe));
    }

    /// <summary>A picture smaller than the loupe is centred in it rather than pinned to a corner.</summary>
    [Fact]
    public void Centres_content_smaller_than_the_loupe()
    {
        var offset = LoupeGeometry.SourceOffset(new Point(0.5, 0.5), new Size(200, 100), new Size(340, 340));

        Assert.Equal(new Point(70, 120), offset);
    }

    /// <summary>
    /// The two halves have to agree: the point picked out of the pane must be the point that ends up
    /// under the cursor in the loupe. This is the property that makes the loupe trustworthy.
    /// </summary>
    [Theory]
    [InlineData(120, 260)]
    [InlineData(400, 400)]
    [InlineData(760, 560)]
    public void The_magnified_point_is_the_one_under_the_cursor(double x, double y)
    {
        var content = new Size(2000, 1000);
        var viewport = new Size(800, 800);
        var source = new Size(6000, 3000);
        var loupe = new Size(340, 340);

        var relative = LoupeGeometry.ToRelative(new Point(x, y), content, viewport);
        Assert.NotNull(relative);

        var offset = LoupeGeometry.SourceOffset(relative.Value, source, loupe);

        // Where that spot in the picture actually renders inside the loupe.
        var renderedX = offset.X + relative.Value.X * source.Width;
        var renderedY = offset.Y + relative.Value.Y * source.Height;

        // The middle, except where clamping has deliberately slid the view to keep the loupe full.
        Assert.InRange(renderedX, 0, loupe.Width);
        Assert.InRange(renderedY, 0, loupe.Height);

        var clampedX = relative.Value.X * source.Width is var sx && (sx < loupe.Width / 2 || sx > source.Width - loupe.Width / 2);
        if (!clampedX)
        {
            Assert.Equal(loupe.Width / 2, renderedX, 6);
        }
    }

    // ---- SourceWindow -------------------------------------------------------------------------

    /// <summary>
    /// The Retina bug: a 340 DIP loupe covers 680 physical pixels, so filling it with 340 source
    /// pixels magnifies everything 2× and resamples it — which is what made JPEGs look grainy while
    /// developed RAWs, having no compression artefacts to magnify, merely looked soft.
    /// </summary>
    [Fact]
    public void Spans_physical_pixels_rather_than_drawing_units()
        => Assert.Equal(new Size(680, 680), LoupeGeometry.SourceWindow(new Size(340, 340), 2));

    [Fact]
    public void Spans_its_own_size_on_an_unscaled_display()
        => Assert.Equal(new Size(340, 340), LoupeGeometry.SourceWindow(new Size(340, 340), 1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Treats_a_nonsense_scaling_as_unscaled(double scaling)
        => Assert.Equal(new Size(340, 340), LoupeGeometry.SourceWindow(new Size(340, 340), scaling));

    /// <summary>
    /// At 100% the loupe is a copy, not a resample: the slice taken from the source is exactly as
    /// many pixels as the physical pixels it is painted onto.
    /// </summary>
    [Fact]
    public void At_full_resolution_the_slice_matches_the_physical_pixels_it_fills()
    {
        var bounds = new Size(340, 340);
        const double scaling = 2;

        var window = LoupeGeometry.SourceWindow(bounds, scaling);

        Assert.Equal(bounds.Width * scaling, window.Width);
        Assert.Equal(window.Width / scaling, bounds.Width);
    }

    /// <summary>
    /// The reported jump: a RAW's embedded preview is a quarter of the developed width, so filling
    /// the loupe with preview pixels at 1:1 would show the picture four times smaller and then resize
    /// it the moment the develop landed. Scaling the window into the preview's own pixels keeps the
    /// magnification identical across the swap.
    /// </summary>
    [Fact]
    public void Takes_fewer_source_pixels_when_the_source_is_below_the_target()
    {
        var window = LoupeGeometry.SourceWindow(
            new Size(340, 340), renderScaling: 2, sourceWidth: 1600, targetWidth: 6400);

        // A quarter of 680: the same picture area, drawn from a quarter as many pixels.
        Assert.Equal(new Size(170, 170), window);
    }

    [Fact]
    public void Takes_the_full_window_once_the_source_is_the_target()
        => Assert.Equal(
            new Size(680, 680),
            LoupeGeometry.SourceWindow(new Size(340, 340), 2, sourceWidth: 6400, targetWidth: 6400));

    /// <summary>
    /// The property that matters: whatever the source resolution, the loupe covers the same fraction
    /// of the picture — which is what stops it jumping when a develop lands mid-inspection.
    /// </summary>
    [Theory]
    [InlineData(1600)]
    [InlineData(3200)]
    [InlineData(6400)]
    public void Covers_the_same_fraction_of_the_picture_at_any_source_resolution(double sourceWidth)
    {
        var window = LoupeGeometry.SourceWindow(
            new Size(340, 340), renderScaling: 2, sourceWidth: sourceWidth, targetWidth: 6400);

        Assert.Equal(680.0 / 6400, window.Width / sourceWidth, 9);
    }

    [Theory]
    [InlineData(0, 6400)]
    [InlineData(1600, 0)]
    public void Falls_back_to_the_plain_window_without_usable_widths(double sourceWidth, double targetWidth)
        => Assert.Equal(
            new Size(680, 680),
            LoupeGeometry.SourceWindow(new Size(340, 340), 2, sourceWidth, targetWidth));

    // ---- Magnification ------------------------------------------------------------------------

    /// <summary>
    /// A 24MP photograph shown 800 DIP wide, magnified by a loupe at one image pixel per physical
    /// pixel on a 2× display: 7.5× in raw pixel terms, but half that as it appears on screen.
    /// </summary>
    [Fact]
    public void Reports_magnification_as_it_appears_on_screen()
    {
        var magnification = LoupeGeometry.Magnification(
            targetWidth: 6000, content: new Size(6000, 4000),
            viewport: new Size(800, 800), renderScaling: 2);

        Assert.Equal(3.75, magnification, 6);
    }

    [Fact]
    public void Reports_the_raw_ratio_on_an_unscaled_display()
        => Assert.Equal(
            7.5,
            LoupeGeometry.Magnification(
                targetWidth: 6000, content: new Size(6000, 4000),
                viewport: new Size(800, 800), renderScaling: 1),
            6);

    /// <summary>
    /// Before the full decode lands the loupe magnifies the 1600px preview, and says so with a much
    /// smaller number rather than claiming 100%.
    /// </summary>
    [Fact]
    public void Reports_a_smaller_magnification_for_the_preview()
    {
        var magnification = LoupeGeometry.Magnification(
            targetWidth: 1600, content: new Size(1600, 1067),
            viewport: new Size(800, 800), renderScaling: 1);

        Assert.Equal(2, magnification, 6);
    }
}
