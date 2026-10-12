using System.Globalization;
using Avalonia.Data.Converters;

namespace Amberglass.UI.Converters;

/// <summary>
/// Whether a tile's judgement badge is worth drawing: rating, flag, and whether the flag is
/// currently the badge's to show.
///
/// Under the contact sheet the flag is written across the picture in pencil instead, and a badge
/// left showing for a flag alone would be an empty dark pill in the corner of a rejected frame.
/// A rating is always the badge's, so a rated file keeps it either way.
/// </summary>
public sealed class TileBadgeConverter : IMultiValueConverter
{
    public static bool IsVisible(bool hasRating, bool hasFlag, bool verdictInBadge)
        => hasRating || (hasFlag && verdictInBadge);

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.Count == 3
           && IsVisible(values[0] is true, values[1] is true, values[2] is true);
}
