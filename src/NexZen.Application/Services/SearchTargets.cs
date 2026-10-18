using System;

namespace NexZen.Application.Services;

public static class SearchTargets
{
    public static string DisplayName(string? engine) => engine switch
    {
        "Bing" => "Bing",
        "DuckDuckGo" => "DuckDuckGo",
        "CocCoc" => "Cốc Cốc",
        "Google" or null or "" => "Google",
        _ => engine
    };

    public static string TextUrl(string? engine, string query)
    {
        var encoded = Uri.EscapeDataString(query);
        return engine switch
        {
            "Bing" => $"https://www.bing.com/search?q={encoded}",
            "DuckDuckGo" => $"https://duckduckgo.com/?q={encoded}",
            "CocCoc" => $"https://coccoc.com/search?query={encoded}",
            _ => $"https://www.google.com/search?q={encoded}"
        };
    }

    public static string ResearchUrl(string? engine, string query, string? language = null)
    {
        var encoded = Uri.EscapeDataString(query);
        var loc = Locale(language);
        return engine switch
        {
            "Bing" => $"https://www.bing.com/search?q={encoded}&setlang={loc.BingLang}&cc={loc.Region}",
            "DuckDuckGo" => $"https://duckduckgo.com/?q={encoded}&kl={loc.Ddg}",
            "CocCoc" => $"https://coccoc.com/search?query={encoded}",
            _ => $"https://www.google.com/search?q={encoded}&hl={loc.Hl}&gl={loc.Gl}&pws=0"
        };
    }

    public static bool IsVietnamese(string? language)
    {
        var text = (language ?? "").Trim();
        if (text.Length == 0)
            return false;
        return text.StartsWith("vi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("việt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("viet", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Hl, string Gl, string BingLang, string Region, string Ddg) Locale(string? language)
    {
        var text = (language ?? "").Trim();
        if (IsVietnamese(text))
            return ("vi", "VN", "vi", "VN", "vn-vi");
        if (Starts(text, "ja") || Contains(text, "japan"))
            return ("ja", "JP", "ja", "JP", "jp-jp");
        if (Starts(text, "ko") || Contains(text, "korea"))
            return ("ko", "KR", "ko", "KR", "kr-kr");
        if (Starts(text, "zh") || Contains(text, "chinese") || Contains(text, "中"))
            return ("zh-CN", "CN", "zh-Hans", "CN", "cn-zh");
        if (Starts(text, "fr") || Contains(text, "french"))
            return ("fr", "FR", "fr", "FR", "fr-fr");
        if (Starts(text, "de") || Contains(text, "german"))
            return ("de", "DE", "de", "DE", "de-de");
        if (Starts(text, "es") || Contains(text, "spanish"))
            return ("es", "ES", "es", "ES", "es-es");
        return ("en", "us", "en", "US", "us-en");
    }

    private static bool Starts(string text, string prefix)
        => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    // Same rule as the SuperMenu address bar: a known scheme or a host-like
    // token is opened, anything else (including text with spaces) is a search.
    public static string FromAddressBar(string? engine, string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
            return "";

        if (!text.Contains(' '))
        {
            if (HasAddressScheme(text))
                return text;
            if (text.Contains('.') || text.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return "https://" + text;
        }

        return TextUrl(engine, text);
    }

    private static bool HasAddressScheme(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("view-source://", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("file://", StringComparison.OrdinalIgnoreCase);

    // DuckDuckGo and Cốc Cốc have no public reverse-image URL. Searching the
    // image address as keywords is a text search, so those engines return null.
    public static string? ImageUrl(string? engine, string publicImageUrl)
    {
        var encoded = Uri.EscapeDataString(publicImageUrl);
        return engine switch
        {
            "Bing" => $"https://www.bing.com/images/searchbyimage?cbir=sbi&imgurl={encoded}",
            "Google" or null or "" => $"https://lens.google.com/uploadbyurl?url={encoded}",
            _ => null
        };
    }

    public static string? PublicImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            trimmed = "https:" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri.AbsoluteUri;
    }
}
