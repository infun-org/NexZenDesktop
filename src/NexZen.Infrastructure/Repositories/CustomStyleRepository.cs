using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public class CustomStyleRepository : ICustomStyleRepository
{
    private readonly NexZenDbContext _context;

    public CustomStyleRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<CustomStyle>> GetAllAsync()
        => await _context.CustomStyles.AsNoTracking().OrderBy(s => s.Site).ToListAsync();

    public async Task<CustomStyle?> GetAsync(string site)
    {
        var key = (site ?? "").Trim().ToLowerInvariant();
        return await _context.CustomStyles.AsNoTracking().FirstOrDefaultAsync(s => s.Site == key);
    }

    public async Task SaveAsync(string site, string css, bool enabled)
    {
        var key = (site ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0)
            return;
        var existing = await _context.CustomStyles.FirstOrDefaultAsync(s => s.Site == key);
        if (string.IsNullOrWhiteSpace(css))
        {
            if (existing != null)
            {
                _context.CustomStyles.Remove(existing);
                await _context.SaveChangesAsync();
            }
            return;
        }
        if (existing == null)
        {
            existing = new CustomStyle { Site = key };
            _context.CustomStyles.Add(existing);
        }
        existing.Css = css;
        existing.Enabled = enabled;
        existing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }
}
