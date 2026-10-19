using System.Collections.Generic;
using System.Threading.Tasks;
using NexZen.Core.Entities;

namespace NexZen.Core.Interfaces;

public interface ICustomStyleRepository
{
    Task<IReadOnlyList<CustomStyle>> GetAllAsync();
    Task<CustomStyle?> GetAsync(string site);

    /// <summary>Creates or updates the site's stylesheet; empty CSS removes it.</summary>
    Task SaveAsync(string site, string css, bool enabled);
}
