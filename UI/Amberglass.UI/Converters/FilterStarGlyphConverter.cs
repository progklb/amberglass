using System.Globalization;
using Avalonia.Data.Converters;
using Amberglass.Core.Services;

namespace Amberglass.UI.Converters;

/// <summary>
/// The glyph for one star in the filter popup.
///
/// Separate from <see cref="StarGlyphConverter"/>, which draws a rating being <i>edited</i> and is
/// always "this many stars". A filter has a second state — exactly this many, rather than this many
/// and up — and the two have to look different, so this one needs both the count and the mode. That
/// is why it is a multi-value converter rather than a parameter on the existing one.
/// </summary>
public sealed class FilterStarGlyphConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var stars = values.Count > 0 && values[0] is int rating ? rating : 0;
        var exact = values.Count > 1 && values[1] is true;

        var position = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };

        return RatingFilterCycle.IsStarFilled(new RatingFilterState(stars, exact), position) ? "★" : "☆";
    }
}
