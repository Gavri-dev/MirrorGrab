using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MirrorGrab.Converters;

public class StringToSolidBrushConverter : IValueConverter
{
    public static readonly StringToSolidBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
            return SolidColorBrush.Parse(hex);
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public class BoolToOppositeConverter : IValueConverter
{
    public static readonly BoolToOppositeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
