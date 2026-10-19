using Microsoft.Data.Sqlite;
using NexZen.Application.ViewModels;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;
using Xunit;

namespace NexZen.Tests;

public class WorkspaceViewModelTests
{
    // Workspace names passed with each opened window, in order.
    private static readonly List<string> names = new();
    private static readonly List<string?> focused = new();
    private static readonly List<int> ids = new();

    private static (WorkspaceViewModel Vm, MemoryRepo Repo, List<IReadOnlyList<string>> Opened) Setup(
        params Workspace[] saved)
    {
        var repo = new MemoryRepo(saved);
        var opened = new List<IReadOnlyList<string>>();
        names.Clear();
        focused.Clear();
        ids.Clear();
        var vm = new WorkspaceViewModel(() => repo) { OpenWorkspace = r => { names.Add(r.Name); opened.Add(r.Urls); focused.Add(r.FocusUrl); ids.Add(r.WorkspaceId); } };
        return (vm, repo, opened);
    }

    private static Workspace Saved(int id, string name, params string[] urls) => new()
    {
        Id = id,
        Name = name,
        Tabs = urls.Select((u, i) => new WorkspaceTab { Id = id * 100 + i, WorkspaceId = id, Title = u, Url = u, SortOrder = i }).ToList()
    };

    [Fact]
    public async Task Load_shows_add_tile_then_cards_and_open_starts_a_window()
    {
        var (vm, _, opened) = Setup(Saved(1, "Xe điện", "https://google.com", "https://voz.vn"), Saved(2, "Trống"));
        await vm.LoadAsync();

        Assert.IsType<AddWorkspaceTile>(vm.Tiles[0]);
        Assert.Equal(new[] { "Xe điện", "Trống" }, vm.Tiles.Skip(1).Cast<WorkspaceCardViewModel>().Select(c => c.Name));

        vm.OpenCommand.Execute(vm.Workspaces[0]);
        Assert.Equal(new[] { "https://google.com", "https://voz.vn" }, opened.Single());

        vm.OpenCommand.Execute(vm.Workspaces[1]);
        Assert.Single(opened);
        Assert.Contains("no tabs", vm.StatusText);

        // A tab row opens (or brings back) the whole workspace, on that tab.
        vm.OpenTabCommand.Execute(vm.Workspaces[0].Tabs[1]);
        Assert.Equal(new[] { "https://google.com", "https://voz.vn" }, opened[1]);
        Assert.Equal(new string?[] { null, "https://voz.vn" }, focused);
        Assert.Equal(new[] { 1, 1 }, ids);
        Assert.Equal(new[] { "Xe điện", "Xe điện" }, names);
    }

    [Fact]
    public async Task Add_this_tab_uses_the_source_window_and_update_opens_the_editor()
    {
        var (vm, _, _) = Setup(Saved(1, "A", "https://old.example"));
        int? editorFor = -1;
        vm.OpenEditor = id => editorFor = id;
        vm.GetActiveTab = () => new WorkspaceTab { Title = "Pinterest", Url = "https://pinterest.com" };
        await vm.LoadAsync();
        var card = vm.Workspaces[0];

        await vm.AddThisTabCommand.ExecuteAsync(card);
        Assert.Equal(new[] { "https://old.example", "Pinterest" }, card.Tabs.Select(t => t.Title));

        vm.GetActiveTab = () => null;
        await vm.AddThisTabCommand.ExecuteAsync(card);
        Assert.Equal(2, card.Tabs.Count);
        Assert.Contains("Open a web page", vm.StatusText);

        vm.UpdateCommand.Execute(card);
        Assert.Equal(1, editorFor);
        vm.AddNewCommand.Execute(null);
        Assert.Null(editorFor);
    }

