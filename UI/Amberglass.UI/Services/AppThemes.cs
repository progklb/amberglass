using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Amberglass.Core.Models;
using Amberglass.UI.Controls;

namespace Amberglass.UI.Services;

/// <summary>The colours a theme is made of.</summary>
/// <param name="Surface">
/// The application's chrome: toolbars, the folder tree, the metadata panel, the status bar.
/// </param>
/// <param name="Panel">
/// The working surfaces — the thumbnail grid and the preview. In some themes this is a step lighter
/// than <paramref name="Surface"/>, in others the same colour.
/// </param>
/// <param name="Selection">
/// What a selected thumbnail or folder is filled with when the selection is set to follow the theme.
/// Only used in that case — under <see cref="SelectionColour.System"/> the platform's colour wins.
/// </param>
public sealed record ThemePalette(Color Surface, Color Panel, Color Selection);

/// <summary>
/// What each theme paints, and the resource keys the rest of the application reads them through.
///
/// The surfaces stay deliberately few, which is the whole reason this remains small: almost every
/// other colour in the application is already a translucent white or black laid <i>over</i> one of
/// them — splitters at 13% white, badges at 69% black, the tile behind a thumbnail at 9% white.
/// Those composite against whatever is beneath, so redefining the surfaces repaints everything
/// resting on them and no border or overlay has to be restated per theme.
///
/// That is also why <see cref="AppTheme.Darkroom"/> can be expressed as opaque colours without
/// changing anything: black with the grid's 6% white overlay composites to exactly #101010, which
/// is what the application was already painting.
/// </summary>
public static class AppThemes
{
    public const string SurfaceKey = "AppSurfaceBrush";
    public const string PanelKey = "AppPanelBrush";

    /// <summary>
    /// The key Fluent publishes the platform's own accent under. Read rather than asked of
    /// <c>PlatformSettings</c>, which needs a window to exist — the theme is applied before there
    /// is one.
    /// </summary>
    private const string PlatformAccentKey = "SystemAccentColor";

