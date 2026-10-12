using System.Globalization;
using Avalonia.Data.Converters;
using Amberglass.Core.Models;

namespace Amberglass.UI.Converters;

public sealed class MediaTypeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            MediaType.Video => "VIDEO",
            MediaType.Image => "IMAGE",
            _ => string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
