using System.Threading;
using System.Threading.Tasks;

namespace NexZen.Core.Interfaces;

public interface IFaviconFetcher
{
    /// <summary>
    /// The site's icon for a page as a data:image/... URL, or null when none could be loaded.
    /// Used for links typed into a workspace, which were never open in a tab.
    /// </summary>
    Task<string?> FetchDataUrlAsync(string pageUrl, CancellationToken cancellationToken = default);
}
