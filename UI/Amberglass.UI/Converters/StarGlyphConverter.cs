using System.Globalization;
using Avalonia.Data.Converters;

namespace Amberglass.UI.Converters;

/// <summary>
/// Renders one star of the rating strip: filled when the current rating reaches this star's
/// position, hollow otherwise. The position arrives as the ConverterParameter.
/// </summary>
public sealed class StarGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value as int? ?? 0;

        var position = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };

        return rating >= position ? "★" : "☆";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
