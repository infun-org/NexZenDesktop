using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace NexZen.Desktop.Converters
{
    /// <summary>SVG path data (an icon kept as a string in a view model) to a geometry for PathIcon.</summary>
    public class PathDataToGeometryConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string data || data.Length == 0)
                return null;
            try
            {
                return StreamGeometry.Parse(data);
            }
            catch
            {
                return null;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
