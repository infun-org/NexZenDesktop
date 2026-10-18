using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public class BookmarkRepository : IBookmarkRepository
{
    private readonly NexZenDbContext _context;

    public BookmarkRepository(NexZenDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<Bookmark>> GetAllAsync()
    {
        return await _context.Bookmarks.ToListAsync();
    }

    public async Task<Bookmark?> GetByIdAsync(int id)
    {
        return await _context.Bookmarks.FindAsync(id);
    }

    public async Task AddAsync(Bookmark bookmark)
    {
        _context.Bookmarks.Add(bookmark);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var bookmark = await _context.Bookmarks.FindAsync(id);
        if (bookmark != null)
        {
            _context.Bookmarks.Remove(bookmark);
            await _context.SaveChangesAsync();
        }
    }
}
