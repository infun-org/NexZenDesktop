using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface IHistoryRepository
{
    Task<IEnumerable<HistoryItem>> GetAllAsync();
    Task AddAsync(HistoryItem historyItem);
    Task UpdateAsync(HistoryItem historyItem);
    Task DeleteAsync(int id);
    Task ClearAsync();
    Task<int> DeleteMatchingAsync(DateTime? sinceUtc, string? host);
}
