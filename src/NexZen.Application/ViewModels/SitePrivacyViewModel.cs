using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;

namespace NexZen.Application.ViewModels;

/// <summary>One site in the list: its domain, icon (data URL) and storage use.</summary>
public partial class SitePrivacySite : ObservableObject
{
    public string Host { get; init; } = "";
    public string? FaviconDataUrl { get; init; }
    [ObservableProperty] private string _usageText = "";
}

/// <summary>A permission row: label, optional note, icon path data and the chosen setting.</summary>
public partial class SitePermission : ObservableObject
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public string IconData { get; init; } = "";
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();
    public int DefaultIndex { get; init; }
    [ObservableProperty] private int _selectedIndex;

    public bool HasDescription => !string.IsNullOrEmpty(Description);
}

/// <summary>
/// "Site privacy": storage use and permissions of the sites on the current page.
/// Interface only for now: the numbers are placeholders and the buttons do not change anything.
/// </summary>
public partial class SitePrivacyViewModel : ObservableObject
{
    private readonly List<SitePrivacySite> _allSites = new();

    public ObservableCollection<SitePrivacySite> Sites { get; } = new();
    public ObservableCollection<SitePermission> Permissions { get; } = new();

    [ObservableProperty] private string _filter = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedHost), nameof(SelectedUsage))]
    private SitePrivacySite? _selectedSite;
    [ObservableProperty] private string _statusText = "";

    public bool HasSelection => SelectedSite != null;
    public string SelectedHost => SelectedSite?.Host ?? "";
    public string SelectedUsage => SelectedSite?.UsageText ?? "";
    public bool HasNoSites => Sites.Count == 0;

    public Action? CloseRequested { get; set; }

    public SitePrivacyViewModel()
    {
        // Chrome's per-site settings, with Chrome's defaults.
        AddPermission("location", "Location", null, Icons.Location, AskAllowBlock, 0);
        AddPermission("camera", "Camera", null, Icons.Camera, AskAllowBlock, 0);
        AddPermission("microphone", "Microphone", null, Icons.Microphone, AskAllowBlock, 0);
        AddPermission("motion", "Motion sensors", null, Icons.Motion, AllowBlock, 0);
        AddPermission("notifications", "Notifications", null, Icons.Notifications, AskAllowBlock, 0);
        AddPermission("javascript", "JavaScript", null, Icons.JavaScript, AllowBlock, 0);
        AddPermission("images", "Images", null, Icons.Images, AllowBlock, 0);
        AddPermission("popups", "Pop-ups and redirects", null, Icons.Popups, BlockAllow, 0);
        AddPermission("ads", "Intrusive ads", "Block if site shows intrusive or misleading ads", Icons.Ads, BlockAllow, 0);
    }

    private static readonly string[] AskAllowBlock = { "Ask (default)", "Allow", "Block" };
    private static readonly string[] AllowBlock = { "Allow (default)", "Block" };
    private static readonly string[] BlockAllow = { "Block (default)", "Allow" };

    private void AddPermission(string key, string label, string? description, string icon, string[] choices, int defaultIndex)
        => Permissions.Add(new SitePermission
        {
            Key = key,
            Label = label,
            Description = description,
            IconData = icon,
            Choices = choices,
            DefaultIndex = defaultIndex,
            SelectedIndex = defaultIndex
        });

    /// <summary>Shows the sites of the page (for now only the page's own site, without real usage).</summary>
    public void Load(string? pageUrl, byte[]? faviconPng)
    {
        _allSites.Clear();
        var host = CustomCss.SiteKey(pageUrl);
        if (host != null)
        {
            _allSites.Add(new SitePrivacySite
            {
                Host = host,
                FaviconDataUrl = faviconPng is { Length: > 0 } ? "data:image/png;base64," + Convert.ToBase64String(faviconPng) : null,
                UsageText = "— · — cookies"
            });
        }
        Filter = "";
        StatusText = "";
        ApplyFilter();
        SelectedSite = Sites.FirstOrDefault();
        foreach (var permission in Permissions)
            permission.SelectedIndex = permission.DefaultIndex;
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = (Filter ?? "").Trim();
        Sites.Clear();
        foreach (var site in _allSites)
        {
            if (filter.Length == 0 || site.Host.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Sites.Add(site);
        }
        OnPropertyChanged(nameof(HasNoSites));
        if (SelectedSite != null && !Sites.Contains(SelectedSite))
            SelectedSite = Sites.FirstOrDefault();
    }

    [RelayCommand]
    private void DeleteData() => StatusText = "Deleting site data is not available yet.";

    [RelayCommand]
    private void ResetPermissions()
    {
        foreach (var permission in Permissions)
            permission.SelectedIndex = permission.DefaultIndex;
        StatusText = "Permissions reset to their defaults (not applied to the site yet).";
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    /// <summary>Material Design icon paths (24×24), as used elsewhere in NexZen.</summary>
    private static class Icons
    {
        public const string Location = "M12,11.5A2.5,2.5 0 0,1 9.5,9A2.5,2.5 0 0,1 12,6.5A2.5,2.5 0 0,1 14.5,9A2.5,2.5 0 0,1 12,11.5M12,2A7,7 0 0,0 5,9C5,14.25 12,22 12,22C12,22 19,14.25 19,9A7,7 0 0,0 12,2Z";
        public const string Camera = "M17,10.5V7A1,1 0 0,0 16,6H4A1,1 0 0,0 3,7V17A1,1 0 0,0 4,18H16A1,1 0 0,0 17,17V13.5L21,17.5V6.5L17,10.5Z";
        public const string Microphone = "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z";
        public const string Motion = "M12,10A2,2 0 0,1 14,12A2,2 0 0,1 12,14A2,2 0 0,1 10,12A2,2 0 0,1 12,10M4.93,4.93L6.34,6.34C4.89,7.79 4,9.79 4,12C4,14.21 4.89,16.21 6.34,17.66L4.93,19.07C3.12,17.26 2,14.76 2,12C2,9.24 3.12,6.74 4.93,4.93M19.07,4.93C20.88,6.74 22,9.24 22,12C22,14.76 20.88,17.26 19.07,19.07L17.66,17.66C19.11,16.21 20,14.21 20,12C20,9.79 19.11,7.79 17.66,6.34L19.07,4.93M7.76,7.76L9.17,9.17C8.45,9.9 8,10.9 8,12C8,13.1 8.45,14.1 9.17,14.83L7.76,16.24C6.67,15.15 6,13.65 6,12C6,10.35 6.67,8.85 7.76,7.76M16.24,7.76C17.33,8.85 18,10.35 18,12C18,13.65 17.33,15.15 16.24,16.24L14.83,14.83C15.55,14.1 16,13.1 16,12C16,10.9 15.55,9.9 14.83,9.17L16.24,7.76Z";
        public const string Notifications = "M21,19V20H3V19L5,17V11C5,7.9 7.03,5.17 10,4.29C10,4.19 10,4.1 10,4A2,2 0 0,1 12,2A2,2 0 0,1 14,4C14,4.1 14,4.19 14,4.29C16.97,5.17 19,7.9 19,11V17L21,19M14,21A2,2 0 0,1 12,23A2,2 0 0,1 10,21";
        public const string JavaScript = "M8,3L3,8L8,13L9.41,11.59L5.83,8L9.41,4.41L8,3M16,3L14.59,4.41L18.17,8L14.59,11.59L16,13L21,8L16,3Z";
        public const string Images = "M21,19V5C21,3.9 20.1,3 19,3H5C3.9,3 3,3.9 3,5V19C3,20.1 3.9,21 5,21H19C20.1,21 21,20.1 21,19M5,5H19V19H5V5M7,17L10.5,12.5L13,15.5L16.5,11L19,17H7Z";
        public const string Popups = "M14,3V5H17.59L7.76,14.83L9.17,16.24L19,6.41V10H21V3M19,19H5V5H12V3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V12H19V19Z";
        public const string Ads = "M19,4H5C3.89,4 3,4.9 3,6V18A2,2 0 0,0 5,20H19A2,2 0 0,0 21,18V6C21,4.89 20.1,4 19,4M19,18H5V8H19V18Z";
    }
}
