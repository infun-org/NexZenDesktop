using System.Collections.Generic;
using System.Threading.Tasks;
using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface IWorkspaceRepository
{
    /// <summary>All workspaces by SortOrder, each with its tabs by SortOrder.</summary>
    Task<IReadOnlyList<Workspace>> GetAllAsync();
    Task<Workspace?> GetByIdAsync(int id);

    /// <summary>Adds a workspace (and any tabs on it) at the end of the list.</summary>
    Task<Workspace> AddAsync(string name, IEnumerable<WorkspaceTab>? tabs = null, string? color = null);
    Task<bool> RenameAsync(int id, string name);
    /// <summary>Sets the border color (#RRGGBB), or clears it with null.</summary>
    Task<bool> SetColorAsync(int id, string? color);
    Task<bool> DeleteAsync(int id);

    /// <summary>Replaces the saved tabs of a workspace, in the given order.</summary>
    Task<bool> ReplaceTabsAsync(int id, IEnumerable<WorkspaceTab> tabs);
    Task<WorkspaceTab?> AddTabAsync(int workspaceId, WorkspaceTab tab);
    Task<bool> RemoveTabAsync(int tabId);
    /// <summary>Stores an icon found later for a saved tab (pasted links have none at first).</summary>
    Task<bool> SetTabFaviconAsync(int tabId, string faviconUrl);

    /// <summary>Sets the card order: the first id gets SortOrder 0, and so on.</summary>
    Task ReorderAsync(IReadOnlyList<int> workspaceIdsInOrder);
}
