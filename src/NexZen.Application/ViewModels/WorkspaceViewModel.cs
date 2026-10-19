using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;

namespace NexZen.Application.ViewModels;

public partial class WorkspaceTabItem : ObservableObject
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    /// <summary>A data: URL (icon captured from the tab or loaded from the site), if any.</summary>
    [ObservableProperty] private string? _faviconUrl;
    public required WorkspaceCardViewModel Card { get; init; }
}

public sealed record WorkspaceOpenRequest(int WorkspaceId, string Name, IReadOnlyList<string> Urls, string? FocusUrl, string? Color = null);

/// <summary>The "Add new" tile shown before the workspace cards.</summary>
public sealed class AddWorkspaceTile
{
}

public partial class WorkspaceCardViewModel : ObservableObject
{
    public int Id { get; init; }
    /// <summary>Border color (#RRGGBB) or null.</summary>
    public string? Color { get; init; }
    /// <summary>The screen the card belongs to. Its menu binds commands through this:
    /// the flyout lives in a popup, outside the card list's visual tree.</summary>
    public WorkspaceViewModel? Owner { get; init; }
    [ObservableProperty] private string _name = "";
    public ObservableCollection<WorkspaceTabItem> Tabs { get; } = new();
    public bool IsEmpty => Tabs.Count == 0;

