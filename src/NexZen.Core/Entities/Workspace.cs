using System;
using System.Collections.Generic;

namespace NexZen.Core.Entities;

/// <summary>A named, saved group of tabs shown as a card on the NexZen workspace screen.</summary>
public class Workspace
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Border color (#RRGGBB) marking the card and the windows opened from it; null for none.</summary>
    public string? Color { get; set; }
    /// <summary>Card position on the workspace screen (ascending).</summary>
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<WorkspaceTab> Tabs { get; set; } = new();
}

/// <summary>One saved tab of a workspace: what is needed to list it and reopen it.</summary>
public class WorkspaceTab
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? FaviconUrl { get; set; }
    /// <summary>Order inside the workspace (ascending).</summary>
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
