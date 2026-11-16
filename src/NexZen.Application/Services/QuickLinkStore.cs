using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexZen.Application.Services;

/// <summary>A shortcut on the new-tab page (about:blank).</summary>
public sealed class QuickLink
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    /// <summary>The site's icon as a data: URL, kept so the page shows it at once (null until fetched).</summary>
    [JsonPropertyName("icon")] public string? Icon { get; set; }
}

/// <summary>
/// The new-tab shortcuts, kept in quicklinks.json in NexZen's data folder and shared by all windows.
/// </summary>
public sealed class QuickLinkStore
{
    public const int MaxLinks = 16;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private List<QuickLink> _links;

    public event EventHandler? Changed;

    public QuickLinkStore(string path)
    {
        _path = path;
        _links = Load(path);
    }

    public static QuickLinkStore ForUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop", "quicklinks.json"));

    public IReadOnlyList<QuickLink> All
    {
        get { lock (_gate) return _links.Select(Copy).ToList(); }
    }

    /// <summary>Adds a link (or renames it if the URL is already there). False when the list is full or the URL is not web.</summary>
    public bool Add(string url, string? title)
    {
        var normalized = Normalize(url);
        if (normalized == null) return false;
        lock (_gate)
        {
            var existing = _links.FirstOrDefault(l => SameUrl(l.Url, normalized));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(title)) existing.Title = title.Trim();
            }
            else
            {
                if (_links.Count >= MaxLinks) return false;
                _links.Add(new QuickLink { Url = normalized, Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(normalized) : title.Trim() });
            }
        }
        Save();
        return true;
    }

    public bool Update(string oldUrl, string url, string? title)
    {
        var normalized = Normalize(url);
        if (normalized == null) return false;
        lock (_gate)
        {
            var link = _links.FirstOrDefault(l => SameUrl(l.Url, oldUrl));
            if (link == null) return false;
            if (!SameUrl(link.Url, normalized)) link.Icon = null; // another site: fetch its icon again
            link.Url = normalized;
            link.Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(normalized) : title.Trim();
        }
        Save();
        return true;
    }

    public void Remove(string url)
    {
        lock (_gate) _links.RemoveAll(l => SameUrl(l.Url, url));
        Save();
    }

    public void SetIcon(string url, string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl) || dataUrl.Length > 256 * 1024) return;
        lock (_gate)
        {
            var link = _links.FirstOrDefault(l => SameUrl(l.Url, url));
            if (link == null || link.Icon == dataUrl) return;
            link.Icon = dataUrl;
        }
        Save();
    }

    /// <summary>"example.com" → "https://example.com/"; null for anything that is not http(s).</summary>
    public static string? Normalize(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host) || (!uri.Host.Contains('.') && uri.Host != "localhost")) return null;
        return uri.AbsoluteUri;
    }

    /// <summary>"https://www.youtube.com/feed" → "Youtube".</summary>
    public static string DefaultTitle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var name = host.Split('.')[0];
        return name.Length == 0 ? host : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static bool SameUrl(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static QuickLink Copy(QuickLink l) => new() { Url = l.Url, Title = l.Title, Icon = l.Icon };

    private static List<QuickLink> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<List<QuickLink>>(File.ReadAllText(path))?
                    .Where(l => Normalize(l.Url) != null).Take(MaxLinks).ToList() ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new List<QuickLink>();
    }

    private void Save()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_links, Json);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
