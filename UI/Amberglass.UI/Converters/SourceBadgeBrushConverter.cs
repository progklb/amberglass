using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amberglass.UI.Converters;

/// <summary>
/// Tints the viewer's source badge: warm when the developed sensor data is on screen, neutral for
/// anything else.
///
/// Colour rather than text alone because the badge is read in passing, while working through a set —
/// the word confirms, the colour is what actually registers.
/// </summary>
public sealed class SourceBadgeBrushConverter : IValueConverter
{
    private static readonly IBrush Developed = new SolidColorBrush(Color.FromArgb(0xCC, 0x2E, 0x6F, 0x3E));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Developed : Neutral;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
