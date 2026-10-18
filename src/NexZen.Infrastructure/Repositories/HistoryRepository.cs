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

public class HistoryRepository : IHistoryRepository
{
    private readonly NexZenDbContext _context;

    public HistoryRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<HistoryItem>> GetAllAsync()
    {
        return await _context.History.OrderByDescending(h => h.VisitedAt).ToListAsync();
    }

    public async Task AddAsync(HistoryItem historyItem)
    {
        _context.History.Add(historyItem);
        await _context.SaveChangesAsync();
    }

    public async Task UpdateAsync(HistoryItem historyItem)
    {
        _context.History.Update(historyItem);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var item = await _context.History.FindAsync(id);
        if (item == null)
            return;

        _context.History.Remove(item);
        await _context.SaveChangesAsync();
    }

    public async Task ClearAsync()
    {
        _context.History.RemoveRange(_context.History);
        await _context.SaveChangesAsync();
    }

    public async Task<int> DeleteMatchingAsync(DateTime? sinceUtc, string? host)
    {
        var rows = await _context.History.ToListAsync();
        var matched = rows.Where(item =>
            DataMatch.InRange(item.VisitedAt, storedIsUtc: true, sinceUtc)
            && DataMatch.UrlMatchesHost(item.Url, host)).ToList();
        if (matched.Count == 0)
            return 0;

        _context.History.RemoveRange(matched);
        await _context.SaveChangesAsync();
        return matched.Count;
    }
}
