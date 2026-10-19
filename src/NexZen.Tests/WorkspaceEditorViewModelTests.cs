using NexZen.Application.ViewModels;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using Xunit;

namespace NexZen.Tests;

public class WorkspaceEditorViewModelTests
{
    [Theory]
    [InlineData("voz.vn", "https://voz.vn/")]
    [InlineData("  https://www.google.vn/search?q=x ", "https://www.google.vn/search?q=x")]
    [InlineData("http://localhost:8080/a", "http://localhost:8080/a")]
    [InlineData("pinterest", null)]
    [InlineData("two words.com", null)]
    [InlineData("ftp://files.example.com", null)]
    [InlineData("", null)]
    public void NormalizeUrl_accepts_pasted_web_addresses(string text, string? expected)
        => Assert.Equal(expected, WorkspaceEditorViewModel.NormalizeUrl(text));

    [Fact]
    public void DisplayName_is_the_site()
    {
        Assert.Equal("Google.vn", WorkspaceEditorViewModel.DisplayName("https://www.google.vn/"));
        Assert.Equal("Voz.vn", WorkspaceEditorViewModel.DisplayName("https://voz.vn/t/1"));
    }

    [Fact]
    public async Task Typed_links_and_window_tabs_are_saved_as_a_workspace()
    {
        var repo = new CapturingRepo();
        Workspace? saved = null;
        var closed = false;
        var vm = new WorkspaceEditorViewModel(() => repo)
        {
            GetCurrentTabs = () => new[]
            {
                new WorkspaceTab { Title = "Google", Url = "https://google.vn/", FaviconUrl = "data:image/png;base64,AQID" },
                new WorkspaceTab { Title = "Voz", Url = "https://voz.vn/" }
            },
            Saved = w => saved = w,
            CloseRequested = () => closed = true
        };
        vm.Name = "Xe điện";

        vm.AddAllTabsCommand.Execute(null);
        vm.AddAllTabsCommand.Execute(null);
        Assert.Equal(2, vm.Entries.Count);
        Assert.Contains("already", vm.StatusText);

        vm.AddUrlCommand.Execute(null);
        vm.AddUrlCommand.Execute(null);
        var typing = Assert.Single(vm.Entries, e => e.IsEditing);
        typing.EditText = "voz.vn";
        vm.ConfirmEntryCommand.Execute(typing);
        Assert.True(typing.IsEditing);
        Assert.Contains("already", vm.StatusText);

        typing.EditText = "pinterest";
        vm.ConfirmEntryCommand.Execute(typing);
        Assert.True(typing.IsEditing);

        typing.EditText = "pinterest.com";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.NotNull(saved);
        Assert.Equal("Xe điện", repo.Name);
        Assert.Equal(new[] { "Google", "Voz", "Pinterest.com" }, repo.Tabs.Select(t => t.Title));
        Assert.Equal("https://pinterest.com/", repo.Tabs[2].Url);
        Assert.Equal("data:image/png;base64,AQID", repo.Tabs[0].FaviconUrl);
        Assert.Empty(vm.Entries);
        Assert.Equal("", vm.Name);
    }

