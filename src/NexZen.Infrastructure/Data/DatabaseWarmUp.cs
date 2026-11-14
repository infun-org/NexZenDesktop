using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace NexZen.Infrastructure.Data;

/// <summary>
/// Builds EF Core's internal services and the NexZenDbContext model, and compiles a first query,
/// on a background thread at launch (against an in-memory database, so nothing on disk is touched).
/// EF caches all of it per context type, so the real contexts created later start instantly.
/// </summary>
public static class DatabaseWarmUp
{
    public static void Start() => Task.Run(() =>
    {
        try
        {
            var options = new DbContextOptionsBuilder<NexZenDbContext>().UseSqlite("Data Source=:memory:").Options;
            using var db = new NexZenDbContext(options);
            db.Database.OpenConnection();
            db.Database.EnsureCreated();
            _ = db.Bookmarks.AsNoTracking().FirstOrDefault();
            _ = db.History.AsNoTracking().OrderByDescending(h => h.VisitedAt).Take(1).ToList();
        }
        catch
        {
            // Only a head start; the real database setup reports its own errors.
        }
    });
}
