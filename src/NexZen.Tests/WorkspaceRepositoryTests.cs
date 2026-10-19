using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Infrastructure.Data;
using NexZen.Infrastructure.Repositories;
using Xunit;

namespace NexZen.Tests;

public class WorkspaceRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public WorkspaceRepositoryTests()
    {
        _connection.Open();
        // An existing nexzen.db: other tables already there, workspace tables added by WorkspaceSchema.
        using var setup = NewContext();
        setup.Database.ExecuteSqlRaw("""CREATE TABLE "Bookmarks" ("Id" INTEGER PRIMARY KEY AUTOINCREMENT, "Title" TEXT NOT NULL, "Url" TEXT NOT NULL, "FaviconUrl" TEXT NULL, "CreatedAt" TEXT NOT NULL);""");
        WorkspaceSchema.Ensure(setup);
        WorkspaceSchema.Ensure(setup); // safe to run on every start
    }

    public void Dispose() => _connection.Dispose();

    private NexZenDbContext NewContext()
        => new(new DbContextOptionsBuilder<NexZenDbContext>().UseSqlite(_connection).Options);

    private static WorkspaceTab Tab(string title, string url, string? icon = null)
        => new() { Title = title, Url = url, FaviconUrl = icon };

    [Fact]
    public async Task Adds_workspaces_in_order_with_their_tabs()
    {
        using (var ctx = NewContext())
        {
            var repo = new WorkspaceRepository(ctx);
            await repo.AddAsync("Xe điện", [Tab("Google", "https://google.com"), Tab("Voz - xe điện Tesla", "https://voz.vn/t/1")]);
            await repo.AddAsync("  ");
        }

        using var read = NewContext();
        var all = await new WorkspaceRepository(read).GetAllAsync();
        Assert.Equal(new[] { "Xe điện", "Workspace" }, all.Select(w => w.Name));
        Assert.Equal(new[] { 0, 1 }, all.Select(w => w.SortOrder));
        Assert.Equal(new[] { "Google", "Voz - xe điện Tesla" }, all[0].Tabs.Select(t => t.Title));
        Assert.Equal(new[] { 0, 1 }, all[0].Tabs.Select(t => t.SortOrder));
        Assert.Empty(all[1].Tabs);
    }

    [Fact]
    public async Task Colors_are_saved_cleaned_and_cleared()
    {
        int id;
        using (var ctx = NewContext())
        {
            var repo = new WorkspaceRepository(ctx);
            id = (await repo.AddAsync("A", null, "#1e88e5")).Id;
            Assert.Null((await repo.AddAsync("B", null, "blue")).Color);
        }
        using (var ctx = NewContext())
            Assert.Equal("#1E88E5", (await new WorkspaceRepository(ctx).GetByIdAsync(id))!.Color);
        using (var ctx = NewContext())
            Assert.True(await new WorkspaceRepository(ctx).SetColorAsync(id, null));
        using (var read = NewContext())
            Assert.Null((await new WorkspaceRepository(read).GetByIdAsync(id))!.Color);
    }

    [Fact]
    public void Ensure_adds_the_color_column_to_an_older_table()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var ctx = new NexZenDbContext(new DbContextOptionsBuilder<NexZenDbContext>().UseSqlite(connection).Options);
        ctx.Database.ExecuteSqlRaw("""CREATE TABLE "Workspaces" ("Id" INTEGER PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "SortOrder" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);""");
        ctx.Database.ExecuteSqlRaw("""INSERT INTO "Workspaces" ("Name","SortOrder","CreatedAt","UpdatedAt") VALUES ('Old',0,'2026-10-19','2026-10-19');""");

        WorkspaceSchema.Ensure(ctx);
        WorkspaceSchema.Ensure(ctx);

        var old = ctx.Workspaces.AsNoTracking().Single();
        Assert.Equal("Old", old.Name);
        Assert.Null(old.Color);
    }

    [Fact]
    public async Task Replace_add_and_remove_tabs()
    {
        int id;
        using (var ctx = NewContext())
            id = (await new WorkspaceRepository(ctx).AddAsync("A", [Tab("Old", "https://old.example")])).Id;

        using (var ctx = NewContext())
        {
            var repo = new WorkspaceRepository(ctx);
            Assert.True(await repo.ReplaceTabsAsync(id, [Tab("Pinterest", "https://pinterest.com"), Tab("Facebook", "https://facebook.com")]));
            var added = await repo.AddTabAsync(id, Tab("Google", "https://google.com", "https://google.com/favicon.ico"));
            Assert.NotNull(added);
            Assert.Equal(2, added!.SortOrder);
            var pinterest = (await repo.GetByIdAsync(id))!.Tabs[0];
            Assert.True(await repo.RemoveTabAsync(pinterest.Id));
        }

        using var read = NewContext();
        var workspace = await new WorkspaceRepository(read).GetByIdAsync(id);
        Assert.Equal(new[] { "Facebook", "Google" }, workspace!.Tabs.Select(t => t.Title));
        Assert.Equal("https://google.com/favicon.ico", workspace.Tabs[1].FaviconUrl);
        Assert.Equal(2, await read.WorkspaceTabs.CountAsync());
    }

    [Fact]
    public async Task Rename_reorder_and_delete_with_tabs()
    {
        int a, b, c;
        using (var ctx = NewContext())
        {
            var repo = new WorkspaceRepository(ctx);
            a = (await repo.AddAsync("A", [Tab("x", "https://x.example")])).Id;
            b = (await repo.AddAsync("B")).Id;
            c = (await repo.AddAsync("C")).Id;
        }

        using (var ctx = NewContext())
        {
            var repo = new WorkspaceRepository(ctx);
            Assert.True(await repo.RenameAsync(b, "Công việc"));
            await repo.ReorderAsync([c, a]);
            Assert.True(await repo.DeleteAsync(a));
            Assert.False(await repo.DeleteAsync(a));
            Assert.False(await repo.RenameAsync(999, "x"));
        }

        using var read = NewContext();
        var all = await new WorkspaceRepository(read).GetAllAsync();
        Assert.Equal(new[] { "C", "Công việc" }, all.Select(w => w.Name));
        Assert.Equal(0, await read.WorkspaceTabs.CountAsync());
    }
}
