using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amberglass.UI.Converters;

/// <summary>Red for a failed operation, green for a successful one.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly IBrush Failure = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
    private static readonly IBrush Success = new SolidColorBrush(Color.FromRgb(0x6B, 0xD1, 0x8A));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Failure : Success;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
