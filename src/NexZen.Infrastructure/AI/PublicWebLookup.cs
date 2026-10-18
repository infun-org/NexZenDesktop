using System.Net.Http.Headers;
using NexZen.Core.AI;

namespace NexZen.Infrastructure.AI;

public sealed class PublicWebLookup : IWebLookup
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<WebLookupHit?> ReadArticleAsync(
        string url, string? title = null, CancellationToken cancellationToken = default, string? query = null)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0 || SerpParser.ShouldSkipUrl(url) || SerpParser.IsSearchHost(url))
            return null;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        var html = await FetchTextAsync(url, cancellationToken);
        var body = string.IsNullOrWhiteSpace(query)
            ? WebLookupParser.HtmlToArticleText(html, WebLookupParser.MaxPageChars)
            : PageFocus.FocusText(
                WebLookupParser.HtmlToArticleText(html, PageFocus.MaxSourceChars), query, WebLookupParser.MaxPageChars);
        if (body.Length < 80)
            return null;
        var heading = string.IsNullOrWhiteSpace(title) ? SerpParser.HostLabel(url) : title.Trim();
        return new WebLookupHit(heading, url, body, "Page");
    }

    public async Task<WebLookupResult> SearchAsync(
        string query, CancellationToken cancellationToken = default, string? language = null)
    {
        var q = WebLookupParser.Clip((query ?? "").Trim(), 180);
        if (q.Length == 0)
            return new WebLookupResult("", "", Array.Empty<WebLookupHit>(), "Empty query");

        var vi = IsVietnameseLanguage(language) || WebLookupParser.LooksVietnamese(q);
        var instantTask = FetchTextAsync(InstantUrl(q), cancellationToken);
        var htmlTask = FetchTextAsync(DuckHtmlUrl(q), cancellationToken);
        var wikiEnTask = FetchTextAsync(WikipediaSearchUrl("en", q), cancellationToken);
        var wikiViTask = vi
            ? FetchTextAsync(WikipediaSearchUrl("vi", q), cancellationToken)
            : Task.FromResult("");
        var newsEnTask = FetchTextAsync(NewsUrl(q, vietnamese: false), cancellationToken);
        var newsViTask = vi
            ? FetchTextAsync(NewsUrl(q, vietnamese: true), cancellationToken)
            : Task.FromResult("");

        try
        {
            await Task.WhenAll(instantTask, htmlTask, wikiEnTask, wikiViTask, newsEnTask, newsViTask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Individual fetches return "" on failure.
        }

        var instantJson = Safe(instantTask);
        var html = Safe(htmlTask);
        var wikiEnJson = Safe(wikiEnTask);
        var wikiViJson = Safe(wikiViTask);
        var newsEnXml = Safe(newsEnTask);
        var newsViXml = Safe(newsViTask);

        var (instant, iaHits) = WebLookupParser.ParseInstantAnswer(instantJson);
        var htmlHits = WebLookupParser.ParseDuckDuckGoHtml(html);
        var wikiEn = WebLookupParser.ParseWikipediaSearch(wikiEnJson, "Wikipedia");
        var wikiVi = vi
            ? WebLookupParser.ParseWikipediaSearch(wikiViJson, "Wikipedia vi")
            : Array.Empty<WebLookupHit>();
        var newsEn = WebLookupParser.ParseNewsRss(newsEnXml);
        var newsVi = vi
            ? WebLookupParser.ParseNewsRss(newsViXml)
            : Array.Empty<WebLookupHit>();
        var news = WebLookupParser.Combine(newsEn, newsVi);

        var wikiExtracts = news.Count >= 3
            ? Array.Empty<WebLookupHit>()
            : await FetchWikipediaExtractsAsync(wikiEn, wikiVi, cancellationToken);
        var hits = WebLookupParser.Combine(news, htmlHits, iaHits, wikiExtracts, wikiEn, wikiVi);
        string? error = null;
        if (!string.IsNullOrWhiteSpace(instant) || hits.Count > 0)
            return new WebLookupResult(q, instant, hits);

        if (WebLookupParser.LooksLikeCaptcha(html) && instantJson.Length == 0 && wikiEnJson.Length == 0 && newsEnXml.Length == 0)
            error = "The public web lookup was blocked.";
        else if (instantJson.Length == 0 && wikiEnJson.Length == 0 && newsEnXml.Length == 0 && html.Length == 0)
            error = "The public web lookup failed.";
        else
            error = "No web results.";
        return new WebLookupResult(q, "", Array.Empty<WebLookupHit>(), error);
    }

    private static async Task<IReadOnlyList<WebLookupHit>> FetchWikipediaExtractsAsync(
        IReadOnlyList<WebLookupHit> en,
        IReadOnlyList<WebLookupHit> vi,
        CancellationToken cancellationToken)
    {
        var enJson = await FetchExtractsAsync("en", en, cancellationToken);
        var viJson = await FetchExtractsAsync("vi", vi, cancellationToken);
        var hits = new List<WebLookupHit>();
        hits.AddRange(WebLookupParser.ParseWikipediaExtracts(enJson, "Wikipedia"));
        hits.AddRange(WebLookupParser.ParseWikipediaExtracts(viJson, "Wikipedia vi"));
        return hits;
    }

    private static async Task<string> FetchExtractsAsync(
        string lang, IReadOnlyList<WebLookupHit> hits, CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
            return "";
        var titles = string.Join('|', hits.Take(4).Select(h => h.Title));
        if (titles.Length == 0)
            return "";
        var url =
            $"https://{lang}.wikipedia.org/w/api.php?action=query&prop=extracts|info&exintro=1&explaintext=1&inprop=url&redirects=1&format=json&utf8=1&titles="
            + Uri.EscapeDataString(titles);
        return await FetchTextAsync(url, cancellationToken);
    }

    private static async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation(
                "Accept", "application/json, application/rss+xml, text/html, */*");
            using var response = await Http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return "";
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Safe(Task<string> task)
    {
        try
        {
            return task.Status == TaskStatus.RanToCompletion ? task.Result ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool IsVietnameseLanguage(string? language)
    {
        var text = (language ?? "").Trim();
        if (text.Length == 0)
            return false;
        return text.StartsWith("vi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("việt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("viet", StringComparison.OrdinalIgnoreCase);
    }

    private static string InstantUrl(string query)
        => "https://api.duckduckgo.com/?format=json&no_html=1&skip_disambig=1&q=" + Uri.EscapeDataString(query);

    private static string DuckHtmlUrl(string query)
        => "https://duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);

    private static string WikipediaSearchUrl(string lang, string query)
        => $"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&srlimit=5&srprop=snippet&format=json&utf8=1&srsearch="
           + Uri.EscapeDataString(query);

    private static string NewsUrl(string query, bool vietnamese)
        => vietnamese
            ? "https://news.google.com/rss/search?hl=vi&gl=VN&ceid=VN:vi&q=" + Uri.EscapeDataString(query)
            : "https://news.google.com/rss/search?hl=en-US&gl=US&ceid=US:en&q=" + Uri.EscapeDataString(query);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NexZenDesktop/1.0 (AI Function web lookup; local desktop app)");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("vi", 0.8));
        return client;
    }
}
