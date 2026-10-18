using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexZen.Core;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public class DownloadRepository : IDownloadRepository
{
    private readonly NexZenDbContext _context;

    public DownloadRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<DownloadRecord>> GetAllAsync()
    {
        return await _context.DownloadRecords.ToListAsync();
    }

    public async Task<DownloadRecord?> GetByIdAsync(string id)
    {
        return await _context.DownloadRecords.FindAsync(id);
    }

    public async Task AddAsync(DownloadRecord record)
    {
        _context.DownloadRecords.Add(record);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(DownloadRecord record)
    {
        _context.DownloadRecords.Update(record);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(string id)
    {
        var record = await _context.DownloadRecords.FindAsync(id);
        if (record != null)
        {
            _context.DownloadRecords.Remove(record);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<int> DeleteMatchingAsync(DateTime? sinceUtc, string? host, IReadOnlyCollection<string>? keepIds)
    {
        var keep = keepIds == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(keepIds, StringComparer.Ordinal);
        var rows = await _context.DownloadRecords.ToListAsync();
        var matched = rows.Where(item =>
            !keep.Contains(item.Id)
            && DataMatch.InRange(item.Timestamp, storedIsUtc: false, sinceUtc)
            && DataMatch.UrlMatchesHost(item.Url, host)).ToList();
        if (matched.Count == 0)
            return 0;

        _context.DownloadRecords.RemoveRange(matched);
        await _context.SaveChangesAsync();
        return matched.Count;
    }
}
