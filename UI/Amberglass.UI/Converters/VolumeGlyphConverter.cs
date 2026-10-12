using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amberglass.UI.Converters;

/// <summary>
/// Picks the speaker icon for the current mute state: a speaker with waves, or one with a cross.
///
/// Returns a <see cref="Geometry"/> rather than a path string because PathIcon.Data is typed, and a
/// binding does not get the string-to-geometry conversion that XAML markup would apply.
/// </summary>
public sealed class VolumeGlyphConverter : IValueConverter
{
    /// <summary>Speaker cone plus two arcs.</summary>
    private static readonly Geometry Audible = Geometry.Parse(
        "M4 9v6h4l5 4V5L8 9H4z M16.5 8.5a5 5 0 0 1 0 7v-2a3 3 0 0 0 0-3v-2z M19 5.5a9 9 0 0 1 0 13v-2.2a6.8 6.8 0 0 0 0-8.6V5.5z");

    /// <summary>The same cone, with a cross where the arcs were.</summary>
    private static readonly Geometry Muted = Geometry.Parse(
        "M4 9v6h4l5 4V5L8 9H4z M16 9.4L17.4 8l2.1 2.1L21.6 8 23 9.4l-2.1 2.1 2.1 2.1-1.4 1.4-2.1-2.1-2.1 2.1L16 13.6l2.1-2.1L16 9.4z");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Muted : Audible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
