using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public class SitePermissionRepository : ISitePermissionRepository
{
    private readonly NexZenDbContext _context;

    public SitePermissionRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<SitePermissionSetting>> GetAllAsync()
        => await _context.SitePermissions.AsNoTracking().OrderBy(s => s.Site).ThenBy(s => s.Permission).ToListAsync();

    public async Task SetAsync(string site, string permission, string? value)
    {
        var siteKey = (site ?? "").Trim().ToLowerInvariant();
        var permissionKey = (permission ?? "").Trim().ToLowerInvariant();
        if (siteKey.Length == 0 || permissionKey.Length == 0)
            return;
        var existing = await _context.SitePermissions
            .FirstOrDefaultAsync(s => s.Site == siteKey && s.Permission == permissionKey);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (existing != null)
            {
                _context.SitePermissions.Remove(existing);
                await _context.SaveChangesAsync();
            }
            return;
        }
        if (existing == null)
        {
            existing = new SitePermissionSetting { Site = siteKey, Permission = permissionKey };
            _context.SitePermissions.Add(existing);
        }
        existing.Value = value.Trim();
        existing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task ResetAsync(string site)
    {
        var siteKey = (site ?? "").Trim().ToLowerInvariant();
        var rows = await _context.SitePermissions.Where(s => s.Site == siteKey).ToListAsync();
        if (rows.Count == 0)
            return;
        _context.SitePermissions.RemoveRange(rows);
        await _context.SaveChangesAsync();
    }
}
