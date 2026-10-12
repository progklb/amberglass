using System.Text.Json;
using Avalonia.Media;
using Amberglass.Core.Models;
using Amberglass.UI.Services;
using Amberglass.UI.ViewModels;
using Xunit;

namespace Amberglass.Tests;

public class ThemeTests
{
    /// <summary>
    /// The overlay the thumbnail grid and preview were painted with before they were themed: white
    /// at 6% over whatever sat beneath them.
    /// </summary>
    private const byte OverlayAlpha = 0x10;

    /// <summary>Source-over compositing of an opaque-white overlay onto an opaque background.</summary>
    private static byte Composite(byte background, byte overlayAlpha)
    {
        var alpha = overlayAlpha / 255d;
        return (byte)Math.Round((255 * alpha) + (background * (1 - alpha)));
    }

    /// <summary>
    /// The guard on the whole change: Darkroom has to be the application exactly as it was. The
    /// panels used to be a translucent white laid over a black window, and are now an opaque colour;
    /// this proves the two land on the same pixel rather than merely looking similar.
    /// </summary>
    [Fact]
    public void DarkroomReproducesTheOriginalUnthemedColours()
    {
        var palette = AppThemes.For(AppTheme.Darkroom);

        Assert.Equal(Color.FromRgb(0, 0, 0), palette.Surface);

        var composited = Composite(palette.Surface.R, OverlayAlpha);
        Assert.Equal(Color.FromRgb(composited, composited, composited), palette.Panel);
    }

    /// <summary>Graphite is the grid's tone everywhere: no panel sits lighter than its neighbour.</summary>
    [Fact]
    public void GraphiteIsFlat()
    {
        var graphite = AppThemes.For(AppTheme.Graphite);

        Assert.Equal(graphite.Surface, graphite.Panel);
        Assert.Equal(AppThemes.For(AppTheme.Darkroom).Panel, graphite.Surface);
    }

    /// <summary>
    /// Themes are opaque; a translucent surface would let the desktop through the window. Driven off
    /// the enum rather than a list of cases, so a theme added later is covered without being added
    /// here — the failure this guards against is one nobody remembers to write a case for.
    /// </summary>
    [Fact]
    public void EveryThemeIsOpaque()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var palette = AppThemes.For(theme);

