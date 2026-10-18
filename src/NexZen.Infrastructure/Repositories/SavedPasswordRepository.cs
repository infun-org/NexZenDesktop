using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;

namespace NexZen.Infrastructure.Repositories;

public sealed class SavedPasswordRepository : ISavedPasswordRepository
{
    private readonly NexZenDbContext _context;

    public SavedPasswordRepository(NexZenDbContext context) => _context = context;

    public async Task<IReadOnlyList<SavedPassword>> GetAllAsync() =>
        await _context.SavedPasswords.OrderBy(p => p.Origin).ThenBy(p => p.Username).ToListAsync();

    public Task<SavedPassword?> GetByIdAsync(int id) => _context.SavedPasswords.FindAsync(id).AsTask();

    public Task<SavedPassword?> FindAsync(string origin, string username) =>
        _context.SavedPasswords.FirstOrDefaultAsync(p => p.Origin == origin && p.Username == username);

    public async Task SaveAsync(SavedPassword password)
    {
        if (password.Id == 0)
            _context.SavedPasswords.Add(password);
        else
            _context.SavedPasswords.Update(password);
        await _context.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var password = await _context.SavedPasswords.FindAsync(id);
        if (password == null)
            return;
        _context.SavedPasswords.Remove(password);
        await _context.SaveChangesAsync();
    }
}
