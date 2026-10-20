using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface ISitePermissionRepository
{
    Task<IReadOnlyList<SitePermissionSetting>> GetAllAsync();
    /// <summary>Stores the value; null or empty removes the setting (back to the default).</summary>
    Task SetAsync(string site, string permission, string? value);
    /// <summary>Removes every setting of the site.</summary>
    Task ResetAsync(string site);
}
