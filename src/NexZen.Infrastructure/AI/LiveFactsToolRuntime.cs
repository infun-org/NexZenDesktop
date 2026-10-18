using System.Globalization;
using System.Text.Json;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class LiveFactsToolRuntime : IAiToolRuntime, IDisposable
{
    public const string GetNow = "get_now";
    public const string GetWeather = "get_weather";
    // Local models get this on every request, page actions included, so it also carries the answer style.
    public const string SystemPrompt =
        "Live facts are provided below. Use the authoritative local clock for today, now, the current date, or the current time. Use live weather when present. Never invent a date, time, or weather from training memory.\n\n"
        + AiAnswerStyle.Rules;

    private static readonly HttpClient Http = CreateClient();

    public IReadOnlyList<AiToolDefinition> Definitions { get; } =
    [
        new(
            GetNow,
            "Get the real current local date, time, weekday, and time zone from this computer. Use for today, now, current date, or current time.",
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["additionalProperties"] = false
            }),
        new(
            GetWeather,
            "Get live weather for a city or place. Use for current weather, temperature, rain, or a short forecast.",
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["location"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = "City or place name, for example Ho Chi Minh City, Hanoi, Da Nang"
                    }
                },
                ["required"] = new[] { "location" }
            })
    ];

    public async Task<string> InvokeAsync(
        string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        if (string.Equals(name, GetNow, StringComparison.OrdinalIgnoreCase))
            return FormatNow(DateTimeOffset.Now);
        if (string.Equals(name, GetWeather, StringComparison.OrdinalIgnoreCase))
            return await GetWeatherAsync(argumentsJson, cancellationToken);
        return $"Unknown tool: {name}";
    }

    public static string FormatNow(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Local;
        var weekdayEn = now.ToString("dddd", CultureInfo.InvariantCulture);
        CultureInfo vi;
        try { vi = CultureInfo.GetCultureInfo("vi-VN"); }
        catch (CultureNotFoundException) { vi = CultureInfo.InvariantCulture; }
        var weekdayVi = now.ToString("dddd", vi);
        return
            $"current_datetime: {now:yyyy-MM-ddTHH:mm:sszzz}\n" +
            $"date: {now:yyyy-MM-dd}\n" +
            $"time: {now:HH:mm:ss}\n" +
            $"weekday: {weekdayEn} ({weekdayVi})\n" +
            $"timezone: {zone.Id} (UTC{now:zzz})";
    }

    public static string FormatForecast(string location, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            return $"Weather lookup for {location} returned no current conditions.";
        var temp = ReadNumber(current, "temperature_2m");
        var feel = ReadNumber(current, "apparent_temperature");
        var humidity = ReadNumber(current, "relative_humidity_2m");
        var wind = ReadNumber(current, "wind_speed_10m");
        var code = current.TryGetProperty("weather_code", out var codeEl) && codeEl.TryGetInt32(out var n) ? n : -1;
        var time = current.TryGetProperty("time", out var timeEl) && timeEl.ValueKind == JsonValueKind.String
            ? timeEl.GetString()
            : "";
        return
            $"location: {location}\n" +
            $"observed: {time}\n" +
            $"conditions: {DescribeWeather(code)}\n" +
            $"temperature_c: {temp}\n" +
            $"feels_like_c: {feel}\n" +
            $"humidity_percent: {humidity}\n" +
            $"wind_kmh: {wind}";
    }

    public static string DescribeWeather(int code) => code switch
    {
        0 => "clear",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 or 63 or 65 => "rain",
        66 or 67 => "freezing rain",
        71 or 73 or 75 or 77 => "snow",
        80 or 81 or 82 => "rain showers",
        85 or 86 => "snow showers",
        95 => "thunderstorm",
        96 or 99 => "thunderstorm with hail",
        _ => "unknown"
    };

    public void Dispose()
    {
        // shared static client
    }

    private static async Task<string> GetWeatherAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var location = ReadLocation(argumentsJson);
        if (string.IsNullOrWhiteSpace(location))
            return "get_weather needs a location (city or place name).";

        var geoUrl =
            "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name="
            + Uri.EscapeDataString(location);
        using var geoResponse = await Http.GetAsync(geoUrl, cancellationToken);
        geoResponse.EnsureSuccessStatusCode();
        var geoJson = await geoResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!TryReadGeo(geoJson, out var label, out var lat, out var lon))
            return $"No weather location matched \"{location}\".";

        var forecastUrl =
            $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m,apparent_temperature" +
            "&timezone=auto";
        using var forecastResponse = await Http.GetAsync(forecastUrl, cancellationToken);
        forecastResponse.EnsureSuccessStatusCode();
        var forecastJson = await forecastResponse.Content.ReadAsStringAsync(cancellationToken);
        return FormatForecast(label, forecastJson);
    }

    public static string ReadLocation(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("location", out var location)
                && location.ValueKind == JsonValueKind.String)
                return (location.GetString() ?? "").Trim();
        }
        catch (JsonException)
        {
            return argumentsJson.Trim().Trim('"');
        }

        return "";
    }

    public static bool TryReadGeo(string json, out string label, out double lat, out double lon)
    {
        label = "";
        lat = 0;
        lon = 0;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
            return false;
        var first = results[0];
        var name = first.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
        var country = first.TryGetProperty("country", out var countryEl) ? countryEl.GetString() ?? "" : "";
        var admin = first.TryGetProperty("admin1", out var adminEl) ? adminEl.GetString() ?? "" : "";
        label = string.Join(", ", new[] { name, admin, country }.Where(s => s.Length > 0));
        if (!first.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out lat))
            return false;
        if (!first.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out lon))
            return false;
        return label.Length > 0;
    }

    private static string ReadNumber(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.String => el.GetString() ?? "",
            _ => ""
        };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NexZenDesktop");
        return client;
    }
}
