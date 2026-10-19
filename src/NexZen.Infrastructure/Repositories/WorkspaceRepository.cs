using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public class WorkspaceRepository : IWorkspaceRepository
{
    private readonly NexZenDbContext _context;

    public WorkspaceRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Workspace>> GetAllAsync()
    {
        var workspaces = await _context.Workspaces
            .AsNoTracking()
            .Include(w => w.Tabs)
            .OrderBy(w => w.SortOrder)
            .ThenBy(w => w.Id)
            .ToListAsync();
        foreach (var workspace in workspaces)
            workspace.Tabs = OrderTabs(workspace.Tabs);
        return workspaces;
    }

    public async Task<Workspace?> GetByIdAsync(int id)
    {
        var workspace = await _context.Workspaces
            .AsNoTracking()
            .Include(w => w.Tabs)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (workspace != null)
            workspace.Tabs = OrderTabs(workspace.Tabs);
        return workspace;
    }

    public async Task<Workspace> AddAsync(string name, IEnumerable<WorkspaceTab>? tabs = null, string? color = null)
    {
        var now = DateTime.UtcNow;
        var last = await _context.Workspaces.MaxAsync(w => (int?)w.SortOrder) ?? -1;
        var workspace = new Workspace
        {
            Name = CleanName(name),
            Color = CleanColor(color),
            SortOrder = last + 1,
            CreatedAt = now,
            UpdatedAt = now,
            Tabs = NumberTabs(tabs, now)
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();
        return workspace;
    }

    public async Task<bool> RenameAsync(int id, string name)
    {
        var workspace = await _context.Workspaces.FindAsync(id);
        if (workspace == null)
            return false;
        workspace.Name = CleanName(name);
        workspace.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetColorAsync(int id, string? color)
    {
        var workspace = await _context.Workspaces.FindAsync(id);
        if (workspace == null)
            return false;
        workspace.Color = CleanColor(color);
        workspace.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var workspace = await _context.Workspaces.Include(w => w.Tabs).FirstOrDefaultAsync(w => w.Id == id);
        if (workspace == null)
            return false;
        _context.Workspaces.Remove(workspace);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReplaceTabsAsync(int id, IEnumerable<WorkspaceTab> tabs)
    {
        var workspace = await _context.Workspaces.Include(w => w.Tabs).FirstOrDefaultAsync(w => w.Id == id);
        if (workspace == null)
            return false;
        var now = DateTime.UtcNow;
        _context.WorkspaceTabs.RemoveRange(workspace.Tabs);
        workspace.Tabs = NumberTabs(tabs, now);
        workspace.UpdatedAt = now;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<WorkspaceTab?> AddTabAsync(int workspaceId, WorkspaceTab tab)
    {
        var workspace = await _context.Workspaces.FindAsync(workspaceId);
        if (workspace == null)
            return null;
        var last = await _context.WorkspaceTabs
            .Where(t => t.WorkspaceId == workspaceId)
            .MaxAsync(t => (int?)t.SortOrder) ?? -1;
        var now = DateTime.UtcNow;
        var saved = Copy(tab, last + 1, now);
        saved.WorkspaceId = workspaceId;
        _context.WorkspaceTabs.Add(saved);
        workspace.UpdatedAt = now;
        await _context.SaveChangesAsync();
        return saved;
    }

    public async Task<bool> RemoveTabAsync(int tabId)
    {
        var tab = await _context.WorkspaceTabs.FindAsync(tabId);
        if (tab == null)
            return false;
        _context.WorkspaceTabs.Remove(tab);
        var workspace = await _context.Workspaces.FindAsync(tab.WorkspaceId);
        if (workspace != null)
            workspace.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetTabFaviconAsync(int tabId, string faviconUrl)
    {
        var tab = await _context.WorkspaceTabs.FindAsync(tabId);
        if (tab == null)
            return false;
        tab.FaviconUrl = faviconUrl;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task ReorderAsync(IReadOnlyList<int> workspaceIdsInOrder)
    {
        var workspaces = await _context.Workspaces.ToListAsync();
        var position = workspaceIdsInOrder
            .Select((id, index) => (id, index))
            .GroupBy(p => p.id)
            .ToDictionary(g => g.Key, g => g.First().index);
        // Ids missing from the list keep their relative order after the listed ones.
        var next = workspaceIdsInOrder.Count;
        foreach (var workspace in workspaces.OrderBy(w => w.SortOrder).ThenBy(w => w.Id))
            workspace.SortOrder = position.TryGetValue(workspace.Id, out var index) ? index : next++;
        await _context.SaveChangesAsync();
    }

    /// <summary>#RRGGBB in upper case, or null for anything else.</summary>
    private static string? CleanColor(string? color)
    {
        var value = (color ?? "").Trim();
        if (value.Length != 7 || value[0] != '#' || !value[1..].All(Uri.IsHexDigit))
            return null;
        return value.ToUpperInvariant();
    }

    private static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        return trimmed.Length == 0 ? "Workspace" : trimmed;
    }

    private static List<WorkspaceTab> OrderTabs(IEnumerable<WorkspaceTab> tabs)
        => tabs.OrderBy(t => t.SortOrder).ThenBy(t => t.Id).ToList();

    private static List<WorkspaceTab> NumberTabs(IEnumerable<WorkspaceTab>? tabs, DateTime now)
        => (tabs ?? Enumerable.Empty<WorkspaceTab>()).Select((t, i) => Copy(t, i, now)).ToList();

    private static WorkspaceTab Copy(WorkspaceTab tab, int order, DateTime now) => new()
    {
        Title = tab.Title ?? "",
        Url = tab.Url ?? "",
        FaviconUrl = tab.FaviconUrl,
        SortOrder = order,
        CreatedAt = now
    };
}