    [Fact]
    public async Task Save_needs_at_least_one_entry_and_drops_empty_typing_rows()
    {
        var repo = new CapturingRepo();
        var vm = new WorkspaceEditorViewModel(() => repo);
        vm.AddUrlCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(vm.Entries);
        Assert.Null(repo.Name);
        Assert.Contains("at least one", vm.StatusText);

        vm.AddUrlCommand.Execute(null);
        vm.RemoveEntryCommand.Execute(vm.Entries[0]);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task Update_loads_a_workspace_and_saves_name_and_tabs_back()
    {
        var repo = new CapturingRepo();
        var vm = new WorkspaceEditorViewModel(() => repo);
        vm.BeginEdit(new Workspace
        {
            Id = 7,
            Name = "Xe điện",
            Tabs = { new WorkspaceTab { Title = "Google", Url = "https://google.vn/" }, new WorkspaceTab { Title = "Voz", Url = "https://voz.vn/" } }
        });
        Assert.Equal(7, vm.EditingId);
        Assert.Equal("Update workspace", vm.SaveLabel);
        Assert.Equal(new[] { "Google", "Voz" }, vm.Entries.Select(e => e.Title));

        vm.Name = "Xe điện 2026";
        vm.RemoveEntryCommand.Execute(vm.Entries[0]);
        vm.AddUrlCommand.Execute(null);
        vm.Entries[^1].EditText = "tinhte.vn";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal((7, "Xe điện 2026"), (repo.RenamedId, repo.Name));
        Assert.Equal(new[] { "Voz", "Tinhte.vn" }, repo.Tabs.Select(t => t.Title));
        Assert.Null(vm.EditingId);
        Assert.Equal("Save workspace", vm.SaveLabel);
    }

    [Fact]
    public async Task Pasted_links_get_the_site_icon_before_saving()
    {
        var repo = new CapturingRepo();
        var icons = new FakeIcons { ["https://pinterest.com/"] = "data:image/png;base64,UElO" };
        var vm = new WorkspaceEditorViewModel(() => repo, icons);
        vm.AddUrlCommand.Execute(null);
        vm.Entries[0].EditText = "pinterest.com";
        vm.ConfirmEntryCommand.Execute(vm.Entries[0]);
        vm.AddUrlCommand.Execute(null);
        vm.Entries[1].EditText = "no-icon.example";
        vm.ConfirmEntryCommand.Execute(vm.Entries[1]);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("data:image/png;base64,UElO", repo.Tabs[0].FaviconUrl);
        Assert.Null(repo.Tabs[1].FaviconUrl);
    }

    [Fact]
    public async Task Update_fetches_icons_for_saved_links_without_one()
    {
        var repo = new CapturingRepo();
        var icons = new FakeIcons { ["https://voz.vn/"] = "data:image/png;base64,Vk9a" };
        var vm = new WorkspaceEditorViewModel(() => repo, icons);
        vm.BeginEdit(new Workspace { Id = 3, Name = "A", Tabs = { new WorkspaceTab { Title = "Voz.vn", Url = "https://voz.vn/" } } });
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("data:image/png;base64,Vk9a", repo.Tabs[0].FaviconUrl);
    }

    [Fact]
    public async Task Color_is_picked_saved_and_loaded_for_update()
    {
        var repo = new CapturingRepo();
        var vm = new WorkspaceEditorViewModel(() => repo);
        Assert.True(vm.Colors[0].IsSelected);
        Assert.Null(vm.Colors[0].Hex);

        var blue = vm.Colors.Single(c => c.Name == "Blue");
        vm.PickColorCommand.Execute(blue);
        Assert.True(blue.IsSelected);
        Assert.Single(vm.Colors, c => c.IsSelected);

        vm.AddUrlCommand.Execute(null);
        vm.Entries[0].EditText = "voz.vn";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("#1E88E5", repo.Color);
        Assert.True(vm.Colors[0].IsSelected); // reset after saving

        vm.BeginEdit(new Workspace { Id = 2, Name = "B", Color = "#43A047", Tabs = { new WorkspaceTab { Title = "x", Url = "https://x.example/" } } });
        Assert.True(vm.Colors.Single(c => c.Name == "Green").IsSelected);
        vm.PickColorCommand.Execute(vm.Colors[0]);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(repo.Color);
    }

    internal sealed class FakeIcons : Dictionary<string, string>, IFaviconFetcher
    {
        public async Task<string?> FetchDataUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            return TryGetValue(pageUrl, out var icon) ? icon : null;
        }
    }

    private sealed class CapturingRepo : IWorkspaceRepository
    {
        public string? Name;
        public string? Color;
        public int? RenamedId;
        public List<WorkspaceTab> Tabs = new();

        public Task<Workspace> AddAsync(string name, IEnumerable<WorkspaceTab>? tabs = null, string? color = null)
        {
            Name = name;
            Color = color;
            Tabs = tabs?.ToList() ?? new();
            return Task.FromResult(new Workspace { Id = 1, Name = name, Tabs = Tabs });
        }

        public Task<bool> RenameAsync(int id, string name)
        {
            (RenamedId, Name) = (id, name);
            return Task.FromResult(true);
        }

        public Task<bool> ReplaceTabsAsync(int id, IEnumerable<WorkspaceTab> tabs)
        {
            Tabs = tabs.ToList();
            return Task.FromResult(true);
        }

        public Task<Workspace?> GetByIdAsync(int id)
            => Task.FromResult<Workspace?>(new Workspace { Id = id, Name = Name ?? "", Tabs = Tabs });

        public Task<IReadOnlyList<Workspace>> GetAllAsync() => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id) => throw new NotSupportedException();

        public Task<bool> SetColorAsync(int id, string? color)
        {
            Color = color;
            return Task.FromResult(true);
        }
        public Task<bool> SetTabFaviconAsync(int tabId, string faviconUrl) => throw new NotSupportedException();
        public Task<WorkspaceTab?> AddTabAsync(int workspaceId, WorkspaceTab tab) => throw new NotSupportedException();
        public Task<bool> RemoveTabAsync(int tabId) => throw new NotSupportedException();
        public Task ReorderAsync(IReadOnlyList<int> workspaceIdsInOrder) => throw new NotSupportedException();
    }
}
