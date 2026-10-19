using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;

namespace NexZen.Application.ViewModels;

/// <summary>The pinned Custom CSS window for one site.</summary>
public partial class CustomCssViewModel : ObservableObject
{
    private readonly CustomStyleStore _store;
    private bool _loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _site = "";
    [ObservableProperty] private string _css = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _statusText = "";

    public string Title => $"Custom CSS - {CustomCss.DisplaySite(Site)}";

    /// <summary>Reloads the tab the CSS was written for, after a save or a switch.</summary>
    public Action? ReloadPage { get; set; }
    public Action? CloseRequested { get; set; }

    public CustomCssViewModel(CustomStyleStore store)
    {
        _store = store;
    }

    /// <summary>Shows the saved CSS of the page's site; false when the page is not a web site.</summary>
    public bool Load(string? pageUrl)
    {
        var site = CustomCss.SiteKey(pageUrl);
        if (site == null)
            return false;
        _loading = true;
        try
        {
            var saved = _store.Get(site);
            Site = site;
            Css = saved?.Css ?? "";
            Enabled = saved?.Enabled ?? true;
            StatusText = "";
        }
        finally
        {
            _loading = false;
        }
        return true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Site.Length == 0)
            return;
        await _store.SaveAsync(Site, Css ?? "", Enabled);
        StatusText = string.IsNullOrWhiteSpace(Css) ? "Custom CSS removed." : "Saved.";
        ReloadPage?.Invoke();
    }

    // The switch applies at once when this site already has saved CSS.
    partial void OnEnabledChanged(bool value)
    {
        if (_loading || Site.Length == 0 || _store.Get(Site) is not { } saved)
            return;
        _ = ApplySwitchAsync(saved.Css, value);
    }

    private async Task ApplySwitchAsync(string savedCss, bool enabled)
    {
        await _store.SaveAsync(Site, savedCss, enabled);
        StatusText = enabled ? "Custom CSS on." : "Custom CSS off.";
        ReloadPage?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();
}