    [Fact]
    public async Task Delete_asks_for_confirmation()
    {
        var (vm, repo, _) = Setup(Saved(1, "A"), Saved(2, "B"));
        await vm.LoadAsync();
        var card = vm.Workspaces[0];

        vm.AskDeleteCommand.Execute(card);
        Assert.True(vm.IsConfirmOpen);
        vm.CancelDeleteCommand.Execute(null);
        Assert.Equal(2, vm.Workspaces.Count);

        vm.AskDeleteCommand.Execute(card);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.False(vm.IsConfirmOpen);
        Assert.Equal(new[] { "B" }, vm.Workspaces.Select(w => w.Name));
        Assert.Equal(2, vm.Tiles.Count);
        Assert.Single(repo.Items);
    }

    [Theory]
    [InlineData(null, "Voz", "Voz - NexZen Desktop")]
    [InlineData("ABC", "Voz", "Workspace ABC - Voz - NexZen Desktop")]
    [InlineData("Workspace ABC", "Voz", "Workspace ABC - Voz - NexZen Desktop")]
    [InlineData("ABC", "about:blank", "Workspace ABC - NexZen Desktop")]
    [InlineData(" ", "", "NexZen Desktop")]
    public void Window_title_shows_the_workspace_first(string? workspace, string page, string expected)
        => Assert.Equal(expected, NexZen.Application.Services.WindowTitle.Compose(workspace, page));

    [Fact]
    public async Task Opening_passes_the_workspace_color()
    {
        var saved = Saved(4, "Màu", "https://voz.vn");
        saved.Color = "#E53935";
        var repo = new MemoryRepo(new[] { saved });
        WorkspaceOpenRequest? request = null;
        var vm = new WorkspaceViewModel(() => repo) { OpenWorkspace = r => request = r };
        await vm.LoadAsync();
        Assert.Equal("#E53935", vm.Workspaces[0].Color);
        vm.OpenCommand.Execute(vm.Workspaces[0]);
        Assert.Equal("#E53935", request!.Color);
    }

    [Fact]
    public async Task Saved_links_without_an_icon_get_one_once_per_site()
    {
        var repo = new MemoryRepo(new[] { Saved(1, "A", "https://voz.vn/", "https://voz.vn/t/2", "https://none.example/") });
        var icons = new WorkspaceEditorViewModelTests.FakeIcons { ["https://voz.vn/"] = "data:image/png;base64,Vk9a" };
        var vm = new WorkspaceViewModel(() => repo, icons);
        await vm.LoadAsync();
        await vm.FillMissingIconsAsync();

        Assert.Equal(new[] { "data:image/png;base64,Vk9a", "data:image/png;base64,Vk9a", null }, vm.Workspaces[0].Tabs.Select(t => t.FaviconUrl));
        Assert.Equal("data:image/png;base64,Vk9a", repo.Items[0].Tabs[1].FaviconUrl);
    }

    [Fact]
    public void FromOpenTab_keeps_web_pages_with_their_icon()
    {
        var google = new TabItemViewModel("1", "https://google.com/", "Google") { FaviconPng = new byte[] { 1, 2, 3 } };
        var saved = WorkspaceViewModel.FromOpenTab(google)!;
        Assert.Equal("https://google.com/", saved.Url);
        Assert.Equal("data:image/png;base64,AQID", saved.FaviconUrl);

        Assert.Null(WorkspaceViewModel.FromOpenTab(new TabItemViewModel("2", "about:blank", "New Tab")));
        Assert.Null(WorkspaceViewModel.FromOpenTab(new TabItemViewModel("3", "nexzen://newtab/blank.html", "New Tab")));
        Assert.Null(WorkspaceViewModel.FromOpenTab(new TabItemViewModel("4", "chrome-extension://abc/popup.html", "x")));
        Assert.Equal("voz.vn", WorkspaceViewModel.FromOpenTab(new TabItemViewModel("5", "https://voz.vn/t/1", " "))!.Title);
    }

