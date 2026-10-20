using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;

namespace NexZen.Application.ViewModels;

/// <summary>Storage use of one site, as measured by the browser.</summary>
public sealed record SiteUsage(string Site, long Bytes, int Cookies);

/// <summary>Browser side of Site privacy for one page (implemented with CEF in the desktop app).</summary>
public interface ISiteDataService
{
    /// <summary>The sites the page loaded something from (the page's own site first).</summary>
    Task<IReadOnlyList<string>> GetSitesAsync();
    /// <summary>Storage plus cookie size and cookie count of each site.</summary>
    Task<IReadOnlyList<SiteUsage>> GetUsageAsync(IReadOnlyList<string> sites);
    /// <summary>Deletes the site's cookies and stored data (local storage, IndexedDB, cache, ...).</summary>
    Task DeleteDataAsync(string site);
    /// <summary>Applies a permission choice to the browser (also used for the defaults).</summary>
    void Apply(string site, string key, SitePermissionValue value);
}

/// <summary>One site in the list: its domain, icon (data URL) and storage use.</summary>
public partial class SitePrivacySite : ObservableObject
{
    public string Host { get; init; } = "";
    [ObservableProperty] private string? _faviconDataUrl;
    [ObservableProperty] private string _usageText = "Measuring...";
}

/// <summary>A permission row: label, optional note, icon path data and the chosen setting.</summary>
public partial class SitePermission : ObservableObject
{
    private readonly SitePrivacyViewModel? _owner;

    public SitePermission(SitePrivacyViewModel? owner, SitePermissionDefinition definition, string iconData)
    {
        _owner = owner;
        Definition = definition;
        IconData = iconData;
    }

    public SitePermissionDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Label => Definition.Label;
    public string? Description => Definition.Description;
    public string IconData { get; }
    public IReadOnlyList<string> Choices => Definition.Choices;
    public int DefaultIndex => 0;
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public SitePermissionValue Value => Definition.Values[Math.Clamp(SelectedIndex, 0, Definition.Values.Count - 1)];

    [ObservableProperty] private int _selectedIndex;

    internal bool Loading { get; set; }

    partial void OnSelectedIndexChanged(int value)
    {
        if (!Loading)
            _owner?.OnPermissionChanged(this);
    }
}

/// <summary>
/// "Site privacy": storage use and permissions of the sites on the current page.
/// Choices are saved per site and applied to the browser; Delete data clears a site's
/// cookies and storage.
/// </summary>
public partial class SitePrivacyViewModel : ObservableObject
{
    private readonly SitePermissionStore? _store;
    private readonly List<SitePrivacySite> _allSites = new();
    private ISiteDataService? _service;
    private int _loadGeneration;

