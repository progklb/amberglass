using System.Globalization;
using Avalonia.Data.Converters;

namespace Amberglass.UI.Converters;

/// <summary>
/// The same keyword box means "add these" normally and "these become the whole list" in replace
/// mode, so the heading has to say which.
/// </summary>
public sealed class KeywordHeadingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Replace with these keywords" : "Add these keywords";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
