using NexZen.Core.Entities;
using NexZen.Core.Interfaces;

namespace NexZen.Application.Services;

/// <summary>
/// The saved Custom CSS kept in memory, so every page load can be served without a database
/// round trip. Changes are written through to the repository.
/// </summary>
public sealed class CustomStyleStore
{
    private readonly Func<ICustomStyleRepository> _repository;
    private readonly Dictionary<string, CustomStyle> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CustomStyleStore(Func<ICustomStyleRepository> repository)
    {
        _repository = repository;
    }

    public async Task LoadAsync()
    {
        var all = await _repository().GetAllAsync();
        lock (_gate)
        {
            _styles.Clear();
            foreach (var style in all)
                _styles[style.Site] = style;
        }
    }

    /// <summary>The saved style for a site (also when switched off), or null.</summary>
    public CustomStyle? Get(string? site)
    {
        if (string.IsNullOrEmpty(site))
            return null;
        lock (_gate)
            return _styles.TryGetValue(site, out var style) ? style : null;
    }

    /// <summary>What to inject into a page: the site's CSS with !important added, or null when none applies.</summary>
    public string? CssForPage(string? url)
    {
        var style = Get(CustomCss.SiteKey(url));
        return style is { Enabled: true } && !string.IsNullOrWhiteSpace(style.Css)
            ? CustomCss.AddImportant(style.Css)
            : null;
    }

    public async Task SaveAsync(string site, string css, bool enabled)
    {
        await _repository().SaveAsync(site, css, enabled);
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(css))
                _styles.Remove(site);
            else
                _styles[site] = new CustomStyle { Site = site, Css = css, Enabled = enabled, UpdatedAt = DateTime.UtcNow };
        }
    }
}
