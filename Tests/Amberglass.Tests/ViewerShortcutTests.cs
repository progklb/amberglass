using Avalonia.Input;
using Amberglass.UI.Controls;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The viewer's keyboard map. Worth stating rather than inferring from a switch in an event handler,
/// because the one interesting rule looks inconsistent written down and is the opposite in practice.
/// </summary>
public class ViewerShortcutTests
{
    /// <summary>
    /// Space is the universal play/pause. A video that ignored it in favour of re-fitting a frame
    /// that already fits would be the surprising thing — and it is what Space does in the inline
    /// preview, so the two agree.
    /// </summary>
    [Fact]
    public void Space_plays_a_video()
        => Assert.Equal(ViewerAction.TogglePlayback, ViewerShortcuts.Resolve(Key.Space, isVideo: true));

    /// <summary>A still has nothing to play, so the key means what Lightroom's space bar means.</summary>
    [Fact]
    public void Space_fits_a_still()
        => Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(Key.Space, isVideo: false));

    /// <summary>
    /// Fit is never only on Space, or it would be unreachable from the keyboard while a video is on
    /// screen.
    /// </summary>
    [Theory]
    [InlineData(Key.D0)]
    [InlineData(Key.NumPad0)]
    [InlineData(Key.Enter)]
    public void Fit_has_keys_that_work_whatever_is_showing(Key key)
    {
        Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(key, isVideo: true));
        Assert.Equal(ViewerAction.Fit, ViewerShortcuts.Resolve(key, isVideo: false));
    }

    [Theory]
    [InlineData(Key.D1)]
    [InlineData(Key.NumPad1)]
    public void One_is_actual_size(Key key)
        => Assert.Equal(ViewerAction.ActualSize, ViewerShortcuts.Resolve(key, isVideo: false));

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.F)]
    public void Escape_and_f_close(Key key)
        => Assert.Equal(ViewerAction.Close, ViewerShortcuts.Resolve(key, isVideo: false));

    [Fact]
    public void The_arrows_browse()
    {
        Assert.Equal(ViewerAction.Previous, ViewerShortcuts.Resolve(Key.Left, isVideo: false));
        Assert.Equal(ViewerAction.Next, ViewerShortcuts.Resolve(Key.Right, isVideo: false));
    }

    /// <summary>Reported under several names depending on keyboard layout.</summary>
    [Theory]
    [InlineData(Key.OemBackslash)]
    [InlineData(Key.OemPipe)]
    [InlineData(Key.Oem5)]
    public void Backslash_switches_between_raw_and_jpeg(Key key)
        => Assert.Equal(ViewerAction.ToggleRawDevelopment, ViewerShortcuts.Resolve(key, isVideo: false));

    /// <summary>The video player's own convention, kept so the habit works here too.</summary>
    [Fact]
    public void K_plays_a_video_and_does_nothing_to_a_still()
    {
        Assert.Equal(ViewerAction.TogglePlayback, ViewerShortcuts.Resolve(Key.K, isVideo: true));
        Assert.Equal(ViewerAction.None, ViewerShortcuts.Resolve(Key.K, isVideo: false));
    }

    [Fact]
    public void An_unmapped_key_does_nothing()
        => Assert.Equal(ViewerAction.None, ViewerShortcuts.Resolve(Key.Q, isVideo: false));

    /// <summary>The hint has to describe the keys that actually apply to what is on screen.</summary>
    [Fact]
    public void The_hint_follows_the_medium()
    {
        var video = ViewerShortcuts.Hint(isVideo: true);
        var still = ViewerShortcuts.Hint(isVideo: false);

        Assert.Equal("play", Assert.Single(video.Where(h => h.Key == "space")).Action);
        Assert.Equal("fit", Assert.Single(still.Where(h => h.Key == "space")).Action);

        // 0 fits a video, where space is spoken for; a still has no need of it.
        Assert.Contains(video, h => h.Key == "0");
        Assert.DoesNotContain(still, h => h.Key == "0");
    }

    /// <summary>
    /// Every key named in the strip has to be one the viewer answers to, or the hint is a lie. The
    /// pointer gestures are the exception: they are not keys and there is nothing to resolve.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_key_in_the_hint_does_something(bool isVideo)
    {
        var gestures = (string[])["scroll", "drag"];

        foreach (var hint in ViewerShortcuts.Hint(isVideo).Where(h => !gestures.Contains(h.Key)))
        {
            var keys = hint.Key switch
            {
                "space" => (Key[])[Key.Space],
                "0" => [Key.D0],
                "← →" => [Key.Left, Key.Right],
                "\\" => [Key.OemBackslash],
                "b" => [Key.B],
                "esc" => [Key.Escape],
                _ => throw new InvalidOperationException($"The hint names '{hint.Key}', which this test does not know about.")
            };

            Assert.All(keys, key =>
                Assert.NotEqual(ViewerAction.None, ViewerShortcuts.Resolve(key, isVideo)));
        }
    }

    /// <summary>
    /// B greys the picture on screen. A still only: greying video would mean converting every frame
    /// as it arrives, which is a different job from converting one photograph once.
    /// </summary>
    [Fact]
    public void B_toggles_black_and_white_for_a_still()
        => Assert.Equal(
            ViewerAction.ToggleBlackAndWhite,
            ViewerShortcuts.Resolve(Key.B, isVideo: false));

    [Fact]
    public void B_does_nothing_on_a_video()
        => Assert.Equal(ViewerAction.None, ViewerShortcuts.Resolve(Key.B, isVideo: true));

    [Fact]
    public void The_still_hint_offers_black_and_white()
        => Assert.Contains(ViewerShortcuts.Hint(isVideo: false), h => h.Key == "b");

    /// <summary>The hint must not promise a key that does nothing for what is on screen.</summary>
    [Fact]
    public void The_video_hint_does_not_offer_black_and_white()
        => Assert.DoesNotContain(ViewerShortcuts.Hint(isVideo: true), h => h.Key == "b");
}

