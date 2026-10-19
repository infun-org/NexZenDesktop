using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NexZen.Desktop.Converters
{
    /// <summary>"#RRGGBB" to a brush; ConverterParameter is the brush used when there is no color.</summary>
    public class HexToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string hex && Color.TryParse(hex, out var color))
                return new SolidColorBrush(color);
            return parameter is string fallback && Color.TryParse(fallback, out var other)
                ? new SolidColorBrush(other)
                : null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Border width for a workspace color: ConverterParameter "colored,plain", e.g. "3,1".</summary>
    public class HexToThicknessConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var widths = (parameter as string ?? "3,1").Split(',');
            var colored = value is string hex && Color.TryParse(hex, out _);
            var text = colored ? widths[0] : widths.Length > 1 ? widths[1] : "1";
            return new Thickness(double.Parse(text, CultureInfo.InvariantCulture));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
