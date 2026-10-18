using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface IBookmarkRepository
{
    Task<IEnumerable<Bookmark>> GetAllAsync();
    Task<Bookmark?> GetByIdAsync(int id);
    Task AddAsync(Bookmark bookmark);
    Task DeleteAsync(int id);
}