/// <summary>
/// The luma weights behind the black-and-white preview.
/// </summary>
public class GreyscaleTests
{
    private static byte GreyOf(byte b, byte g, byte r)
    {
        var row = new byte[] { b, g, r, 255 };
        Greyscale.GreyRowBgra(row);
        return row[0];
    }

    [Fact]
    public void All_three_channels_end_up_equal()
    {
        var row = new byte[] { 10, 200, 90, 255 };

        Greyscale.GreyRowBgra(row);

        Assert.Equal(row[0], row[1]);
        Assert.Equal(row[1], row[2]);
    }

    [Fact]
    public void Alpha_is_left_alone()
    {
        var row = new byte[] { 10, 200, 90, 128 };

        Greyscale.GreyRowBgra(row);

        Assert.Equal(128, row[3]);
    }

    [Fact]
    public void Grey_stays_where_it_was()
    {
        Assert.Equal(0, GreyOf(0, 0, 0));
        Assert.Equal(255, GreyOf(255, 255, 255));
        Assert.Equal(128, GreyOf(128, 128, 128));
    }

    /// <summary>
    /// Rec. 709, not a flat average. Green carries most of the brightness and blue almost none, so
    /// a pure green reads far lighter than a pure blue — which is what stops skies coming up milky
    /// and foliage muddy.
    /// </summary>
    [Fact]
    public void Green_reads_lighter_than_red_which_reads_lighter_than_blue()
    {
        var green = GreyOf(0, 255, 0);
        var red = GreyOf(0, 0, 255);
        var blue = GreyOf(255, 0, 0);

        Assert.True(green > red, $"green {green} should outweigh red {red}");
        Assert.True(red > blue, $"red {red} should outweigh blue {blue}");

        // A flat average would put all three at 85.
        Assert.NotEqual(85, green);
    }

    [Fact]
    public void Every_pixel_in_the_row_is_converted()
    {
        var row = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };

        Greyscale.GreyRowBgra(row);

        Assert.Equal(row[0], row[2]);
        Assert.Equal(row[4], row[6]);
        Assert.True(row[4] > row[0], "the green pixel should be lighter than the blue one");
    }
}
