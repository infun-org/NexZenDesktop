using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using NexZen.Core.Interfaces;

namespace NexZen.Infrastructure.Services;

/// <summary>
/// Loads a site icon the way a browser would: the page's &lt;link rel="icon"&gt; tags first,
/// then /favicon.ico. SVG icons are skipped (the app shows bitmaps only).
/// </summary>
public sealed class FaviconFetcher : IFaviconFetcher
{
    private const int MaxPageBytes = 512 * 1024;
    private const int MaxIconBytes = 256 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static readonly Regex LinkTag = new(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Attribute = new(
        @"\b(?<name>rel|href|type)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string?> FetchDataUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
            return null;

        var candidates = new List<Uri>();
        try
        {
            using var response = await Http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri ?? page;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
            {
                var html = await ReadLimitedStringAsync(response.Content, MaxPageBytes, cancellationToken);
                candidates.AddRange(FindIconLinks(html, finalUri));
            }
            candidates.Add(new Uri(finalUri, "/favicon.ico"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The page itself failed; the conventional location may still answer.
        }
        candidates.Add(new Uri(page, "/favicon.ico"));

        foreach (var icon in candidates.DistinctBy(u => u.AbsoluteUri).Take(5))
        {
            var dataUrl = await TryDownloadAsync(icon, cancellationToken);
            if (dataUrl != null)
                return dataUrl;
        }
        return null;
    }

    /// <summary>Icon links in page order, apple-touch-icon (larger, PNG) before plain icons; no SVG or mask icons.</summary>
    public static IReadOnlyList<Uri> FindIconLinks(string? html, Uri baseUri)
    {
        var found = new List<(int Rank, int Order, Uri Uri)>();
        if (string.IsNullOrEmpty(html))
            return Array.Empty<Uri>();
        var order = 0;
        foreach (Match tag in LinkTag.Matches(html))
        {
            string rel = "", href = "", type = "";
            foreach (Match attr in Attribute.Matches(tag.Value))
            {
                var value = System.Net.WebUtility.HtmlDecode(attr.Groups["v"].Value).Trim();
                switch (attr.Groups["name"].Value.ToLowerInvariant())
                {
                    case "rel": rel = value.ToLowerInvariant(); break;
                    case "href": href = value; break;
                    case "type": type = value.ToLowerInvariant(); break;
                }
            }
            if (!rel.Contains("icon") || rel.Contains("mask-icon") || href.Length == 0)
                continue;
            if (type.Contains("svg") || href.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(baseUri, href, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "data"))
                continue;
            found.Add((rel.Contains("apple-touch-icon") ? 0 : 1, order++, uri));
        }
        return found.OrderBy(f => f.Rank).ThenBy(f => f.Order).Select(f => f.Uri).ToList();
    }

    /// <summary>The image type from its first bytes, or null when it is not a bitmap the app can show.</summary>
    public static string? SniffImageMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
            return "image/x-icon";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 6 && Encoding.ASCII.GetString(bytes[..6]) is "GIF87a" or "GIF89a")
            return "image/gif";
        if (bytes.Length >= 12 && Encoding.ASCII.GetString(bytes[..4]) == "RIFF" && Encoding.ASCII.GetString(bytes[8..12]) == "WEBP")
            return "image/webp";
        if (bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            return "image/bmp";
        return null;
    }

    private static async Task<string?> TryDownloadAsync(Uri icon, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (icon.Scheme == "data")
            {
                var text = icon.OriginalString;
                var comma = text.IndexOf(',');
                if (comma < 0 || !text[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return null;
                bytes = Convert.FromBase64String(text[(comma + 1)..]);
            }
            else
            {
                using var response = await Http.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;
                bytes = await ReadLimitedBytesAsync(response.Content, MaxIconBytes, cancellationToken);
            }
            var mime = SniffImageMime(bytes);
            return mime == null || bytes.Length == 0 ? null : $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpContent content, int max, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max)
                return Array.Empty<byte>(); // too big for a favicon
        }
        return buffer.ToArray();
    }

    private static async Task<string> ReadLimitedStringAsync(HttpContent content, int max, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[max];
        var total = 0;
        int read;
        // Icon links live in <head>; the start of the page is enough.
        while (total < max && (read = await stream.ReadAsync(buffer.AsMemory(total, max - total), cancellationToken)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.8));
        return client;
    }
}