    public WorkspaceCardViewModel()
    {
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>
/// The NexZen workspace screen: saved, fixed groups of tabs. Opening one starts a new window
/// with its tabs; the window it was opened from stays as it is.
/// </summary>
public partial class WorkspaceViewModel : ObservableObject
{
    private readonly Func<IWorkspaceRepository> _repository;
    private readonly IFaviconFetcher? _favicons;

    public ObservableCollection<WorkspaceCardViewModel> Workspaces { get; } = new();
    /// <summary>What the grid shows: the "Add new" tile, then the cards.</summary>
    public ObservableCollection<object> Tiles { get; } = new();
    private readonly AddWorkspaceTile _addTile = new();

    /// <summary>The active tab of that window, or null when it is a blank page.</summary>
    public Func<WorkspaceTab?>? GetActiveTab { get; set; }
    /// <summary>
    /// Shows a workspace in a browser window: the one already open for it, or a new one with
    /// its tabs. When FocusUrl is set, that tab is selected (added if it was closed).
    /// </summary>
    public Action<WorkspaceOpenRequest>? OpenWorkspace { get; set; }
    /// <summary>Opens the pinned editor window: null for a new workspace, or the id to update.</summary>
    public Action<int?>? OpenEditor { get; set; }

    [RelayCommand]
    private void AddNew() => OpenEditor?.Invoke(null);

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmMessage = "";
    private WorkspaceCardViewModel? _pendingDelete;

    public WorkspaceViewModel(Func<IWorkspaceRepository> repository, IFaviconFetcher? favicons = null)
    {
        _repository = repository;
        _favicons = favicons;
        Workspaces.CollectionChanged += (_, _) => RebuildTiles();
        RebuildTiles();
    }

    private void RebuildTiles()
    {
        Tiles.Clear();
        Tiles.Add(_addTile);
        foreach (var card in Workspaces)
            Tiles.Add(card);
    }

    public async Task LoadAsync()
    {
        var saved = await _repository().GetAllAsync();
        Workspaces.Clear();
        foreach (var workspace in saved)
            Workspaces.Add(ToCard(workspace));
        StatusText = "";
        _ = FillMissingIconsAsync();
    }

    /// <summary>
    /// Pasted links are saved before their icon is known; load those icons in the background
    /// and keep them, so each site is fetched once.
    /// </summary>
    public async Task FillMissingIconsAsync()
    {
        if (_favicons == null)
            return;
        var missing = Workspaces.SelectMany(w => w.Tabs)
            .Where(t => string.IsNullOrEmpty(t.FaviconUrl) && t.Id > 0)
            .ToList();
        foreach (var site in missing.GroupBy(t => Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u.Host : t.Url))
        {
            string? icon;
            try { icon = await _favicons.FetchDataUrlAsync(site.First().Url); }
            catch (Exception) { continue; }
            if (string.IsNullOrEmpty(icon))
                continue;
            foreach (var tab in site)
            {
                tab.FaviconUrl = icon;
                try { await _repository().SetTabFaviconAsync(tab.Id, icon); }
                catch (Exception) { }
            }
        }
    }

    [RelayCommand]
    private void Open(WorkspaceCardViewModel? card)
    {
        if (card == null)
            return;
        var urls = card.Tabs.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0)
        {
            StatusText = $"\"{card.Name}\" has no tabs yet.";
            return;
        }
        OpenWorkspace?.Invoke(new WorkspaceOpenRequest(card.Id, card.Name, urls, null, card.Color));
    }

    [RelayCommand]
    private void OpenTab(WorkspaceTabItem? tab)
    {
        if (tab == null || string.IsNullOrWhiteSpace(tab.Url))
            return;
        var urls = tab.Card.Tabs.Select(t => t.Url).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        OpenWorkspace?.Invoke(new WorkspaceOpenRequest(tab.Card.Id, tab.Card.Name, urls, tab.Url, tab.Card.Color));
    }

    [RelayCommand]
    private async Task AddThisTabAsync(WorkspaceCardViewModel? card)
    {
        if (card == null)
            return;
        var tab = GetActiveTab?.Invoke();
        if (tab == null)
        {
            StatusText = "Open a web page first, then add it.";
            return;
        }
        var saved = await _repository().AddTabAsync(card.Id, tab);
        if (saved != null)
        {
            card.Tabs.Add(ToItem(saved, card));
            StatusText = $"Added \"{saved.Title}\" to {card.Name}.";
        }
    }

    [RelayCommand]
    private void Update(WorkspaceCardViewModel? card)
    {
        // Name and tabs are changed in the same pinned window used by "Add new".
        if (card != null)
            OpenEditor?.Invoke(card.Id);
    }

    [RelayCommand]
    private void AskDelete(WorkspaceCardViewModel? card)
    {
        if (card == null)
            return;
        _pendingDelete = card;
        ConfirmMessage = $"Delete \"{card.Name}\" and its {card.Tabs.Count} saved tab(s)? Open windows are not affected.";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        var card = _pendingDelete;
        _pendingDelete = null;
        IsConfirmOpen = false;
        if (card == null)
            return;
        if (await _repository().DeleteAsync(card.Id))
        {
            Workspaces.Remove(card);
            StatusText = $"Deleted {card.Name}.";
        }
    }

    [RelayCommand]
    private void CancelDelete()
    {
        _pendingDelete = null;
        IsConfirmOpen = false;
    }

    /// <summary>
    /// What gets saved from an open tab, or null for pages that cannot be reopened later
    /// (blank/new tab, internal nexzen:, chrome:, extension pages).
    /// </summary>
    public static WorkspaceTab? FromOpenTab(TabItemViewModel? tab)
    {
        var url = (tab?.Url ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        return new WorkspaceTab
        {
            Title = string.IsNullOrWhiteSpace(tab!.Title) ? uri.Host : tab.Title.Trim(),
            Url = url,
            // The captured PNG keeps the icon visible offline and needs no network fetch.
            FaviconUrl = tab.FaviconPng is { Length: > 0 } png
                ? "data:image/png;base64," + Convert.ToBase64String(png)
                : null
        };
    }

    private WorkspaceCardViewModel ToCard(Workspace workspace)
    {
        var card = new WorkspaceCardViewModel { Id = workspace.Id, Name = workspace.Name, Color = workspace.Color, Owner = this };
        foreach (var tab in workspace.Tabs)
            card.Tabs.Add(ToItem(tab, card));
        return card;
    }

    private static WorkspaceTabItem ToItem(WorkspaceTab tab, WorkspaceCardViewModel card) => new()
    {
        Id = tab.Id,
        Title = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title,
        Url = tab.Url,
        FaviconUrl = tab.FaviconUrl,
        Card = card
    };
}
