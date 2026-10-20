namespace NexZen.Core.Entities;

/// <summary>A per-site permission chosen in Site privacy (only choices other than the default are stored).</summary>
public class SitePermissionSetting
{
    public int Id { get; set; }
    /// <summary>Registrable domain, e.g. "google.com".</summary>
    public string Site { get; set; } = "";
    /// <summary>Permission key, e.g. "javascript", "camera".</summary>
    public string Permission { get; set; } = "";
    /// <summary>"Allow" or "Block".</summary>
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