    [Fact]
    public void DatabaseLocation_copies_the_newest_old_file_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexzen-db-" + Guid.NewGuid().ToString("N"));
        var oldA = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var oldB = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
        var target = Path.Combine(root, "data");
        try
        {
            MakeDb(Path.Combine(oldA, "nexzen.db"), "old");
            MakeDb(Path.Combine(oldB, "nexzen.db"), "new");
            File.SetLastWriteTimeUtc(Path.Combine(oldA, "nexzen.db"), DateTime.UtcNow.AddDays(-1));

            var (path, migrated) = DatabaseLocation.Resolve(target, oldA, oldB, null);
            Assert.Equal(Path.Combine(target, "nexzen.db"), path);
            Assert.Equal(Path.Combine(oldB, "nexzen.db"), migrated);
            Assert.Equal("new", ReadMarker(path));
            Assert.True(File.Exists(Path.Combine(oldA, "nexzen.db")));

            var again = DatabaseLocation.Resolve(target, oldA, oldB);
            Assert.Null(again.Migrated);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void MakeDb(string path, string marker)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE TABLE M (V TEXT); INSERT INTO M VALUES ('{marker}');";
        cmd.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT V FROM M";
        return (string)cmd.ExecuteScalar()!;
    }

    private sealed class MemoryRepo : IWorkspaceRepository
    {
        public List<Workspace> Items { get; }
        private int _nextTab = 10_000;

        public MemoryRepo(IEnumerable<Workspace> items) => Items = items.ToList();

        public Task<IReadOnlyList<Workspace>> GetAllAsync() => Task.FromResult<IReadOnlyList<Workspace>>(Items.ToList());
        public Task<Workspace?> GetByIdAsync(int id) => Task.FromResult(Items.FirstOrDefault(w => w.Id == id));

        public Task<Workspace> AddAsync(string name, IEnumerable<WorkspaceTab>? tabs = null, string? color = null)
        {
            var w = new Workspace { Id = Items.Count + 1, Name = name, Tabs = tabs?.ToList() ?? new() };
            Items.Add(w);
            return Task.FromResult(w);
        }

        public Task<bool> RenameAsync(int id, string name)
        {
            var w = Items.FirstOrDefault(x => x.Id == id);
            if (w != null) w.Name = name;
            return Task.FromResult(w != null);
        }

        public Task<bool> DeleteAsync(int id) => Task.FromResult(Items.RemoveAll(w => w.Id == id) > 0);
        public Task<bool> SetColorAsync(int id, string? color) => Task.FromResult(true);
        public List<WorkspaceOpenRequest> Requests { get; } = new();

        public Task<bool> ReplaceTabsAsync(int id, IEnumerable<WorkspaceTab> tabs)
        {
            var w = Items.FirstOrDefault(x => x.Id == id);
            if (w != null) w.Tabs = tabs.Select(t => new WorkspaceTab { Id = _nextTab++, WorkspaceId = id, Title = t.Title, Url = t.Url }).ToList();
            return Task.FromResult(w != null);
        }

        public Task<WorkspaceTab?> AddTabAsync(int workspaceId, WorkspaceTab tab)
        {
            var w = Items.FirstOrDefault(x => x.Id == workspaceId);
            if (w == null) return Task.FromResult<WorkspaceTab?>(null);
            var saved = new WorkspaceTab { Id = _nextTab++, WorkspaceId = workspaceId, Title = tab.Title, Url = tab.Url, FaviconUrl = tab.FaviconUrl };
            w.Tabs.Add(saved);
            return Task.FromResult<WorkspaceTab?>(saved);
        }

        public Task<bool> RemoveTabAsync(int tabId) => Task.FromResult(Items.Any(w => w.Tabs.RemoveAll(t => t.Id == tabId) > 0));
        public Task ReorderAsync(IReadOnlyList<int> workspaceIdsInOrder) => Task.CompletedTask;

        public Task<bool> SetTabFaviconAsync(int tabId, string faviconUrl)
        {
            var tab = Items.SelectMany(w => w.Tabs).FirstOrDefault(t => t.Id == tabId);
            if (tab != null) tab.FaviconUrl = faviconUrl;
            return Task.FromResult(tab != null);
        }
    }
}
