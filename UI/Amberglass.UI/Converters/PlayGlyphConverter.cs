using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amberglass.UI.Converters;

/// <summary>
/// Picks the transport icon for the current playback state.
///
/// Returns a <see cref="Geometry"/> rather than a character, as the volume glyph does. Emoji were
/// convenient but they are font-dependent: on macOS they render as colour glyphs with their own
/// metrics, so they sit at a different weight and baseline from the drawn icons beside them and the
/// row does not look like one set of controls.
/// </summary>
public sealed class PlayGlyphConverter : IValueConverter
{
    /// <summary>A right-pointing triangle, on the same 24-unit grid as the other icons.</summary>
    private static readonly Geometry Play = Geometry.Parse("M8 5v14l11-7z");

    /// <summary>Two bars, matched in weight to the step icons' bars.</summary>
    private static readonly Geometry Pause = Geometry.Parse("M7 5h3.5v14H7z M13.5 5H17v14h-3.5z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Pause : Play;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// The frame-stepping icons: a triangle against a bar, mirrored about the centre of the grid so the
/// pair reads as one control rather than two similar ones.
/// </summary>
public static class TransportGlyphs
{
    public const string StepBack = "M6 5h2v14H6z M20 5v14L9 12z";

    public const string StepForward = "M4 5v14l11-7z M16 5h2v14h-2z";
}
