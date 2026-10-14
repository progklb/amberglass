using System.Globalization;
using Avalonia.Data.Converters;

namespace Amberglass.UI.Converters;

/// <summary>
/// Whether one star of a rating strip is filled: true when the rating reaches this star's position,
/// which arrives as the ConverterParameter. Drives the star's <c>filled</c> class, so the star is
/// one shape that is either filled or outlined — never two glyphs that might come from two fonts.
/// </summary>
public sealed class StarFilledConverter : IValueConverter
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

        return position > 0 && rating >= position;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
