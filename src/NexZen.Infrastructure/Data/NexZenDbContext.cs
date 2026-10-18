using Microsoft.EntityFrameworkCore;
using NexZen.Core.Entities;

namespace NexZen.Infrastructure.Data;

public class NexZenDbContext : DbContext
{
    public DbSet<Bookmark> Bookmarks { get; set; } = null!;
    public DbSet<HistoryItem> History { get; set; } = null!;
    public DbSet<DownloadRecord> DownloadRecords { get; set; } = null!;
    public DbSet<SavedPassword> SavedPasswords { get; set; } = null!;

    public NexZenDbContext(DbContextOptions<NexZenDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.Entity<Bookmark>().HasKey(b => b.Id);
        modelBuilder.Entity<HistoryItem>().HasKey(h => h.Id);
        modelBuilder.Entity<DownloadRecord>().HasKey(d => d.Id);
        modelBuilder.Entity<SavedPassword>().HasKey(p => p.Id);
        modelBuilder.Entity<SavedPassword>().HasIndex(p => new { p.Origin, p.Username }).IsUnique();
    }
}
