using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace NexZen.Desktop.Converters
{
    /// <summary>
    /// Favicons saved with a workspace are data:image/...;base64 URLs. Decoded bitmaps are
    /// cached, since the same few site icons repeat across many cards.
    /// </summary>
    public class DataUrlToBitmapConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string url || !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return null;
            if (Cache.Count > 500)
                Cache.Clear();
            return Cache.GetOrAdd(url, Decode);
        }

        private static Bitmap? Decode(string url)
        {
            var comma = url.IndexOf(',');
            if (comma < 0 || !url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                return null;
            try
            {
                using var stream = new MemoryStream(System.Convert.FromBase64String(url[(comma + 1)..]));
                return new Bitmap(stream);
            }
            catch
            {
                return null;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