            Assert.Equal(255, palette.Surface.A);
            Assert.Equal(255, palette.Panel.A);
        });
    }

    /// <summary>
    /// Themes have to be told apart. Two entries resolving to the same palette would leave the
    /// dropdown offering a choice that does nothing.
    /// </summary>
    [Fact]
    public void EveryThemeIsDistinct()
    {
        var palettes = Enum.GetValues<AppTheme>().Select(AppThemes.For).ToArray();

        Assert.Equal(palettes.Length, palettes.Distinct().Count());
    }

    /// <summary>
    /// A selection has to be clearly lighter than the tile it lands on. A tile is the panel plus 9%
    /// white, so a selection near that value reads as a hover rather than as a choice — which is
    /// exactly the failure a hand-picked colour drifts into.
    /// </summary>
    [Fact]
    public void EveryThemeSelectionOutranksItsTile()
    {
        const byte TileOverlay = 0x18;

        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var palette = AppThemes.For(theme);
            var tile = Composite(palette.Panel.R, TileOverlay);

            Assert.True(palette.Selection.R > tile + 8 || palette.Selection.G > tile + 8,
                $"{theme}'s selection does not stand clear of its tile.");
        });
    }

    /// <summary>
    /// Every theme stays dark enough to judge a photograph against. A light surface would be a
    /// different product, and would arrive by way of someone nudging a colour rather than deciding.
    /// Selection is excluded — it is meant to stand out.
    /// </summary>
    [Fact]
    public void EveryThemeStaysDark()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var palette = AppThemes.For(theme);

            foreach (var colour in new[] { palette.Surface, palette.Panel })
            {
                // Rec. 601 luma, which weights green the way the eye does.
                var luma = (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B);
                Assert.True(luma < 80, $"{theme} has a surface at luma {luma:F0}, which is not dark.");
            }
        });
    }

    /// <summary>
    /// A settings file naming a theme this build does not have must still open, looking like the
    /// application rather than like an unpainted window.
    /// </summary>
    [Fact]
    public void UnknownThemeFallsBackToDarkroom()
    {
        Assert.Equal(AppThemes.For(AppTheme.Darkroom), AppThemes.For((AppTheme)999));
    }

    [Fact]
    public void DefaultsToDarkroom()
    {
        Assert.Equal(AppTheme.Darkroom, AppSettings.Default.Theme);
    }

    /// <summary>
    /// Themes are persisted as their pinned numbers, so the value a previous version wrote has to
    /// still mean the same theme.
    /// </summary>
    [Fact]
    public void ThemeSurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { Theme = AppTheme.Graphite };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(AppTheme.Graphite, restored.Theme);
    }

    [Fact]
    public void PinnedThemeNumbersDoNotMove()
    {
        Assert.Equal(0, (int)AppTheme.Darkroom);
        Assert.Equal(1, (int)AppTheme.Graphite);
        Assert.Equal(2, (int)AppTheme.Safelight);
        Assert.Equal(3, (int)AppTheme.Verdigris);
        Assert.Equal(4, (int)AppTheme.Amberlight);
    }

    // ---- Selection colour ----------------------------------------------------------------------

    /// <summary>
    /// The accent is the whole mechanism now, so the two modes must genuinely differ: Theme paints
    /// from the palette, System leaves the platform's colour alone by removing the override.
    /// </summary>
    [Fact]
    public void ThemeAccentComesFromThePalette()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
            Assert.Equal(AppThemes.For(theme).Selection, AppThemes.AccentRamp(AppThemes.For(theme).Selection)[0]));
    }

    [Fact]
    public void SelectionColourDefaultsToSystemSoNothingChangesUnasked()
    {
        Assert.Equal(SelectionColour.System, AppSettings.Default.SelectionColour);
        Assert.Equal(0, (int)SelectionColour.System);
        Assert.Equal(1, (int)SelectionColour.Theme);
    }

    [Fact]
    public void SelectionColourSurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { SelectionColour = SelectionColour.Theme };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(SelectionColour.Theme, restored.SelectionColour);
    }

    /// <summary>
    /// The accent ramp has to be the one Fluent would have produced itself, or an accented control
    /// changes character under the pointer — hover and pressed states come from the variants, not
    /// the base. The expected values here were read out of a running Avalonia on macOS, given the
    /// platform accent it had resolved, so this pins the derivation to something observed rather
    /// than to ratios that looked about right.
    /// </summary>
    [Fact]
    public void AccentRampMatchesTheOneAvaloniaDerives()
    {
        var platformAccent = Color.FromRgb(0x00, 0x7A, 0xFF);

        Color[] avalonia =
        [
            Color.FromRgb(0x00, 0x7A, 0xFF), // base
            Color.FromRgb(0x4E, 0xA3, 0xFF), // Light1
            Color.FromRgb(0x8C, 0xC3, 0xFF), // Light2
            Color.FromRgb(0xCE, 0xE5, 0xFF), // Light3
            Color.FromRgb(0x00, 0x5F, 0xC6), // Dark1
            Color.FromRgb(0x00, 0x4B, 0x9D), // Dark2
            Color.FromRgb(0x00, 0x33, 0x6A)  // Dark3
        ];

        var ramp = AppThemes.AccentRamp(platformAccent);

        Assert.Equal(avalonia.Length, ramp.Count);

        for (var i = 0; i < avalonia.Length; i++)
        {
            // Within a point or two per channel: the ratios were recovered from rounded bytes.
            Assert.True(
                Math.Abs(avalonia[i].R - ramp[i].R) <= 2
                && Math.Abs(avalonia[i].G - ramp[i].G) <= 2
                && Math.Abs(avalonia[i].B - ramp[i].B) <= 2,
                $"step {i}: expected about {avalonia[i]}, got {ramp[i]}");
        }
    }

    /// <summary>Lighter steps must actually lighten and darker ones darken, for any accent.</summary>
    [Fact]
    public void AccentRampIsMonotonic()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var ramp = AppThemes.AccentRamp(AppThemes.For(theme).Selection);

            static int Luma(Color c) => (int)((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B));

            Assert.True(Luma(ramp[1]) > Luma(ramp[0]), $"{theme}: Light1 is not lighter than the base.");
            Assert.True(Luma(ramp[2]) > Luma(ramp[1]), $"{theme}: Light2 is not lighter than Light1.");
            Assert.True(Luma(ramp[3]) > Luma(ramp[2]), $"{theme}: Light3 is not lighter than Light2.");

            Assert.True(Luma(ramp[4]) < Luma(ramp[0]), $"{theme}: Dark1 is not darker than the base.");
            Assert.True(Luma(ramp[5]) < Luma(ramp[4]), $"{theme}: Dark2 is not darker than Dark1.");
            Assert.True(Luma(ramp[6]) < Luma(ramp[5]), $"{theme}: Dark3 is not darker than Dark2.");
        });
    }

    // ---- Hand-drawn selection ------------------------------------------------------------------

    /// <summary>
    /// An experiment nobody opted into is a bug. This is the guard on that, and it also pins the
    /// stored numbers so a saved preference keeps meaning what it meant.
    /// </summary>
    [Fact]
    public void HandDrawnSelectionIsOffUntilAskedFor()
    {
        Assert.Equal(SelectionStyle.Standard, AppSettings.Default.SelectionStyle);
        Assert.Equal(0, (int)SelectionStyle.Standard);
        Assert.Equal(1, (int)SelectionStyle.HandDrawn);
        Assert.True(AppSettings.Default.HandDrawnAnimates);
    }

    /// <summary>
    /// Roughness is clamped rather than trusted. A hand-edited settings file carrying a wild value
    /// would otherwise produce a ring that wanders clean off the row it is meant to mark.
    /// </summary>
    [Theory]
    [InlineData(-40, AppSettings.MinRoughness)]
    [InlineData(0, AppSettings.MinRoughness)]
    [InlineData(1.0, 1.0)]
    [InlineData(999, AppSettings.MaxRoughness)]
    public void RoughnessIsClampedToAUsableRange(double stored, double expected)
    {
        var settings = AppSettings.Default with { HandDrawnRoughness = stored };

        Assert.Equal(expected, settings.ClampedRoughness);
    }

    /// <summary>
    /// The pencil takes the selection's hue but not its value. Those colours were chosen to sit
    /// behind text as a filled block; a one-and-a-half pixel line has no area to carry a dark tone
    /// and reads as a smudge. This asserts the lift is real, and that it is a lift rather than a
    /// wash — a colour blended so far towards white that the hue is gone would "match the theme"
    /// only in the sense that white matches everything.
    /// </summary>
    [Fact]
    public void RingInkLiftsTheSelectionColourWithoutLosingItsHue()
    {
        Assert.All(Enum.GetValues<AppTheme>(), theme =>
        {
            var selection = AppThemes.For(theme).Selection;
            var ink = AppThemes.InkFor(selection);

            static int Luma(Color c) => (int)((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B));

            Assert.True(Luma(ink) > Luma(selection) + 20,
                $"{theme}: ink at luma {Luma(ink)} is not clear of the selection at {Luma(selection)}.");

            // The channel that led in the selection must still lead in the ink, so a red theme does
            // not acquire a green pencil.
            var selectionMax = Math.Max(selection.R, Math.Max(selection.G, selection.B));
            var inkMax = Math.Max(ink.R, Math.Max(ink.G, ink.B));

            Assert.Equal(
                selection.R == selectionMax ? 'r' : selection.G == selectionMax ? 'g' : 'b',
                ink.R == inkMax ? 'r' : ink.G == inkMax ? 'g' : 'b');
        });
    }

    // ---- Interface font ------------------------------------------------------------------------

    [Fact]
    public void FontDefaultsToTheSystemOne()
    {
        Assert.Equal(UiFont.System, AppSettings.Default.UiFont);
        Assert.Equal(0, (int)UiFont.System);
        Assert.Equal(1, (int)UiFont.Andika);
        Assert.Equal(2, (int)UiFont.Delius);
    }

    [Fact]
    public void FontSurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { UiFont = UiFont.Andika };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(UiFont.Andika, restored.UiFont);
    }

    /// <summary>
    /// A font in the enum but not in the dropdown would be unreachable, and would leave the dropdown
    /// blank for anyone whose settings already named it.
    /// </summary>
    [Fact]
    public void EveryFontIsOfferedInSettings()
    {
        var offered = SettingsViewModel.Fonts.Select(choice => choice.Font).ToArray();

        Assert.Equal(Enum.GetValues<UiFont>(), offered);
        Assert.All(SettingsViewModel.Fonts, choice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.Name));
            Assert.False(string.IsNullOrWhiteSpace(choice.Description));
        });
    }

    [Fact]
    public void HandDrawnSettingsSurviveARoundTrip()
    {
        var settings = AppSettings.Default with
        {
            SelectionStyle = SelectionStyle.HandDrawn,
            HandDrawnRoughness = 1.4,
            HandDrawnAnimates = false
        };

        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal(SelectionStyle.HandDrawn, restored.SelectionStyle);
        Assert.Equal(1.4, restored.HandDrawnRoughness);
        Assert.False(restored.HandDrawnAnimates);
    }

    [Fact]
    public void EverySelectionColourIsOfferedInSettings()
    {
        var offered = SettingsViewModel.SelectionColours.Select(choice => choice.Source).ToArray();

        Assert.Equal(Enum.GetValues<SelectionColour>(), offered);
    }

    /// <summary>
    /// Every theme must be offerable. A theme added to the enum but not to the dropdown would be
    /// unreachable, and would leave the dropdown blank for anyone whose settings already named it.
    /// </summary>
    [Fact]
    public void EveryThemeIsOfferedInSettings()
    {
        var offered = SettingsViewModel.Themes.Select(choice => choice.Theme).ToArray();

        Assert.Equal(Enum.GetValues<AppTheme>(), offered);
        Assert.All(SettingsViewModel.Themes, choice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.Name));
            Assert.False(string.IsNullOrWhiteSpace(choice.Description));
        });
    }
}

