using Microsoft.Data.Sqlite;

namespace NexZen.Infrastructure.Data;

/// <summary>
/// nexzen.db used to be opened relative to the working directory, so launching from the
/// build folder, from `dotnet run` or from the menu each used a different database.
/// It now lives in one place; the first start copies the most recently used old file.
/// Old files are left where they are.
/// </summary>
public static class DatabaseLocation
{
    public const string FileName = "nexzen.db";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop");

    /// <returns>Full path of the database to open, and a note when an old file was copied.</returns>
    public static (string Path, string? Migrated) Resolve(string? directory = null, params string?[] legacyDirectories)
    {
        var dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, FileName);
        if (File.Exists(target))
            return (target, null);

        var source = legacyDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(Path.Combine(d!, FileName)))
            .Distinct(StringComparer.Ordinal)
            .Where(p => File.Exists(p) && !string.Equals(p, target, StringComparison.Ordinal))
            .OrderByDescending(LastWrite)
            .FirstOrDefault();
        if (source == null)
            return (target, null);

        // The backup API reads committed pages from the -wal file too; a plain copy would not.
        var temp = target + ".migrating";
        using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
        using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
        File.Move(temp, target);
        return (target, source);
    }

    public static string ConnectionString(string path) => $"Data Source={path}";

    private static DateTime LastWrite(string dbPath)
    {
        var time = File.GetLastWriteTimeUtc(dbPath);
        var wal = dbPath + "-wal";
        if (File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > time)
            time = File.GetLastWriteTimeUtc(wal);
        return time;
    }
}
