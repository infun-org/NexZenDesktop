using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface ISavedPasswordRepository
{
    Task<IReadOnlyList<SavedPassword>> GetAllAsync();
    Task<SavedPassword?> GetByIdAsync(int id);
    Task<SavedPassword?> FindAsync(string origin, string username);
    Task SaveAsync(SavedPassword password);
    Task DeleteAsync(int id);
}