/// <summary>
/// The interface brightness setting: a darkroom is dim so the eye adapts to the print rather than
/// to the room.
/// </summary>
public class InterfaceBrightnessTests
{
    [Fact]
    public void Full_brightness_leaves_a_colour_exactly_as_it_was()
    {
        var colour = Color.FromRgb(0x18, 0x05, 0x05);

        Assert.Equal(colour, AppThemes.Dim(colour, 1.0));
    }

    [Fact]
    public void Half_brightness_halves_every_channel()
    {
        var dimmed = AppThemes.Dim(Color.FromRgb(200, 100, 50), 0.5);

        Assert.Equal(100, dimmed.R);
        Assert.Equal(50, dimmed.G);
        Assert.Equal(25, dimmed.B);
    }

    /// <summary>Scaled, not blended, so a red theme stays red rather than drifting grey.</summary>
    [Fact]
    public void Dimming_keeps_the_hue()
    {
        var dimmed = AppThemes.Dim(Color.FromRgb(0x18, 0x05, 0x05), 0.5);

        Assert.True(dimmed.R > dimmed.G);
        Assert.Equal(dimmed.G, dimmed.B);
    }

    [Fact]
    public void Alpha_is_untouched()
        => Assert.Equal(128, AppThemes.Dim(Color.FromArgb(128, 200, 200, 200), 0.5).A);

    [Fact]
    public void Black_has_nothing_left_to_dim()
        => Assert.Equal(Colors.Black, AppThemes.Dim(Colors.Black, 0.4));

    /// <summary>
    /// The floor is set by the label chips, which are ink on a fixed pale swatch and so lose
    /// contrast as they dim, where the rest of the interface gains it.
    /// </summary>
    [Fact]
    public void The_setting_is_clamped_to_something_still_readable()
    {
        Assert.Equal(AppSettings.MinBrightness, (AppSettings.Default with { InterfaceBrightness = 0.01 }).ClampedBrightness);
        Assert.Equal(AppSettings.MaxBrightness, (AppSettings.Default with { InterfaceBrightness = 9.0 }).ClampedBrightness);
    }

    [Fact]
    public void It_starts_at_full_brightness()
        => Assert.Equal(1.0, AppSettings.Default.ClampedBrightness);

    [Fact]
    public void BrightnessSurvivesARoundTrip()
    {
        var settings = AppSettings.Default with { InterfaceBrightness = 0.6 };

        var json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(0.6, back!.ClampedBrightness);
    }
}
