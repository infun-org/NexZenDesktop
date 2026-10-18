using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface IDownloadRepository
{
    Task<IEnumerable<DownloadRecord>> GetAllAsync();
    Task<DownloadRecord?> GetByIdAsync(string id);
    Task AddAsync(DownloadRecord record);
    Task UpdateAsync(DownloadRecord record);
    Task DeleteAsync(string id);
    Task<int> DeleteMatchingAsync(DateTime? sinceUtc, string? host, IReadOnlyCollection<string>? keepIds);
}