    public ObservableCollection<SitePrivacySite> Sites { get; } = new();
    public ObservableCollection<SitePermission> Permissions { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedHost), nameof(SelectedUsage))]
    private SitePrivacySite? _selectedSite;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteLabel))]
    private bool _confirmingDelete;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteDataCommand))]
    private bool _isDeleting;

    public bool HasSelection => SelectedSite != null;
    public string SelectedHost => SelectedSite?.Host ?? "";
    public string SelectedUsage => SelectedSite?.UsageText ?? "";
    public bool HasNoSites => Sites.Count == 0;
    public string DeleteLabel => ConfirmingDelete ? "Click again to delete" : "Delete data";

    public Action? CloseRequested { get; set; }

    public SitePrivacyViewModel(SitePermissionStore? store = null)
    {
        _store = store;
        foreach (var definition in SitePermissionKeys.All)
            Permissions.Add(new SitePermission(this, definition, Icons.For(definition.Key)));
    }

    /// <summary>Shows the page's site at once, then the other sites of the page and their usage.</summary>
    public async Task LoadAsync(string? pageUrl, byte[]? faviconPng, ISiteDataService? service)
    {
        var generation = ++_loadGeneration;
        _service = service;
        _allSites.Clear();
        Filter = "";
        StatusText = "";
        ConfirmingDelete = false;
        var pageSite = SiteDomains.SiteOf(pageUrl);
        if (pageSite != null)
        {
            _allSites.Add(new SitePrivacySite
            {
                Host = pageSite,
                FaviconDataUrl = faviconPng is { Length: > 0 } ? "data:image/png;base64," + Convert.ToBase64String(faviconPng) : null
            });
        }
        ApplyFilter();
        SelectedSite = Sites.FirstOrDefault();
        if (service == null)
            return;

        try
        {
            var found = await service.GetSitesAsync();
            if (generation != _loadGeneration)
                return;
            foreach (var site in found)
            {
                if (_allSites.All(s => s.Host != site))
                    _allSites.Add(new SitePrivacySite { Host = site });
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            // The page's own site is still listed and measured.
            if (generation == _loadGeneration)
                StatusText = "Couldn't read the other sites of this page: " + ex.Message;
        }
        await RefreshUsageAsync(generation);
    }

    private async Task RefreshUsageAsync(int generation)
    {
        if (_service == null || _allSites.Count == 0)
            return;
        IReadOnlyList<SiteUsage> usage;
        try
        {
            usage = await _service.GetUsageAsync(_allSites.Select(s => s.Host).ToList());
        }
        catch (Exception ex)
        {
            if (generation != _loadGeneration)
                return;
            foreach (var site in _allSites)
                site.UsageText = "Usage unavailable";
            OnPropertyChanged(nameof(SelectedUsage));
            StatusText = "Couldn't measure the usage: " + ex.Message;
            return;
        }
        if (generation != _loadGeneration)
            return;
        foreach (var site in _allSites)
        {
            var entry = usage.FirstOrDefault(u => u.Site == site.Host);
            site.UsageText = entry == null ? SiteDomains.UsageText(0, 0) : SiteDomains.UsageText(entry.Bytes, entry.Cookies);
        }
        OnPropertyChanged(nameof(SelectedUsage));
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedSiteChanged(SitePrivacySite? value)
    {
        ConfirmingDelete = false;
        foreach (var permission in Permissions)
        {
            permission.Loading = true;
            var stored = _store?.Get(value?.Host, permission.Key) ?? SitePermissionValue.Default;
            permission.SelectedIndex = permission.Definition.IndexOf(stored);
            permission.Loading = false;
        }
    }

    private void ApplyFilter()
    {
        var filter = (Filter ?? "").Trim();
        var selected = SelectedSite;
        Sites.Clear();
        foreach (var site in _allSites)
        {
            if (filter.Length == 0 || site.Host.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Sites.Add(site);
        }
        OnPropertyChanged(nameof(HasNoSites));
        SelectedSite = selected != null && Sites.Contains(selected) ? selected : Sites.FirstOrDefault();
    }

    internal async void OnPermissionChanged(SitePermission permission)
    {
        var site = SelectedSite?.Host;
        if (site == null)
            return;
        var value = permission.Value;
        try
        {
            if (_store != null)
                await _store.SetAsync(site, permission.Key, value);
            _service?.Apply(site, permission.Key, value);
            StatusText = $"{permission.Label}: {permission.Choices[permission.SelectedIndex]} for {site}. Reload the page to apply.";
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't save the setting: " + ex.Message;
        }
    }

    private bool CanDelete() => !IsDeleting;

    /// <summary>First click asks for confirmation; the second deletes the site's cookies and storage.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteDataAsync()
    {
        var site = SelectedSite;
        if (site == null || _service == null)
            return;
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            StatusText = $"Delete all cookies and stored data of {site.Host}? Click the button again to confirm.";
            return;
        }
        ConfirmingDelete = false;
        IsDeleting = true;
        StatusText = $"Deleting data of {site.Host}...";
        try
        {
            await _service.DeleteDataAsync(site.Host);
            await RefreshUsageAsync(_loadGeneration);
            StatusText = $"Deleted cookies and stored data of {site.Host}.";
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't delete the data: " + ex.Message;
        }
        finally
        {
            IsDeleting = false;
        }
    }

    [RelayCommand]
    private async Task ResetPermissionsAsync()
    {
        var site = SelectedSite?.Host;
        if (site == null)
            return;
        try
        {
            if (_store != null)
                await _store.ResetAsync(site);
            foreach (var permission in Permissions)
            {
                permission.Loading = true;
                permission.SelectedIndex = 0;
                permission.Loading = false;
                _service?.Apply(site, permission.Key, SitePermissionValue.Default);
            }
            StatusText = $"Permissions of {site} are back to their defaults. Reload the page to apply.";
        }
        catch (Exception ex)
        {
            StatusText = "Couldn't reset the permissions: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Material Design icon paths (24×24), as used elsewhere in NexZen.</summary>
    private static class Icons
    {
        public static string For(string key) => key switch
        {
            SitePermissionKeys.Location => "M12,11.5A2.5,2.5 0 0,1 9.5,9A2.5,2.5 0 0,1 12,6.5A2.5,2.5 0 0,1 14.5,9A2.5,2.5 0 0,1 12,11.5M12,2A7,7 0 0,0 5,9C5,14.25 12,22 12,22C12,22 19,14.25 19,9A7,7 0 0,0 12,2Z",
            SitePermissionKeys.Camera => "M17,10.5V7A1,1 0 0,0 16,6H4A1,1 0 0,0 3,7V17A1,1 0 0,0 4,18H16A1,1 0 0,0 17,17V13.5L21,17.5V6.5L17,10.5Z",
            SitePermissionKeys.Microphone => "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z",
            SitePermissionKeys.Motion => "M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10M4.93,4.93L6.34,6.34C4.89,7.79 4,9.79 4,12C4,14.21 4.89,16.21 6.34,17.66L4.93,19.07C3.12,17.26 2,14.76 2,12C2,9.24 3.12,6.74 4.93,4.93M19.07,4.93C20.88,6.74 22,9.24 22,12C22,14.76 20.88,17.26 19.07,19.07L17.66,17.66C19.11,16.21 20,14.21 20,12C20,9.79 19.11,7.79 17.66,6.34L19.07,4.93M7.76,7.76L9.17,9.17C8.45,9.9 8,10.9 8,12C8,13.1 8.45,14.1 9.17,14.83L7.76,16.24C6.67,15.15 6,13.65 6,12C6,10.35 6.67,8.85 7.76,7.76M16.24,7.76C17.33,8.85 18,10.35 18,12C18,13.65 17.33,15.15 16.24,16.24L14.83,14.83C15.55,14.1 16,13.1 16,12C16,10.9 15.55,9.9 14.83,9.17L16.24,7.76Z",
            SitePermissionKeys.Notifications => "M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21",
            SitePermissionKeys.JavaScript => "M8,3L3,8L8,13L9.41,11.59L5.83,8L9.41,4.41L8,3M16,3L14.59,4.41L18.17,8L14.59,11.59L16,13L21,8L16,3Z",
            SitePermissionKeys.Images => "M21,19V5C21,3.9 20.1,3 19,3H5C3.9,3 3,3.9 3,5V19C3,20.1 3.9,21 5,21H19C20.1,21 21,20.1 21,19M5,5H19V19H5V5M7,17L10.5,12.5L13,15.5L16.5,11L19,17H7Z",
            SitePermissionKeys.Popups => "M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z",
            SitePermissionKeys.BackgroundSync => "M12,18A6,6 0 0,1 6,12C6,11 6.25,10.03 6.7,9.2L5.24,7.74C4.46,8.97 4,10.43 4,12A8,8 0 0,0 12,20V23L16,19L12,15M12,4V1L8,5L12,9V6A6,6 0 0,1 18,12C18,13 17.75,13.97 17.3,14.8L18.76,16.26C19.54,15.03 20,13.57 20,12A8,8 0 0,0 12,4Z",
            SitePermissionKeys.Sound => "M3,9V15H7L12,20V4L7,9H3M16.5,12C16.5,10.23 15.48,8.71 14,7.97V16.02C15.48,15.29 16.5,13.77 16.5,12M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.85 14,18.71V20.77C18.01,19.86 21,16.28 21,12C21,7.72 18.01,4.14 14,3.23Z",
            SitePermissionKeys.AutomaticDownloads => "M5,20H19V18H5V20M19,9H15V3H9V9H5L12,16L19,9Z",
            SitePermissionKeys.Midi => "M3,4H21V20H3V4M5,6V18H19V6H5M7,6H9V13H7V6M11,6H13V13H11V6M15,6H17V13H15V6M9.5,13H10.5V18H9.5V13M13.5,13H14.5V18H13.5V13Z",
            SitePermissionKeys.Usb => "M11,2H13V15.3A2.5,2.5 0 1,1 11,15.3V2M7,7H9V9.7L11,11.2V13.4L7,10.6V7M15,5H17V9.6L13,12.4V10.2L15,8.7V5Z",
            SitePermissionKeys.Serial => "M3,6H21V18H3V6M5,8V16H19V8H5M7,10H9V12H7V10M11,10H13V12H11V10M15,10H17V12H15V10M9,13H11V15H9V13M13,13H15V15H13V13Z",
            SitePermissionKeys.FileEditing => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H11V20H6V4H13V9H18V11H20V8L14,2M19.3,13L20.3,14L15.3,19L14,19.3L14.3,18L19.3,13M21,12.3L22,13.3L21.1,14.2L20.1,13.2L21,12.3Z",
            SitePermissionKeys.Hid => "M7,6H17A5,5 0 0,1 22,11V13A5,5 0 0,1 17,18H7A5,5 0 0,1 2,13V11A5,5 0 0,1 7,6M6,9V11H4V13H6V15H8V13H10V11H8V9H6M16,9.5A1.5,1.5 0 1,0 17.5,11A1.5,1.5 0 0,0 16,9.5M18.5,12A1.5,1.5 0 1,0 20,13.5A1.5,1.5 0 0,0 18.5,12Z",
            SitePermissionKeys.Clipboard => "M19,4H17.82C17.4,2.84 16.3,2 15,2H9C7.7,2 6.6,2.84 6.18,4H5C3.9,4 3,4.9 3,6V20C3,21.1 3.9,22 5,22H19C20.1,22 21,21.1 21,20V6C21,4.9 20.1,4 19,4M9,4H15V6H9V4M19,20H5V6H7V8H17V6H19V20Z",
            SitePermissionKeys.PaymentHandlers => "M20,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V6A2,2 0 0,0 20,4M20,18H4V12H20V18M20,8H4V6H20V8Z",
            SitePermissionKeys.InsecureContent => "M12,2L1,21H23L12,2M12,6L19.53,19H4.47L12,6M11,10V14H13V10H11M11,16V18H13V16H11Z",
            SitePermissionKeys.JavaScriptOptimization => "M12,1L3,5V11C3,16.55 6.84,21.74 12,23C17.16,21.74 21,16.55 21,11V5L12,1M12,3.18L19,6.3V11C19,15.52 16.02,19.69 12,20.93C7.98,19.69 5,15.52 5,11V6.3L12,3.18M13,7L9,13H11.5L11,17L15,11H12.5L13,7Z",
            SitePermissionKeys.ThirdPartySignIn => "M12,2A10,10 0 1,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 18.36,16.85C16.97,15.16 14.6,14 12,14C9.4,14 7.03,15.16 5.64,16.85A8,8 0 0,1 12,4M12,6A3.5,3.5 0 1,0 15.5,9.5A3.5,3.5 0 0,0 12,6Z",
            SitePermissionKeys.AugmentedReality or SitePermissionKeys.VirtualReality => "M5,7H19A3,3 0 0,1 22,10V15A3,3 0 0,1 19,18H16L14,15H10L8,18H5A3,3 0 0,1 2,15V10A3,3 0 0,1 5,7M7.5,10.5A1.5,1.5 0 1,0 9,12A1.5,1.5 0 0,0 7.5,10.5M16.5,10.5A1.5,1.5 0 1,0 18,12A1.5,1.5 0 0,0 16.5,10.5Z",
            SitePermissionKeys.DeviceUse => "M4,5H20V16H4V5M6,7V14H18V7H6M2,17H22V19H2V17Z",
            SitePermissionKeys.WindowManagement => "M3,3H15V13H3V3M5,6V11H13V6H5M9,15H19V7H21V17H9V15Z",
            SitePermissionKeys.Fonts => "M9.62,12L12,5.67L14.37,12M11,3L5.5,17H7.75L8.87,14H15.12L16.25,17H18.5L13,3H11Z",
            SitePermissionKeys.AutoPictureInPicture => "M19,11H11V17H19V11M21,3H3A2,2 0 0,0 1,5V19A2,2 0 0,0 3,21H21A2,2 0 0,0 23,19V5A2,2 0 0,0 21,3M21,19H3V5H21V19Z",
            SitePermissionKeys.SharedTabsControl => "M11,1.07C7.05,1.56 4,4.92 4,9H11V1.07M4,15A8,8 0 0,0 12,23A8,8 0 0,0 20,15V11H4V15M13,1.07V9H20C20,4.92 16.94,1.56 13,1.07Z",
            SitePermissionKeys.WebAppInstallations => "M4,4H20A2,2 0 0,1 22,6V16A2,2 0 0,1 20,18H15V20H17V22H7V20H9V18H4A2,2 0 0,1 2,16V6A2,2 0 0,1 4,4M4,6V16H20V6H4M11,7H13V11H15L12,14L9,11H11V7Z",
            SitePermissionKeys.LocalNetwork => "M4,14H20A2,2 0 0,1 22,16V19A2,2 0 0,1 20,21H4A2,2 0 0,1 2,19V16A2,2 0 0,1 4,14M5,17V18H7V17H5M8,17V18H10V17H8M11,17V18H13V17H11M12,6A6,6 0 0,1 17,8.7L15.6,10.1A4,4 0 0,0 8.4,10.1L7,8.7A6,6 0 0,1 12,6M12,10A2,2 0 0,1 13.4,10.6L12,12L10.6,10.6A2,2 0 0,1 12,10M12,2A10,10 0 0,1 19.8,5.8L18.4,7.2A8,8 0 0,0 5.6,7.2L4.2,5.8A10,10 0 0,1 12,2Z",
            SitePermissionKeys.AppsOnDevice => "M21,16H3V4H21M21,2H3C1.89,2 1,2.89 1,4V16A2,2 0 0,0 3,18H10V20H8V22H16V20H14V18H21A2,2 0 0,0 23,16V4C23,2.89 22.1,2 21,2Z",
            _ => "M19,4H5C3.89,4 3,4.9 3,6V18A2,2 0 0,0 5,20H19A2,2 0 0,0 21,18V6C21,4.89 20.1,4 19,4M19,18H5V8H19V18Z"
        };
    }
}
