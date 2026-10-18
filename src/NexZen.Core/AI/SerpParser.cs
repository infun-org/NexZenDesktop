using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexZen.Core.AI;

public static class SerpParser
{
    public const int MaxSerpHits = 5;
    public const int MaxPages = 2;
    public const int MaxPageAttempts = 5;

    private static readonly Regex GoogleH3 = new(
        @"<a\b[^>]*\bhref\s*=\s*""(?<href>[^""]+)""[^>]*>\s*<h3\b[^>]*>(?<title>.*?)</h3>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BingAlgo = new(
        @"<li\b[^>]*\bclass\s*=\s*""[^""]*b_algo[^""]*""[^>]*>.*?<h2\b[^>]*>\s*<a\b[^>]*\bhref\s*=\s*""(?<href>[^""]+)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex GenericHeadingLink = new(
        @"<a\b[^>]*\bhref\s*=\s*""(?<href>[^""]+)""[^>]*>\s*<h[23]\b[^>]*>(?<title>.*?)</h[23]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "mình", "muốn", "tìm", "hiểu", "cho", "hỏi", "về", "của", "và", "the", "a", "an",
        "is", "are", "what", "who", "how", "why", "when", "where", "please", "tell", "me",
        "about", "thông", "tin", "giúp", "với", "này", "đó", "không", "được", "hay", "hoặc",
        "làm", "sao", "như", "thế", "nào", "for", "from", "with", "this", "that"
    };

    private static readonly string[] SearchHosts =
    [
        "google.com", "google.com.vn", "bing.com", "duckduckgo.com", "coccoc.com",
        "googleusercontent.com", "gstatic.com"
    ];

    private static readonly string[] SkipHosts =
    [
        "youtube.com", "youtu.be", "facebook.com", "instagram.com", "tiktok.com",
        "twitter.com", "x.com", "accounts.google.com", "consent.google.com"
    ];

    private static readonly string[] ManufacturerHosts =
    [
        "apple.com", "samsung.com", "google.com", "microsoft.com", "sony.com",
        "huawei.com", "xiaomi.com", "oneplus.com", "motorola.com", "nokia.com",
        "nvidia.com", "amd.com", "intel.com", "lenovo.com", "asus.com", "dell.com",
        "hp.com", "lg.com"
    ];

    private static readonly string[] PreferredHosts =
    [
        "macrumors.com", "9to5mac.com", "9to5google.com", "theverge.com",
        "gsmarena.com", "phonearena.com", "cnet.com", "tomsguide.com",
        "androidauthority.com", "androidcentral.com", "engadget.com",
        "arstechnica.com", "wired.com", "techcrunch.com", "pcmag.com",
        "digitaltrends.com", "zdnet.com", "windowscentral.com",
        "bbc.com", "bbc.co.uk", "reuters.com", "bloomberg.com",
        "nytimes.com", "theguardian.com", "wikipedia.org"
    ];

    private static readonly Regex RumorWords = new(
        @"\b(rumou?rs?|leaks?|leaked|allegedly|reportedly|unconfirmed|dự báo|tin đồn|đồn đoán|rò rỉ)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string From, string To)[] IntentPhrases =
    [
        ("bao nhiêu", "how many"),
        ("màu sắc", "colors"),
        ("ra mắt", "release"),
        ("phát hành", "release"),
        ("cấu hình", "specs"),
        ("thông số", "specs"),
        ("màn hình", "display"),
        ("dung lượng", "storage"),
        ("bộ nhớ", "storage"),
        ("so sánh", "vs"),
        ("khi nào", "when"),
        ("đánh giá", "review"),
        ("giá bán", "price"),
    ];

    private static readonly Dictionary<string, string> IntentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["màu"] = "colors",
        ["giá"] = "price",
        ["pin"] = "battery",
        ["camera"] = "camera",
        ["ngày"] = "date",
        ["review"] = "review",
    };

