using System;

namespace NexZen.Core.Entities;

/// <summary>A user stylesheet applied to every page of one site (domain without "www.").</summary>
public class CustomStyle
{
    public int Id { get; set; }
    public string Site { get; set; } = string.Empty;
    public string Css { get; set; } = string.Empty;
    /// <summary>Off keeps the CSS but does not apply it.</summary>
    public bool Enabled { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
