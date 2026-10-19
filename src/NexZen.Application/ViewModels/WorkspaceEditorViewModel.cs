using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;

namespace NexZen.Application.ViewModels;

public partial class WorkspaceEditorEntry : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private string? _faviconUrl;
}

/// <summary>One swatch in the workspace color row; Hex null means "no color".</summary>
public partial class WorkspaceColorChoice : ObservableObject
{
    public string? Hex { get; init; }
    public required string Name { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The pinned "Add new" window: name a workspace, collect tabs from the browser window
/// or pasted links, then save it.
/// </summary>
public partial class WorkspaceEditorViewModel : ObservableObject
{
    private readonly Func<IWorkspaceRepository> _repository;
    private readonly IFaviconFetcher? _favicons;
    private readonly List<Task> _pendingIcons = new();

    public ObservableCollection<WorkspaceEditorEntry> Entries { get; } = new();

    /// <summary>Border colors to tell workspace windows apart; the first is "none".</summary>
    public static readonly IReadOnlyList<(string? Hex, string Name)> Palette = new (string?, string)[]
    {
        (null, "No color"),
        ("#E53935", "Red"),
        ("#FB8C00", "Orange"),
        ("#FDD835", "Yellow"),
        ("#43A047", "Green"),
        ("#00ACC1", "Teal"),
        ("#1E88E5", "Blue"),
        ("#8E24AA", "Purple"),
        ("#D81B60", "Pink"),
    };

    public ObservableCollection<WorkspaceColorChoice> Colors { get; } = new(
        Palette.Select(p => new WorkspaceColorChoice { Hex = p.Hex, Name = p.Name, IsSelected = p.Hex == null }));

    [ObservableProperty] private string? _selectedColor;

    /// <summary>Tabs of the browser window the editor was opened from (web pages only).</summary>
    public Func<IReadOnlyList<WorkspaceTab>>? GetCurrentTabs { get; set; }
    /// <summary>Called after a successful save with the new workspace.</summary>
    public Action<Workspace>? Saved { get; set; }
    public Action? CloseRequested { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _statusText = "";
    /// <summary>The workspace being updated, or null while creating a new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    private int? _editingId;

    public string SaveLabel => EditingId == null ? "Save workspace" : "Update workspace";

    public WorkspaceEditorViewModel(Func<IWorkspaceRepository> repository, IFaviconFetcher? favicons = null)
    {
        _repository = repository;
        _favicons = favicons;
    }

    /// <summary>Starts a new, empty workspace.</summary>
    public void Reset()
    {
        EditingId = null;
        Name = "";
        Entries.Clear();
        StatusText = "";
        SelectColor(null);
    }

    /// <summary>Loads a saved workspace to change its name and tabs.</summary>
    public void BeginEdit(Workspace workspace)
    {
        Reset();
        EditingId = workspace.Id;
        Name = workspace.Name;
        SelectColor(workspace.Color);
        foreach (var tab in workspace.Tabs)
        {
            var entry = new WorkspaceEditorEntry { Title = tab.Title, Url = tab.Url, FaviconUrl = tab.FaviconUrl };
            Entries.Add(entry);
            // Saved earlier without an icon (pasted link): try again, it is kept on Update.
            if (string.IsNullOrEmpty(entry.FaviconUrl))
                StartIconFetch(entry);
        }
    }

    [RelayCommand]
    private void PickColor(WorkspaceColorChoice? choice) => SelectColor(choice?.Hex);

    private void SelectColor(string? hex)
    {
        // A color that is not in the palette (set elsewhere) shows as "no color" but is kept.
        SelectedColor = hex;
        var match = Colors.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase))
                    ?? Colors[0];
        foreach (var color in Colors)
            color.IsSelected = ReferenceEquals(color, match);
    }

    [RelayCommand]
    private void AddAllTabs()
    {
        var tabs = GetCurrentTabs?.Invoke() ?? Array.Empty<WorkspaceTab>();
        var added = 0;
        foreach (var tab in tabs)
        {
            if (Contains(tab.Url))
                continue;
            Entries.Add(new WorkspaceEditorEntry { Title = tab.Title, Url = tab.Url, FaviconUrl = tab.FaviconUrl });
            added++;
        }
        StatusText = tabs.Count == 0
            ? "This window has no web pages to add."
            : added == 0 ? "Those tabs are already in the list." : $"Added {added} tab(s).";
    }

    [RelayCommand]
    private void AddUrl()
    {
        // One link at a time: reuse the row that is still being typed.
        var open = Entries.FirstOrDefault(e => e.IsEditing);
        if (open != null)
            return;
        Entries.Add(new WorkspaceEditorEntry { IsEditing = true });
        StatusText = "";
    }

    [RelayCommand]
    private void ConfirmEntry(WorkspaceEditorEntry? entry)
    {
        if (entry == null || !entry.IsEditing)
            return;
        var url = NormalizeUrl(entry.EditText);
        if (url == null)
        {
            StatusText = "Enter a web address, for example voz.vn or https://google.com.";
            return;
        }
        if (Contains(url, except: entry))
        {
            StatusText = "That address is already in the list.";
            return;
        }
        entry.Url = url;
        entry.Title = DisplayName(url);
        entry.IsEditing = false;
        StatusText = "";
        // A pasted link was never open in a tab, so its icon has to be loaded from the site.
        StartIconFetch(entry);
    }

    private void StartIconFetch(WorkspaceEditorEntry entry)
    {
        if (_favicons == null || string.IsNullOrEmpty(entry.Url))
            return;
        var task = FetchIconAsync(entry);
        lock (_pendingIcons)
            _pendingIcons.Add(task);
    }

    private async Task FetchIconAsync(WorkspaceEditorEntry entry)
    {
        try
        {
            var icon = await _favicons!.FetchDataUrlAsync(entry.Url);
            if (!string.IsNullOrEmpty(icon) && string.IsNullOrEmpty(entry.FaviconUrl))
                entry.FaviconUrl = icon;
        }
        catch (Exception)
        {
            // No icon: the globe placeholder is shown.
        }
    }

    /// <summary>Lets icon downloads that are still running finish before saving (bounded).</summary>
    private async Task WaitForIconsAsync()
    {
        Task[] pending;
        lock (_pendingIcons)
        {
            pending = _pendingIcons.Where(t => !t.IsCompleted).ToArray();
            _pendingIcons.Clear();
        }
        if (pending.Length > 0)
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [RelayCommand]
    private void RemoveEntry(WorkspaceEditorEntry? entry)
    {
        if (entry != null)
            Entries.Remove(entry);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        // A typed but unconfirmed link counts, so Save works without pressing ✓ first.
        foreach (var editing in Entries.Where(e => e.IsEditing).ToList())
        {
            if (string.IsNullOrWhiteSpace(editing.EditText))
                Entries.Remove(editing);
            else
                ConfirmEntry(editing);
        }
        if (Entries.Any(e => e.IsEditing))
            return;
        if (Entries.Count == 0)
        {
            StatusText = "Add at least one tab or link.";
            return;
        }

        if (_favicons != null)
        {
            StatusText = "Saving…";
            await WaitForIconsAsync();
            StatusText = "";
        }
        var tabs = Entries.Select(e => new WorkspaceTab { Title = e.Title, Url = e.Url, FaviconUrl = e.FaviconUrl }).ToList();
        Workspace? workspace;
        if (EditingId is int id)
        {
            var repository = _repository();
            if (!await repository.RenameAsync(id, Name) || !await repository.ReplaceTabsAsync(id, tabs)
                || !await repository.SetColorAsync(id, SelectedColor))
            {
                StatusText = "This workspace no longer exists.";
                return;
            }
            workspace = await repository.GetByIdAsync(id);
        }
        else
            workspace = await _repository().AddAsync(Name, tabs, SelectedColor);
        if (workspace != null)
            Saved?.Invoke(workspace);
        Reset();
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    private bool Contains(string url, WorkspaceEditorEntry? except = null)
        => Entries.Any(e => !ReferenceEquals(e, except) && !e.IsEditing
                            && string.Equals(e.Url.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>"voz.vn" → "https://voz.vn"; null when it is not a web address.</summary>
    public static string? NormalizeUrl(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0 || raw.Contains(' '))
            return null;
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || (!uri.Host.Contains('.') && !uri.IsLoopback))
            return null;
        return uri.ToString();
    }

    /// <summary>What a pasted link is listed as: its site, e.g. "Google.vn".</summary>
    public static string DisplayName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host.Length == 0 ? url : char.ToUpperInvariant(host[0]) + host[1..];
    }
}