    private static readonly HashSet<string> DropWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "có", "của", "và", "cho", "hỏi", "về", "với", "này", "đó", "không", "được",
        "hay", "hoặc", "làm", "sao", "như", "thế", "nào", "là", "thì", "ở", "tại",
        "một", "các", "những", "bị", "trong", "ngoài", "mình", "muốn", "tìm", "hiểu",
        "thông", "tin", "giúp", "đi", "ạ", "nhỉ", "ko", "dc", "đc", "hôm", "nay"
    };

    public static IReadOnlyList<WebLookupHit> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<WebLookupHit>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<WebLookupHit>();
            var hits = new List<WebLookupHit>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var title = Read(item, "title");
                var url = FromTranslateProxy(Unwrap(Read(item, "url")));
                var snippet = Read(item, "snippet");
                if (title.Length == 0 || url.Length == 0)
                    continue;
                if (ShouldSkipUrl(url))
                    continue;
                var shown = IsRedirect(url) ? DisplayHostFrom(Read(item, "display")) : null;
                if (shown != null && ShouldSkipUrl("https://" + shown + "/"))
                    continue;
                hits.Add(new WebLookupHit(title, url, snippet, "Search", shown));
                if (hits.Count >= MaxSerpHits)
                    break;
            }

            return hits;
        }
        catch (JsonException)
        {
            return Array.Empty<WebLookupHit>();
        }
    }

    public static IReadOnlyList<WebLookupHit> ParseHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || WebLookupParser.LooksLikeCaptcha(html))
            return Array.Empty<WebLookupHit>();

        var hits = new List<WebLookupHit>();
        Collect(hits, BingAlgo.Matches(html));
        if (hits.Count < 3)
            Collect(hits, GoogleH3.Matches(html));
        if (hits.Count < 3)
        {
            foreach (var hit in WebLookupParser.ParseDuckDuckGoHtml(html))
            {
                if (ShouldSkipUrl(hit.Url))
                    continue;
                hits.Add(hit with { Source = "Search" });
                if (hits.Count >= MaxSerpHits)
                    break;
            }
        }

        if (hits.Count < 3)
            Collect(hits, GenericHeadingLink.Matches(html));
        return Dedup(hits);
    }

    public static IReadOnlyList<WebLookupHit> RankHits(IEnumerable<WebLookupHit>? hits)
    {
        if (hits == null)
            return Array.Empty<WebLookupHit>();
        return hits
            .Select((hit, index) => (hit, index, score: HitScore(hit)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.hit)
            .ToList();
    }

    public static IReadOnlyList<WebLookupHit> PickPages(IEnumerable<WebLookupHit> serp, int max = MaxPages)
    {
        var result = new List<WebLookupHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in RankHits(serp))
        {
            // Read the original rather than Google's machine-translated copy of it.
            var url = FromTranslateProxy(Unwrap(hit.Url));
            // Redirects are opened in the browser, which lands on the real page.
            var redirect = IsRedirect(url);
            if (url.Length == 0 || ShouldSkipUrl(url) || (IsSearchHost(url) && !redirect))
                continue;
            // Every redirect shares the path /goto; only its token tells them apart.
            if (!seen.Add(redirect ? url : NormalizeKey(url)))
                continue;
            result.Add(hit with { Url = url, Source = "Search" });
            if (result.Count >= max)
                break;
        }

        return result;
    }

    public static string RewriteQuery(string? query)
    {
        var raw = (query ?? "").Trim();
        if (raw.Length == 0 || !WebLookupParser.LooksVietnamese(raw))
            return raw;

        var text = raw.ToLowerInvariant();
        foreach (var (from, to) in IntentPhrases.OrderByDescending(p => p.From.Length))
            text = text.Replace(from, to, StringComparison.Ordinal);

        var parts = new List<string>();
        var latin = 0;
        var mapped = 0;
        var token = new char[64];
        var n = 0;
        void Flush()
        {
            if (n == 0)
                return;
            var word = new string(token, 0, n);
            n = 0;
            if (IntentWords.TryGetValue(word, out var english))
            {
                mapped++;
                if (parts.Count == 0 || !parts[^1].Equals(english, StringComparison.OrdinalIgnoreCase))
                    parts.Add(english);
                return;
            }

            if (DropWords.Contains(word) || WebLookupParser.LooksVietnamese(word))
                return;
            if (parts.Count > 0 && parts[^1].Equals(word, StringComparison.OrdinalIgnoreCase))
                return;
            parts.Add(word);
            var letters = 0;
            var digits = 0;
            var ascii = true;
            foreach (var c in word)
            {
                if (c > 127)
                    ascii = false;
                if (char.IsLetter(c))
                    letters++;
                else if (char.IsDigit(c))
                    digits++;
            }

            if (ascii && (letters >= 3 || digits >= 2))
                latin++;
        }

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (n < token.Length)
                    token[n++] = c;
                continue;
            }

            Flush();
        }

        Flush();
        if (parts.Count == 0)
            return raw;
        foreach (var part in parts)
        {
            foreach (var c in part)
            {
                if (c > 127)
                    return raw;
            }
        }

        if (latin >= 2 || (latin >= 1 && mapped >= 1 && parts.Count >= 2))
            return string.Join(' ', parts);
        return raw;
    }

    public static bool LooksLikeRumor(string? title, string? snippet = null)
    {
        var hay = ((title ?? "") + " " + (snippet ?? "")).Trim();
        return hay.Length > 0 && RumorWords.IsMatch(hay);
    }

    public static int HitScore(WebLookupHit hit)
    {
        // A redirect's own host is the search engine (google.com would count as a manufacturer).
        var score = IsRedirect(hit.Url)
            ? (string.IsNullOrEmpty(hit.DisplayHost) ? 0 : HostScore("https://" + hit.DisplayHost + "/"))
            : HostScore(hit.Url);
        if (LooksLikeRumor(hit.Title, hit.Snippet) && score < 80)
            score -= 50;
        return score;
    }

    public static int HostScore(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return 0;
        var host = StripWww(uri.Host);
        if (HostMatches(host, ManufacturerHosts))
            return 100;
        if (HostMatches(host, PreferredHosts))
            return 60;
        return 0;
    }

    public static bool PreferEnglishSearch(string? original, string? rewritten)
        => !string.Equals((original ?? "").Trim(), (rewritten ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Unwrap(string? href)
    {
        var raw = WebLookupParser.UnwrapUrl(href);
        if (raw.StartsWith("/url", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("/l?", StringComparison.OrdinalIgnoreCase))
            raw = "https://www.google.com" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return raw;
        if (uri.AbsolutePath.Equals("/url", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Equals("/l", StringComparison.OrdinalIgnoreCase))
        {
            var target = QueryValue(uri.Query, "q")
                ?? QueryValue(uri.Query, "url")
                ?? QueryValue(uri.Query, "uddg");
            var unwrapped = string.IsNullOrWhiteSpace(target) ? "" : WebLookupParser.UnwrapUrl(target);
            if (Uri.TryCreate(unwrapped, UriKind.Absolute, out var real)
                && (real.Scheme == Uri.UriSchemeHttp || real.Scheme == Uri.UriSchemeHttps))
                return unwrapped;
        }

        return uri.ToString();
    }

    /// <summary>
    /// Google result links that only resolve by following them: /goto?url=… and
    /// /url?url=CAES… carry an encrypted token instead of the target address.
    /// </summary>
    public static bool IsRedirect(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSearchHost(url))
            return false;
        var path = uri.AbsolutePath;
        if (path.Equals("/goto", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!path.Equals("/url", StringComparison.OrdinalIgnoreCase))
            return false;
        var target = QueryValue(uri.Query, "q") ?? QueryValue(uri.Query, "url") ?? "";
        return target.Length > 0 && !target.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host from the address a search page shows, e.g. "https://www.thegioididong.com › dtdd".</summary>
    public static string? DisplayHostFrom(string? shown)
    {
        var text = (shown ?? "").Trim();
        if (text.Length == 0)
            return null;
        var cut = text.IndexOfAny([' ', '›']);
        if (cut > 0)
            text = text[..cut];
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(FromTranslateProxy(text), UriKind.Absolute, out var uri) || !uri.Host.Contains('.'))
            return null;
        return StripWww(uri.Host).ToLowerInvariant();
    }

    /// <summary>Address to show for a hit: the real site instead of an opaque search redirect.</summary>
    public static string ShownUrl(WebLookupHit hit)
    {
        if (IsRedirect(hit.Url))
            return string.IsNullOrEmpty(hit.DisplayHost) ? "" : "https://" + hit.DisplayHost + "/";
        return hit.Url ?? "";
    }

    /// <summary>Short site name for status lines.</summary>
    public static string HostLabel(WebLookupHit hit)
        => IsRedirect(hit.Url) && !string.IsNullOrEmpty(hit.DisplayHost) ? hit.DisplayHost : HostLabel(hit.Url);

    public static bool ShouldSkipUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return true;
        var host = StripWww(uri.Host);
        foreach (var skip in SkipHosts)
        {
            if (host.Equals(skip, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + skip, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (IsSearchHost(url) && !uri.AbsolutePath.Equals("/", StringComparison.Ordinal)
            && uri.AbsolutePath.IndexOf("/search", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (uri.AbsolutePath.Contains("webcache", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("/imgres", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool IsSearchHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = StripWww(uri.Host);
        foreach (var search in SearchHosts)
        {
            if (host.Equals(search, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + search, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool QueryLooksRelated(string? query, string? title, string? url)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0)
            return false;
        var hay = ((title ?? "") + " " + (url ?? "")).ToLowerInvariant();
        if (hay.Trim().Length == 0)
            return false;
        var hits = 0;
        foreach (var token in tokens)
        {
            if (!hay.Contains(token, StringComparison.Ordinal))
                continue;
            hits++;
            if (token.Length >= 5)
                return true;
        }

        return hits >= Math.Min(2, tokens.Count);
    }

    public static IReadOnlyList<string> Tokens(string? query)
    {
        var raw = (query ?? "").ToLowerInvariant();
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new char[64];
        var n = 0;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (n < current.Length)
                    current[n++] = c;
                continue;
            }

            FlushToken(list, seen, current, n);
            n = 0;
        }

        FlushToken(list, seen, current, n);
        return list;
    }

    public static string HostLabel(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "page";
        return StripWww(uri.Host);
    }

    public static bool LooksLikeConsent(string? url, string? title)
    {
        var u = url ?? "";
        var t = title ?? "";
        return u.Contains("consent.", StringComparison.OrdinalIgnoreCase)
            || u.Contains("sorry/index", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Before you continue", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Trước khi tiếp tục", StringComparison.OrdinalIgnoreCase)
            || t.Contains("cookie", StringComparison.OrdinalIgnoreCase);
    }

    private static void Collect(List<WebLookupHit> hits, MatchCollection matches)
    {
        foreach (Match match in matches)
        {
            if (hits.Count >= MaxSerpHits)
                return;
            var title = WebLookupParser.StripHtml(match.Groups["title"].Value);
            var url = Unwrap(match.Groups["href"].Value);
            if (title.Length < 3 || url.Length == 0 || ShouldSkipUrl(url) || IsSearchHost(url))
                continue;
            if (hits.Any(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase)))
                continue;
            hits.Add(new WebLookupHit(title, url, "", "Search"));
        }
    }

    private static IReadOnlyList<WebLookupHit> Dedup(List<WebLookupHit> hits)
        => WebLookupParser.Combine(hits);

    private static string Read(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return "";
        return WebLookupParser.StripHtml(value.GetString() ?? "");
    }

    private static string? QueryValue(string query, string key)
    {
        var raw = query.StartsWith('?') ? query[1..] : query;
        foreach (var part in raw.Split('&'))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = eq < 0 ? "" : part[(eq + 1)..];
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return null;
    }

    private static bool HostMatches(string host, string[] list)
    {
        foreach (var item in list)
        {
            if (host.Equals(item, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + item, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string StripWww(string host)
        => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string NormalizeKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim().TrimEnd('/');
        return StripWww(uri.Host).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
    }

    /// <summary>Same key for the same page (www, trailing slash, query and Google Translate copies ignored).</summary>
    public static string PageKey(string? url) => NormalizeKey(FromTranslateProxy(url));

    private const string TranslateProxySuffix = ".translate.goog";

    /// <summary>
    /// Google's "translate this page" links serve the original through a proxy host:
    /// www-cloudzero-com.translate.goog/blog?_x_tr_sl=en → www.cloudzero.com/blog.
    /// Returns the original address; other URLs come back unchanged.
    /// </summary>
    public static string FromTranslateProxy(string? url)
    {
        var raw = (url ?? "").Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith(TranslateProxySuffix, StringComparison.OrdinalIgnoreCase))
            return raw;
        var label = uri.Host[..^TranslateProxySuffix.Length];
        if (label.Length == 0)
            return raw;
        // "--" stands for a real hyphen, "-" for a dot.
        var host = label.Replace("--", "\0").Replace('-', '.').Replace('\0', '-');
        var query = string.Join('&', uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("_x_tr_", StringComparison.OrdinalIgnoreCase)));
        return "https://" + host + uri.AbsolutePath + (query.Length > 0 ? "?" + query : "") + uri.Fragment;
    }

    private static void FlushToken(List<string> list, HashSet<string> seen, char[] buffer, int length)
    {
        if (length < 3)
            return;
        var token = new string(buffer, 0, length);
        if (Stopwords.Contains(token) || !seen.Add(token))
            return;
        list.Add(token);
    }
}
