using BetterDAM.Core.Models;
using BetterDAM.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BetterDAM.Tests;

/// <summary>
/// The arrangement of the window, which is remembered between sessions.
/// </summary>
public class LayoutSettingsTests
{
    [Fact]
    public void A_fresh_install_opens_on_the_designed_layout()
    {
        var layout = LayoutSettings.Default;

        Assert.Equal(160, layout.ClampedThumbnailSize);
        Assert.Equal(240, layout.ClampedFolderPanelWidth);
        Assert.Equal(360, layout.ClampedInspectorWidth);
        Assert.False(layout.FolderPanelCollapsed);
        Assert.False(layout.InspectorCollapsed);
        Assert.False(layout.PreviewOnTop);
    }

    /// <summary>
    /// The file can be hand-edited, and a settings file is a poor reason for a window that cannot
    /// be used. A panel wider than the screen hides the photographs; one nought wide with nothing
    /// recorded as collapsed would leave no way to find it again.
    /// </summary>
    [Theory]
    [InlineData(0, LayoutSettings.MinPanelWidth)]
    [InlineData(-400, LayoutSettings.MinPanelWidth)]
    [InlineData(40, LayoutSettings.MinPanelWidth)]
    [InlineData(5000, LayoutSettings.MaxPanelWidth)]
    [InlineData(300, 300)]
    public void A_panel_width_is_kept_within_what_a_window_can_use(double stored, double expected)
    {
        Assert.Equal(expected, new LayoutSettings { FolderPanelWidth = stored }.ClampedFolderPanelWidth);
        Assert.Equal(expected, new LayoutSettings { InspectorWidth = stored }.ClampedInspectorWidth);
    }

    /// <summary>The slider's own range: a thumbnail outside it could not be dragged back.</summary>
    [Theory]
    [InlineData(0, LayoutSettings.MinThumbnailSize)]
    [InlineData(2000, LayoutSettings.MaxThumbnailSize)]
    [InlineData(200, 200)]
    public void The_thumbnail_size_is_kept_within_the_slider(double stored, double expected)
        => Assert.Equal(expected, new LayoutSettings { ThumbnailSize = stored }.ClampedThumbnailSize);

    [Fact]
    public void The_lower_pane_keeps_a_floor_but_may_be_most_of_the_window()
    {
        Assert.Equal(60, new LayoutSettings { BottomPaneHeight = 0 }.ClampedBottomPaneHeight);
        Assert.Equal(1200, new LayoutSettings { BottomPaneHeight = 1200 }.ClampedBottomPaneHeight);
    }

    /// <summary>
    /// The point of the whole thing: the arrangement has to come back. Through the real settings
    /// file, because a layout that only survives in memory is the bug this fixes.
    /// </summary>
    [Fact]
    public async Task The_arrangement_comes_back_in_the_next_session()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        var arranged = AppSettings.Default with
        {
            Layout = new LayoutSettings
            {
                ThumbnailSize = 240,
                FolderPanelWidth = 300,
                InspectorWidth = 420,
                InspectorCollapsed = true,
                BottomPaneHeight = 500,
                PreviewOnTop = true
            }
        };

        await new JsonSettingsService(path, NullLogger<JsonSettingsService>.Instance).SaveAsync(arranged);

        // A new session, reading the file the last one wrote.
        var reopened = new JsonSettingsService(path, NullLogger<JsonSettingsService>.Instance).Current.Layout;

        Assert.Equal(240, reopened.ThumbnailSize);
        Assert.Equal(300, reopened.FolderPanelWidth);
        Assert.Equal(420, reopened.InspectorWidth);
        Assert.True(reopened.InspectorCollapsed);
        Assert.False(reopened.FolderPanelCollapsed);
        Assert.Equal(500, reopened.BottomPaneHeight);
        Assert.True(reopened.PreviewOnTop);
    }

    /// <summary>
    /// A settings file written before any of this existed has no layout in it at all, and must open
    /// on the designed one rather than on a window with no panels.
    /// </summary>
    [Fact]
    public void Settings_from_an_older_build_open_on_the_defaults()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "settings.json");

        File.WriteAllText(path, """{ "Theme": 1, "StackPairs": true }""");

        var settings = new JsonSettingsService(path, NullLogger<JsonSettingsService>.Instance).Current;

        Assert.True(settings.StackPairs);
        Assert.Equal(LayoutSettings.Default, settings.Layout);
        Assert.Equal(160, settings.Layout.ClampedThumbnailSize);
    }
}
