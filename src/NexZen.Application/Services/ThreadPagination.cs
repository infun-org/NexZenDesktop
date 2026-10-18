using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexZen.Application.Services;

public sealed record ThreadPageLink(int Number, string Url);

public sealed class ThreadPageMeta
{
    public int CurrentPage { get; init; } = 1;
    public int LastPage { get; init; } = 1;
    public string? NextUrl { get; init; }
    public IReadOnlyList<ThreadPageLink> Pages { get; init; } = Array.Empty<ThreadPageLink>();
}

public static class ThreadPagination
{
    public const int MaxPages = 15;

    private static readonly Regex PageDash = new(
        @"/page-(\d+)(?=/|$|\?|#)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PageSlash = new(
        @"/page/(\d+)(?=/|$|\?|#)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PageQuery = new(
        @"[?&]page=(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static int PageNumberFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return 0;
        var match = PageDash.Match(url);
        if (match.Success)
            return int.Parse(match.Groups[1].Value);
        match = PageSlash.Match(url);
        if (match.Success)
            return int.Parse(match.Groups[1].Value);
        match = PageQuery.Match(url);
        if (match.Success)
            return int.Parse(match.Groups[1].Value);
        return 0;
    }

    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return (url ?? "").Trim().TrimEnd('/');
        var builder = new UriBuilder(uri) { Fragment = "" };
        builder.Path = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(builder.Path))
            builder.Path = "/";
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static string ThreadKey(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return Normalize(url).ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        path = PageDash.Replace(path, "");
        path = PageSlash.Replace(path, "");
        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("page=", StringComparison.OrdinalIgnoreCase))
                    continue;
                queryParts.Add(part);
            }
        }

        var query = queryParts.Count == 0 ? "" : "?" + string.Join('&', queryParts);
        return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{path.ToLowerInvariant()}{query}";
    }

    public static bool IsSafePageUrl(string currentUrl, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var dest))
            return false;
        if (dest.Scheme != Uri.UriSchemeHttp && dest.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var src))
            return false;
        if (!string.Equals(src.Host, dest.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(ThreadKey(currentUrl), ThreadKey(candidate), StringComparison.OrdinalIgnoreCase);
    }

    public static ThreadPageMeta ReadMeta(string? json, string fallbackUrl)
    {
        var fromUrl = PageNumberFromUrl(fallbackUrl);
        var currentFromUrl = fromUrl > 0 ? fromUrl : 1;
        if (string.IsNullOrWhiteSpace(json))
            return new ThreadPageMeta { CurrentPage = currentFromUrl, LastPage = currentFromUrl };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object)
                return new ThreadPageMeta { CurrentPage = currentFromUrl, LastPage = currentFromUrl };

            var current = GetInt(thread, "current", currentFromUrl);
            if (current < 1)
                current = currentFromUrl;
            var last = GetInt(thread, "last", current);
            string? next = null;
            if (thread.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.String)
            {
                var value = nextEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    next = value;
            }

            var pages = new List<ThreadPageLink>();
            if (thread.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pagesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var n = GetInt(item, "n", 0);
                    var url = item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                        ? urlEl.GetString()
                        : null;
                    if (n >= 1 && !string.IsNullOrWhiteSpace(url))
                        pages.Add(new ThreadPageLink(n, url));
                }
            }

            last = Math.Max(last, current);
            foreach (var page in pages)
                last = Math.Max(last, page.Number);
            if (!string.IsNullOrWhiteSpace(next) && last <= current)
            {
                var nextN = PageNumberFromUrl(next);
                last = nextN > last ? nextN : current + 1;
            }

            return new ThreadPageMeta
            {
                CurrentPage = current,
                LastPage = last,
                NextUrl = next,
                Pages = pages
            };
        }
        catch (JsonException)
        {
            return new ThreadPageMeta { CurrentPage = currentFromUrl, LastPage = currentFromUrl };
        }
    }

    public static IReadOnlyList<ThreadPageLink> Expand(string currentUrl, ThreadPageMeta meta)
    {
        var currentPage = meta.CurrentPage > 0 ? meta.CurrentPage : 1;
        var urlPage = PageNumberFromUrl(currentUrl);
        if (urlPage > 0)
            currentPage = urlPage;

        var last = Math.Max(1, meta.LastPage);
        last = Math.Max(last, currentPage);
        foreach (var page in meta.Pages)
            last = Math.Max(last, page.Number);

        var known = new Dictionary<int, string>();
        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out _))
            known[currentPage] = currentUrl;
        foreach (var page in meta.Pages)
        {
            if (page.Number < 1 || string.IsNullOrWhiteSpace(page.Url))
                continue;
            if (!IsSafePageUrl(currentUrl, page.Url))
                continue;
            known[page.Number] = page.Url;
        }

        var wanted = new List<int>();
        var cap = Math.Min(last, MaxPages);
        for (var n = 1; n <= cap; n++)
            wanted.Add(n);
        if (last > cap)
            wanted.Add(last);

        foreach (var n in wanted)
        {
            if (known.ContainsKey(n))
                continue;
            var made = Synthesize(currentUrl, currentPage, n, known);
            if (!string.IsNullOrWhiteSpace(made) && IsSafePageUrl(currentUrl, made))
                known[n] = made;
        }

        return wanted
            .Where(known.ContainsKey)
            .Select(n => new ThreadPageLink(n, known[n]))
            .ToList();
    }

    public static string? SwapPage(string url, int fromPage, int toPage)
    {
        if (string.IsNullOrWhiteSpace(url) || fromPage == toPage)
            return url;

        if (PageDash.IsMatch(url))
        {
            if (toPage <= 1)
                return TrimPathSlash(PageDash.Replace(url, ""));
            return PageDash.Replace(url, $"/page-{toPage}");
        }

        if (PageSlash.IsMatch(url))
        {
            if (toPage <= 1)
                return TrimPathSlash(PageSlash.Replace(url, ""));
            return PageSlash.Replace(url, $"/page/{toPage}");
        }

        if (PageQuery.IsMatch(url))
        {
            if (toPage <= 1)
                return RemoveQueryParam(url, "page");
            return Regex.Replace(url, @"([?&]page=)\d+", $"${{1}}{toPage}", RegexOptions.IgnoreCase);
        }

        if (fromPage <= 1 && toPage > 1 && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri) { Fragment = "" };
            builder.Path = uri.AbsolutePath.TrimEnd('/') + $"/page-{toPage}";
            return builder.Uri.ToString();
        }

        if (toPage <= 1)
            return TrimPathSlash(PageSlash.Replace(PageDash.Replace(url, ""), ""));

        return null;
    }

    private static string? Synthesize(string currentUrl, int currentPage, int target, Dictionary<int, string> known)
    {
        foreach (var pair in known.OrderBy(p => p.Key))
        {
            var templatePage = PageNumberFromUrl(pair.Value);
            if (templatePage <= 0)
                templatePage = pair.Key;
            var swapped = SwapPage(pair.Value, templatePage, target);
            if (!string.IsNullOrWhiteSpace(swapped))
                return swapped;
        }

        return SwapPage(currentUrl, currentPage, target);
    }

    private static int GetInt(JsonElement el, string name, int fallback)
    {
        if (!el.TryGetProperty(name, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
            return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n))
            return n;
        return fallback;
    }

    private static string TrimPathSlash(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.TrimEnd('/');
        var builder = new UriBuilder(uri);
        builder.Path = builder.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(builder.Path))
            builder.Path = "/";
        return builder.Uri.ToString();
    }

    private static string RemoveQueryParam(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var parts = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var builder = new UriBuilder(uri) { Query = string.Join('&', parts) };
        return builder.Uri.ToString();
    }
}
