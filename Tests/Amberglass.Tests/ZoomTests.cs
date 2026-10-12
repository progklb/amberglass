using Avalonia;
using Amberglass.Preview.Video;
using Amberglass.UI.Controls;
using Amberglass.UI.Services;
using Xunit;

namespace Amberglass.Tests;

public class ZoomStateTests
{
    /// <summary>A 4000x3000 image in a 1000x1000 viewport: fit is a quarter.</summary>
    private static ZoomState Create(double contentW = 4000, double contentH = 3000,
                                    double viewW = 1000, double viewH = 1000)
    {
        var state = new ZoomState();
        state.SetContent(new Size(contentW, contentH), new Size(viewW, viewH));
        return state;
    }

    [Fact]
    public void New_content_starts_fitted()
    {
        var state = Create();

        Assert.Equal(0.25, state.Scale, precision: 4);
        Assert.True(state.IsFitted);
    }

    [Fact]
    public void Fitting_centres_the_content()
    {
        var state = Create();

        // 4000x3000 at 0.25 is 1000x750, so it fills the width and is centred vertically.
        Assert.Equal(0, state.Offset.X, precision: 4);
        Assert.Equal(125, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Actual_size_is_one_content_pixel_per_screen_pixel()
    {
        var state = Create();

        state.ActualSize();

        Assert.Equal(1, state.Scale, precision: 4);
        Assert.False(state.IsFitted);
    }

    [Fact]
    public void Zooming_keeps_the_anchor_point_still()
    {
        var state = Create();
        var anchor = new Point(300, 400);

        // The content point under the anchor before the zoom...
        var before = ((anchor.X - state.Offset.X) / state.Scale, (anchor.Y - state.Offset.Y) / state.Scale);

        state.ZoomBy(2, anchor);

        var after = ((anchor.X - state.Offset.X) / state.Scale, (anchor.Y - state.Offset.Y) / state.Scale);

        // ...must still be under it afterwards, which is what makes wheel zoom feel anchored.
        Assert.Equal(before.Item1, after.Item1, precision: 3);
        Assert.Equal(before.Item2, after.Item2, precision: 3);
    }

    [Fact]
    public void Zoom_is_clamped_at_both_ends()
    {
        var state = Create();
        var anchor = new Point(500, 500);

        state.ZoomTo(1000, anchor);
        Assert.Equal(ZoomState.MaxScale, state.Scale);

        state.ZoomTo(0.0001, anchor);
        Assert.Equal(ZoomState.MinScale, state.Scale);
    }

    [Fact]
    public void Content_smaller_than_the_viewport_stays_centred()
    {
        var state = Create(contentW: 200, contentH: 100);

        state.ZoomTo(1, new Point(500, 500));

        // Panning cannot move it, because there is nothing hidden to pan to.
        state.PanBy(new Vector(400, 400));

        Assert.Equal(400, state.Offset.X, precision: 4);
        Assert.Equal(450, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Panning_cannot_drag_the_content_off_screen()
    {
        var state = Create();
        state.ActualSize();

        // A wild drag in each direction: the edges stop at the viewport, never past them.
        state.PanBy(new Vector(9999, 9999));
        Assert.Equal(0, state.Offset.X, precision: 4);
        Assert.Equal(0, state.Offset.Y, precision: 4);

        state.PanBy(new Vector(-99999, -99999));
        Assert.Equal(1000 - 4000, state.Offset.X, precision: 4);
        Assert.Equal(1000 - 3000, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Resizing_the_viewport_keeps_the_chosen_magnification()
    {
        var state = Create();
        state.ActualSize();

        state.SetContent(new Size(4000, 3000), new Size(1400, 900));

        // Only the window changed; the user's zoom is not theirs to reset.
        Assert.Equal(1, state.Scale, precision: 4);
    }

    [Fact]
    public void The_first_content_starts_fitted()
    {
        var state = new ZoomState();

        state.SetContent(new Size(800, 600), new Size(1000, 1000));

        Assert.True(state.IsFitted);
        Assert.Equal(1.25, state.Scale, precision: 4);
    }

    [Fact]
    public void Replacing_content_of_the_same_size_changes_nothing()
    {
        var state = Create();
        state.ActualSize();
        state.PanBy(new Vector(-500, -400));

        var scale = state.Scale;
        var offset = state.Offset;

        // A re-developed RAW at the same resolution: only the pixels differ.
        state.SetContent(new Size(4000, 3000), new Size(1000, 1000));

        Assert.Equal(scale, state.Scale, precision: 6);
        Assert.Equal(offset.X, state.Offset.X, precision: 6);
        Assert.Equal(offset.Y, state.Offset.Y, precision: 6);
    }

    [Fact]
    public void Replacing_content_of_a_different_size_keeps_the_same_region_framed()
    {
        var state = Create();
        state.ActualSize();

        // Look at a point three quarters across and a third down.
        state.PanBy(new Vector(-(3000 - 500), -(1000 - 500)));

        var before = CentreOfView(state);

        // Switching between a developed RAW and its embedded JPEG: same scene, fewer pixels.
        state.SetContent(new Size(2000, 1500), new Size(1000, 1000));

        var after = CentreOfView(state);

        Assert.Equal(before.X, after.X, precision: 2);
        Assert.Equal(before.Y, after.Y, precision: 2);
    }

    [Fact]
    public void Replacing_content_of_a_different_size_keeps_the_same_relative_zoom()
    {
        var state = Create();
        state.ActualSize();

        var relative = state.Scale / state.FitScale;

        state.SetContent(new Size(2000, 1500), new Size(1000, 1000));

        // Half the pixels, so 100% would show twice as much; staying at the same multiple of fit is
        // what keeps the comparison honest.
        Assert.Equal(relative, state.Scale / state.FitScale, precision: 4);
    }

    /// <summary>Where the middle of the viewport falls within the content, as a proportion.</summary>
    private static (double X, double Y) CentreOfView(ZoomState state)
    {
        var centre = new Point(state.Viewport.Width / 2, state.Viewport.Height / 2);
        return (
            (centre.X - state.Offset.X) / state.Scale / state.Content.Width,
            (centre.Y - state.Offset.Y) / state.Scale / state.Content.Height);
    }

    [Fact]
    public void Zooming_out_past_fit_re_centres_rather_than_sticking_to_a_corner()
    {
        var state = Create();
        state.ActualSize();
        state.PanBy(new Vector(-2000, -1500));

        state.ZoomTo(0.1, new Point(0, 0));

        // At 0.1 the content is 400x300, smaller than the viewport, so it must be centred.
        Assert.Equal(300, state.Offset.X, precision: 4);
        Assert.Equal(350, state.Offset.Y, precision: 4);
    }

    [Fact]
    public void Without_content_there_is_nothing_to_fit()
    {
        var state = new ZoomState();

        Assert.False(state.HasContent);
        Assert.Equal(1, state.FitScale);
    }
}

public class VideoRotationTests
{
    /// <summary>A one-video-stream probe result, with whatever extra stream properties are needed.</summary>
    private static string Json(string extraStreamProperties)
        => "{\"streams\":[{\"codec_type\":\"video\",\"width\":1920,\"height\":1080,"
           + "\"avg_frame_rate\":\"30/1\""
           + extraStreamProperties
           + "}],\"format\":{\"duration\":\"5\"}}";

    [Fact]
    public void An_unrotated_stream_keeps_its_dimensions()
    {
        var info = FfprobeVideoInfoProvider.Parse(Json(""));

        Assert.Equal(1920, info!.Width);
        Assert.Equal(1080, info.Height);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    [InlineData(270)]
    public void A_quarter_turn_swaps_them(int degrees)
    {
        // ffmpeg applies the rotation when decoding, so frames arrive portrait. Reporting the
        // stored landscape size would make the scaler squash them.
        var info = FfprobeVideoInfoProvider.Parse(
            Json(",\"side_data_list\":[{\"rotation\":" + degrees + "}]"));

        Assert.Equal(1080, info!.Width);
        Assert.Equal(1920, info.Height);
    }

    [Fact]
    public void A_half_turn_does_not()
    {
        var info = FfprobeVideoInfoProvider.Parse(Json(",\"side_data_list\":[{\"rotation\":180}]"));

        Assert.Equal(1920, info!.Width);
        Assert.Equal(1080, info.Height);
    }

    [Fact]
    public void The_legacy_rotate_tag_is_understood()
    {
        // Older files carry rotation as a tag rather than as display-matrix side data.
        var info = FfprobeVideoInfoProvider.Parse(Json(",\"tags\":{\"rotate\":\"90\"}"));

        Assert.Equal(1080, info!.Width);
        Assert.Equal(1920, info.Height);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(-90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    [InlineData(360, false)]
    public void Quarter_turns_are_recognised(double degrees, bool expected)
        => Assert.Equal(expected, FfprobeVideoInfoProvider.IsQuarterTurn(degrees));
}

public class RevealInFileManagerTests
{
    private const string Path = "/library/namibia/IMG001.jpg";

    [Fact]
    public void macOS_selects_the_file_rather_than_opening_it()
    {
        var (command, arguments) = RevealInFileManager.BuildCommand(Path, RevealInFileManager.PlatformKind.MacOS);

        // -R reveals; without it "open" would launch the file in Preview.
        Assert.Equal("open", command);
        Assert.Equal(["-R", Path], arguments);
    }

    [Fact]
    public void Windows_puts_no_space_after_the_select_comma()
    {
        var (command, arguments) = RevealInFileManager.BuildCommand(Path, RevealInFileManager.PlatformKind.Windows);

        Assert.Equal("explorer.exe", command);

        // "/select, path" would be read as two arguments and open Documents instead.
        Assert.Single(arguments);
        Assert.DoesNotContain(", ", arguments[0]);
        Assert.EndsWith(Path, arguments[0]);
    }

    [Fact]
    public void Elsewhere_the_containing_folder_is_opened()
    {
        var (command, arguments) = RevealInFileManager.BuildCommand(Path, RevealInFileManager.PlatformKind.Other);

        Assert.Equal("xdg-open", command);
        Assert.Equal([System.IO.Path.GetDirectoryName(Path)], arguments);
    }

    [Fact]
    public void The_menu_names_the_platform_s_own_file_manager()
        => Assert.Contains(RevealInFileManager.MenuHeader, (string[])["Reveal in Finder", "Show in Explorer", "Show in File Manager"]);
}

public class OpenFolderInFileManagerTests
{
    private const string Folder = "/library/namibia/etosha";

    /// <summary>
    /// The folder itself, with no selection flag. Opening it shows what is inside, where the reveal
    /// commands would show it picked out among its neighbours one level up.
    ///
    /// Not a [Theory]: PlatformKind is internal, so it cannot appear in a public test signature.
    /// </summary>
    private static void AssertOpens(RevealInFileManager.PlatformKind platform, string expected)
    {
        var (command, arguments) = RevealInFileManager.BuildOpenCommand(Folder, platform);

        Assert.Equal(expected, command);
        Assert.Equal([Folder], arguments);
    }

    [Fact]
    public void macOS_opens_the_folder_itself()
        => AssertOpens(RevealInFileManager.PlatformKind.MacOS, "open");

    [Fact]
    public void Windows_opens_the_folder_itself()
        => AssertOpens(RevealInFileManager.PlatformKind.Windows, "explorer.exe");

    [Fact]
    public void Elsewhere_the_folder_itself_is_opened()
        => AssertOpens(RevealInFileManager.PlatformKind.Other, "xdg-open");

    [Fact]
    public void Opening_a_folder_is_not_the_same_command_as_revealing_it()
    {
        foreach (var platform in Enum.GetValues<RevealInFileManager.PlatformKind>())
        {
            Assert.NotEqual(
                RevealInFileManager.BuildCommand(Folder, platform),
                RevealInFileManager.BuildOpenCommand(Folder, platform));
        }
    }

    [Fact]
    public void The_menu_says_open_rather_than_reveal()
    {
        Assert.Contains(
            RevealInFileManager.OpenFolderMenuHeader,
            (string[])["Open in Finder", "Open in Explorer", "Open in File Manager"]);

        Assert.NotEqual(RevealInFileManager.MenuHeader, RevealInFileManager.OpenFolderMenuHeader);
    }
}
