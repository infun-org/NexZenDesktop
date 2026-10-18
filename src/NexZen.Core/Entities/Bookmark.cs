using System;

namespace NexZen.Core.Entities;

public class Bookmark
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? FaviconUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