    /// <summary>Used only if the platform accent cannot be read at all.</summary>
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x7A, 0xFF);

    private static Color? ReadPlatformAccent(Application application)
        => application.TryGetResource(PlatformAccentKey, application.ActualThemeVariant, out var value)
            && value is Color colour
                ? colour
                : null;

    /// <summary>
    /// Selection colours are a clear step lighter than the tile they land on — a tile is the panel
    /// plus 9% white, so anything close to that reads as a hover rather than a choice.
    /// </summary>
    private static readonly ThemePalette Darkroom = new(
        Surface: Color.FromRgb(0x00, 0x00, 0x00),
        Panel: Color.FromRgb(0x10, 0x10, 0x10),
        Selection: Color.FromRgb(0x4A, 0x4A, 0x4A));

    /// <summary>
    /// Deliberately flat: both surfaces are the tone Darkroom uses for the grid alone. Panels are
    /// then told apart by their splitters and borders rather than by a change of shade.
    /// </summary>
    private static readonly ThemePalette Graphite = new(
        Surface: Color.FromRgb(0x10, 0x10, 0x10),
        Panel: Color.FromRgb(0x10, 0x10, 0x10),
        Selection: Color.FromRgb(0x4A, 0x4A, 0x4A));

    /// <summary>
    /// Red kept very dark and only moderately saturated. A safelight is dim on purpose, and the
    /// colour has to sit behind photographs without casting the eye — a bright red would tint the
    /// judgement of every warm image in the grid.
    /// </summary>
    private static readonly ThemePalette Safelight = new(
        Surface: Color.FromRgb(0x18, 0x05, 0x05),
        Panel: Color.FromRgb(0x28, 0x09, 0x09),
        Selection: Color.FromRgb(0x6B, 0x1A, 0x1A));

    /// <summary>
    /// Pitched well below the application it takes its hue from: that one paints a writing canvas,
    /// where this sits behind photographs and has to stay out of their contrast. The first attempt
    /// kept the borrowed value as well as the hue and read as loud next to the other themes, so the
    /// value came down and the hue stayed.
    /// </summary>
    private static readonly ThemePalette Verdigris = new(
        Surface: Color.FromRgb(0x08, 0x1B, 0x1D),
        Panel: Color.FromRgb(0x0D, 0x27, 0x29),
        Selection: Color.FromRgb(0x1C, 0x53, 0x4C));

    public static ThemePalette For(AppTheme theme) => theme switch
    {
        AppTheme.Graphite => Graphite,
        AppTheme.Safelight => Safelight,
        AppTheme.Verdigris => Verdigris,

        // Darkroom is also the fallback: a settings file naming a theme this build has never heard
        // of should open looking like the application, not fail to start.
        _ => Darkroom
    };

    /// <summary>
    /// Repaints the application. Every consumer binds these keys with <c>DynamicResource</c>, so
    /// replacing them here restyles windows that are already open — there is nothing to reopen and
    /// no restart to prompt for.
    /// </summary>
    public static void Apply(Application application, AppSettings settings)
    {
        var palette = For(settings.Theme);
        var brightness = settings.ClampedBrightness;

        application.Resources[SurfaceKey] = new SolidColorBrush(Dim(palette.Surface, brightness));
        application.Resources[PanelKey] = new SolidColorBrush(Dim(palette.Panel, brightness));

        // Text as well as surfaces, and this is the half that does the work. The surfaces are
        // already almost black, so scaling them moves little — under Darkroom, whose surface is
        // black, it moves nothing at all. What is actually bright in this application is the ink:
        // white text, and the translucent whites laid over the surfaces. Dimming the inherited
        // foreground takes the glare off without touching a photograph.
        var ink = new SolidColorBrush(Dim(Colors.White, brightness));
        application.Resources[ForegroundKey] = ink;
        application.Resources[FluentInkKey] = ink;

        // A ToggleButton is the one control a style cannot reach. Its template hands the content
        // presenter a colour from the theme's own resources, which arrives as a local value — and a
        // local value outranks every style, so neither setting it on the control nor on the
        // presenter nor on the text moved "RAW", "Keep" or "Unflagged" off full white. Found by
        // probing: these four keys are what it reads, and nothing else did anything.
        foreach (var key in ToggleButtonInkKeys)
        {
            application.Resources[key] = ink;
        }

        ApplyDimming(application, ink, brightness);

        // The accent is applied first and hands back the colour that ended up in force, so the ring
        // can be tinted to match it. Returned rather than read back out of the resources: reading
        // it back is precisely the bug that once painted a system-coloured selection in the previous
        // theme's colour, because the read happened while this application's own override was still
        // in the dictionary.
        var accent = ApplyAccent(application, settings);

        ApplySelectionStyle(application, settings, accent);
        ApplyContactSheet(application, settings, brightness);
        ApplyFont(application, settings);
    }

    public const string ContactSheetKey = "AppContactSheet";

    /// <summary>
    /// Whether a tile's corner badge still carries the flag. The inverse of the experiment,
    /// published separately because the badge binds to it and a binding cannot negate a resource.
    /// </summary>
    public const string VerdictInBadgeKey = "AppVerdictInBadge";

    public const string ChinagraphInkKey = "AppChinagraphInk";
    public const string EdgePrintInkKey = "AppEdgePrintInk";
    public const string SplitterWidthKey = "AppSplitterWidth";

    /// <summary>
    /// Grease pencil. Waxy red, well short of pure: a chinagraph is a dull, dense colour, and a
    /// saturated one would sit on top of a photograph rather than on it.
    /// </summary>
    private static readonly Color Chinagraph = Color.FromRgb(0xC4, 0x36, 0x2B);

    /// <summary>
    /// The amber that film edge print is exposed in. The same value the Orange label uses, which
    /// is not a coincidence worth breaking — it means the rebate and a label never clash.
    /// </summary>
    private static readonly Color EdgePrint = Color.FromRgb(0xE8, 0x94, 0x4A);

    /// <summary>The bar between panes. Four points is a line; twelve is room for a perforation.</summary>
    public static double SplitterWidthFor(bool contactSheet) => contactSheet ? 12 : 4;

    /// <summary>
    /// Publishes the contact sheet's settings. The class that switches the splitters is set on
    /// the window from <see cref="ContactSheetKey"/>, for the reason
    /// <see cref="ApplySelectionStyle"/> gives.
    ///
    /// The chinagraph is not dimmed with the interface. It is drawn on the photograph, in the same
    /// place as the label bar, and like the label bar it is part of what is being judged about the
    /// picture rather than of the room the picture is in. The edge print is chrome, and dims.
    /// </summary>
    private static void ApplyContactSheet(Application application, AppSettings settings, double brightness)
    {
        var on = settings.ContactSheet;

        application.Resources[ContactSheetKey] = on;
        application.Resources[VerdictInBadgeKey] = !on;
        application.Resources[ChinagraphInkKey] = new SolidColorBrush(Chinagraph);
        application.Resources[EdgePrintInkKey] = new SolidColorBrush(Dim(EdgePrint, brightness));
        application.Resources[SplitterWidthKey] = new GridLength(SplitterWidthFor(on));
    }

    public const string ForegroundKey = "AppForegroundBrush";

    /// <summary>
    /// The key Fluent's control themes read their text colour from. Overridden alongside our own so
    /// controls dim with everything else.
    /// </summary>
    private const string FluentInkKey = "ThemeForegroundBrush";

    /// <summary>
    /// The keys a ToggleButton draws its content with. Its template passes the colour to the content
    /// presenter from within the template, where it becomes a local value that no style can outrank,
    /// so the resource is the only way in.
    /// </summary>
    private static readonly string[] ToggleButtonInkKeys =
    [
        "ToggleButtonForeground",
        "ToggleButtonForegroundChecked",
        "ToggleButtonForegroundPointerOver",
        "ToggleButtonForegroundCheckedPointerOver"
    ];

    /// <summary>The dimming styles currently in force, so they can be replaced or taken away.</summary>
    private static Styles? _dimming;

    /// <summary>
    /// Dims the ink by styling it directly rather than by handing a colour down the tree.
    ///
    /// Inheritance was not enough, and the measurements said so: at 35% the status bar dimmed by 65%
    /// while the folder tree and the thumbnail filenames did not move at all. Text inside an item
    /// template sits in a ListBoxItem or a TreeViewItem whose own control theme sets a foreground,
    /// and a control that sets one does not inherit one. A style outranks a control theme, so this
    /// reaches them; a colour written in the markup outranks a style, so the badges that are
    /// deliberately blue or amber keep their colour.
    ///
    /// <para>Added and removed rather than always present. At full brightness there is no style at
    /// all, so nothing changes for anyone who leaves the slider alone — which matters, because this
    /// would otherwise flatten the greys a control theme uses for disabled and selected text.</para>
    /// </summary>
    private static void ApplyDimming(Application application, IBrush ink, double brightness)
    {
        if (_dimming is not null)
        {
            application.Styles.Remove(_dimming);
            _dimming = null;
        }

        if (brightness >= 1.0)
        {
            return;
        }

        var text = new Style(x => x.OfType<TextBlock>());
        text.Setters.Add(new Setter(TextBlock.ForegroundProperty, ink));

        // Every templated control as well, which is a wider net than it first looks and is needed.
        // A CheckBox draws its label through a ContentPresenter, and that hands the text a
        // foreground as a local value — which outranks a style, so the TextBlock rule above never
        // reached it and "Include subfolders" stayed at full white while everything around it dimmed.
        // Setting it on the control is upstream of that. It also covers the drawn icons, since a
        // PathIcon is one of these too.
        var controls = new Style(x => x.Is<TemplatedControl>());
        controls.Setters.Add(new Setter(TemplatedControl.ForegroundProperty, ink));

        // And the presenters, which is where a CheckBox's label actually gets its colour: Fluent
        // hands its ContentPresenter a per-state brush of its own rather than passing the control's
        // Foreground down, so setting it on the CheckBox never reached the words beside the tick.
        var presenters = new Style(x => x.OfType<ContentPresenter>());
        presenters.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, ink));

        _dimming = [controls, presenters, text];
        application.Styles.Add(_dimming);
    }

    /// <summary>
    /// Scales a colour towards black, keeping its hue.
    ///
    /// Multiplied per channel rather than blended towards black, which comes to the same thing for
    /// an opaque colour and says more plainly what it is: at 0.5 every channel is half as bright.
    /// </summary>
    internal static Color Dim(Color colour, double brightness)
    {
        static byte Scale(byte channel, double by) => (byte)Math.Clamp(Math.Round(channel * by), 0, 255);

        return brightness >= 1.0
            ? colour
            : Color.FromArgb(colour.A, Scale(colour.R, brightness), Scale(colour.G, brightness), Scale(colour.B, brightness));
    }

    public const string FontKey = "AppFontFamily";

    /// <summary>
    /// Where the bundled faces live. The family name after the hash is the one recorded inside the
    /// font file, not the file name — a wrong name here falls back to the system font silently,
    /// which looks exactly like the setting not being wired up.
    /// </summary>
    private const string Bundled = "avares://Amberglass/Assets/Fonts#";

    private static void ApplyFont(Application application, AppSettings settings)
    {
        var family = settings.UiFont switch
        {
            UiFont.Andika => new FontFamily(Bundled + "Andika"),
            UiFont.Delius => new FontFamily(Bundled + "Delius"),
            _ => FontFamily.Default
        };

        application.Resources[FontKey] = family;
    }

    public const string RingEnabledKey = "AppRingEnabled";
    public const string RingRoughnessKey = "AppRingRoughness";
    public const string RingAnimatesKey = "AppRingAnimates";

    public const string RingInkKey = "AppRingInk";

    /// <summary>
    /// The ink, taken from whatever colour a selection is currently drawn in — but lifted.
    ///
    /// Not the raw selection colour, and the reason is not aesthetic. Those colours were picked to
    /// sit <i>behind</i> text as a filled block, where a large area carries a dark tone perfectly
    /// well. A one-and-a-half pixel line has no area to carry it, and the same value that reads as
    /// a solid highlight reads as a smudge. Light1 of the accent ramp keeps the hue plainly — teal
    /// stays teal, red stays red — while lifting it enough to be a line.
    /// </summary>
    public static Color InkFor(Color selection) => AccentRamp(selection)[1];

    /// <summary>
    /// Publishes the ring's settings. The style that suppresses the filled row lives in App.axaml
    /// and is switched by a class on the tree, not added and removed here.
    ///
    /// That is not a stylistic preference. Adding and removing the style from
    /// <c>Application.Styles</c> was tried first and half worked: switching the experiment on
    /// suppressed the fill, and switching it off did <b>not</b> bring it back, leaving a selected
    /// folder with no mark at all until the application was restarted. Removing a style does not
    /// revert a setter that has already been applied to a realised template part. A class does —
    /// re-evaluating on a class change is the mechanism the styling system is built around.
    /// </summary>
    private static void ApplySelectionStyle(Application application, AppSettings settings, Color accent)
    {
        application.Resources[RingEnabledKey] = settings.SelectionStyle == SelectionStyle.HandDrawn;
        application.Resources[RingRoughnessKey] = settings.ClampedRoughness;
        application.Resources[RingAnimatesKey] = settings.HandDrawnAnimates;
        application.Resources[RingInkKey] = new SolidColorBrush(InkFor(accent));

        // Generated rather than authored as a fixed path, so the tick wanders by the same amount as
        // every other pencil in the application and follows the roughness slider with them. A
        // hand-written squiggle would have been one more thing to keep in step by eye.
        application.Resources[TickGeometryKey] =
            RoughGeometry.Tick(TickSize, TickSeed, settings.ClampedRoughness);
    }

    public const string TickGeometryKey = "AppTickGeometry";

    /// <summary>The square the tick is authored in; Fluent's Viewbox scales it to the checkbox.</summary>
    private const double TickSize = 24;

    /// <summary>
    /// Fixed, so every checkbox in the application wears the same tick. They are read as a set —
    /// a column of them each wobbling differently would look like a fault rather than a hand.
    /// </summary>
    private const int TickSeed = 8731;

    /// <summary>
    /// Sets the accent, which is what actually colours a selection.
    ///
    /// Everything selectable is left to Fluent to paint from this one colour rather than overridden
    /// per control. An earlier version did override the list and tree directly, and it was wrong in
    /// a way worth recording: Fluent paints the tree row from the accent <i>and</i> the header
    /// presenter sits inside that row, so overriding the presenter produced a second band in a
    /// different colour nested inside the first. Setting the source colour and letting the theme
    /// distribute it cannot produce that class of mismatch, and needs no template part names — which
    /// are neither public API nor stable.
    ///
    /// Under <see cref="SelectionColour.System"/> the overrides are <b>removed</b> rather than set to
    /// the platform colour, so lookups fall through to the ones Fluent derived from the operating
    /// system. Writing the values back would freeze them, and the point of System is that it follows
    /// a change made in System Settings.
    /// </summary>
    private static Color ApplyAccent(Application application, AppSettings settings)
    {
        if (settings.SelectionColour != SelectionColour.Theme)
        {
            foreach (var key in AccentKeys)
            {
                application.Resources.Remove(key);
            }

            // Read only after the overrides are gone, so this is the platform's value and not one
            // of ours. Kept inside this method so the ordering cannot be got wrong from outside.
            return ReadPlatformAccent(application) ?? FallbackAccent;
        }

        // Fluent expects a ramp, not a single colour: the variants are what a checkbox uses when it
        // is hovered, pressed or disabled. Supplying only the base would leave those states on the
        // platform blue, which is worse than not overriding at all — the control would change colour
        // under the pointer.
        var accent = For(settings.Theme).Selection;
        var ramp = AccentRamp(accent);

        for (var i = 0; i < AccentKeys.Length; i++)
        {
            application.Resources[AccentKeys[i]] = ramp[i];
        }

        return accent;
    }

    /// <summary>
    /// The seven colours Fluent wants for an accent, in the order of <see cref="AccentKeys"/>:
    /// the base, three lighter, three darker.
    /// </summary>
    public static IReadOnlyList<Color> AccentRamp(Color accent)
    {
        var ramp = new Color[AccentKeys.Length];
        ramp[0] = accent;

        for (var i = 0; i < LightenSteps.Length; i++)
        {
            ramp[i + 1] = Lighten(accent, LightenSteps[i]);
            ramp[i + 4] = Darken(accent, DarkenSteps[i]);
        }

        return ramp;
    }

    /// <summary>
    /// Order matters: base, Light1-3, Dark1-3, matching the indexing in <see cref="ApplyAccent"/>.
    /// </summary>
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3"
    ];

    /// <summary>
    /// The ramp Avalonia itself derives from the platform accent, measured from what it publishes
    /// rather than guessed — lighter shades blend towards white, darker ones scale down. Matching it
    /// means the accented controls behave the same whichever source the colour came from.
    /// </summary>
    private static readonly double[] LightenSteps = [0.30, 0.55, 0.81];

    private static readonly double[] DarkenSteps = [0.78, 0.62, 0.42];

    private static Color Lighten(Color colour, double towardsWhite) => Color.FromRgb(
        (byte)Math.Round(colour.R + ((255 - colour.R) * towardsWhite)),
        (byte)Math.Round(colour.G + ((255 - colour.G) * towardsWhite)),
        (byte)Math.Round(colour.B + ((255 - colour.B) * towardsWhite)));

    private static Color Darken(Color colour, double scale) => Color.FromRgb(
        (byte)Math.Round(colour.R * scale),
        (byte)Math.Round(colour.G * scale),
        (byte)Math.Round(colour.B * scale));

}
