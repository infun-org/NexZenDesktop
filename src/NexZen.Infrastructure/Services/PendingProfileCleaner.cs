using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using NexZen.Application.Services;
using NexZen.Core;

namespace NexZen.Infrastructure.Services;

/// <summary>
/// Applies autofill, site settings, hosted app data, and Chromium history
/// before CEF opens those files. A failed run keeps the request for the next start.
/// </summary>
public static class PendingProfileCleaner
{
    static readonly string[] AutofillTables =
    [
        "autofill",
        "autofill_profiles",
        "autofill_profile_names",
        "autofill_profile_emails",
        "autofill_profile_phones",
        "autofill_profile_addresses",
        "autofill_profile_birthdates",
        "credit_cards",
        "server_addresses",
        "server_card_metadata",
        "local_addresses",
        "contact_info",
        "local_ibans"
    ];

    public static string PendingPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexZenDesktop");
        return Path.Combine(dir, "pending-data-clear.json");
    }

    public static string FallbackUserDataDir()
    {
        if (OperatingSystem.IsLinux())
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(config))
            {
                config = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config");
            }
            return Path.Combine(config, "cef_user_data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cef_user_data");
    }

    public static void Enqueue(DataClearRequest request)
    {
        if (!HasWork(request))
            return;

        var path = PendingPath();
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var incoming = Copy(request);
        if (string.IsNullOrWhiteSpace(incoming.UserDataDir))
            incoming.UserDataDir = FallbackUserDataDir();

        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<DataClearRequest>(File.ReadAllText(path));
                if (existing != null && HasWork(existing))
                    incoming = Merge(existing, incoming);
            }
            catch
            {
                // An unreadable file is replaced by the new request.
            }
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(incoming));
        File.Move(temporary, path, overwrite: true);
    }

    public static string Apply()
    {
        var path = PendingPath();
        if (!File.Exists(path))
            return "none";

        DataClearRequest? job;
        try
        {
            job = JsonSerializer.Deserialize<DataClearRequest>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            TryDelete(path);
            return "discarded unreadable request: " + ex.Message;
        }

        if (job == null || !HasWork(job))
        {
            TryDelete(path);
            return "empty";
        }

        if (job.SinceUtc is DateTime since && since.Kind != DateTimeKind.Utc)
            job.SinceUtc = DateTime.SpecifyKind(since, DateTimeKind.Utc);

        var userData = job.UserDataDir;
        if (string.IsNullOrWhiteSpace(userData) || !Directory.Exists(userData))
            userData = FallbackUserDataDir();
        var profile = Path.Combine(userData, "Default");
        if (!Directory.Exists(profile))
            return "profile missing, kept for next start";

        var notes = new List<string>();
        var failed = false;
        if (job.Autofill && string.IsNullOrEmpty(job.Host))
            failed |= Run(notes, "autofill", () => ClearAutofill(profile, job.SinceUtc));
        if (job.SiteSettings)
            failed |= Run(notes, "site settings", () => ClearSiteSettings(profile, job.SinceUtc, job.Host));
        if (job.HostedAppData)
            failed |= Run(notes, "hosted app data", () => ClearHostedApps(profile, job.SinceUtc, job.Host));
        if (job.History)
            failed |= Run(notes, "chromium history", () => ClearChromiumHistory(profile, job.SinceUtc, job.Host));

        if (failed)
            return "kept for next start; " + string.Join("; ", notes);

        TryDelete(path);
        return "applied; " + string.Join("; ", notes);
    }

    static bool HasWork(DataClearRequest request)
    {
        return request.History || request.Autofill || request.SiteSettings || request.HostedAppData;
    }

    static DataClearRequest Copy(DataClearRequest request)
    {
        return new DataClearRequest
        {
            History = request.History,
            Cookies = request.Cookies,
            Cache = request.Cache,
            DownloadHistory = request.DownloadHistory,
            Autofill = request.Autofill,
            SiteSettings = request.SiteSettings,
            HostedAppData = request.HostedAppData,
            SinceUtc = request.SinceUtc,
            Host = DataMatch.NormalizeHost(request.Host),
            UserDataDir = request.UserDataDir
        };
    }

    static DataClearRequest Merge(DataClearRequest older, DataClearRequest newer)
    {
        return new DataClearRequest
        {
            History = older.History || newer.History,
            Cookies = older.Cookies || newer.Cookies,
            Cache = older.Cache || newer.Cache,
            DownloadHistory = older.DownloadHistory || newer.DownloadHistory,
            Autofill = older.Autofill || newer.Autofill,
            SiteSettings = older.SiteSettings || newer.SiteSettings,
            HostedAppData = older.HostedAppData || newer.HostedAppData,
            SinceUtc = MergeSince(older.SinceUtc, newer.SinceUtc),
            Host = MergeHost(older.Host, newer.Host),
            UserDataDir = string.IsNullOrWhiteSpace(newer.UserDataDir) ? older.UserDataDir : newer.UserDataDir
        };
    }

    static DateTime? MergeSince(DateTime? older, DateTime? newer)
    {
        if (older == null || newer == null)
            return null;
        var left = older.Value.Kind == DateTimeKind.Utc
            ? older.Value
            : DateTime.SpecifyKind(older.Value, DateTimeKind.Utc);
        var right = newer.Value.Kind == DateTimeKind.Utc
            ? newer.Value
            : DateTime.SpecifyKind(newer.Value, DateTimeKind.Utc);
        return left <= right ? left : right;
    }

    static string? MergeHost(string? older, string? newer)
    {
        var left = DataMatch.NormalizeHost(older);
        var right = DataMatch.NormalizeHost(newer);
        if (left == null || right == null)
            return null;
        if (left == right)
            return left;
        if (DataMatch.HostMatches(left, right))
            return right;
        if (DataMatch.HostMatches(right, left))
            return left;
        return null;
    }

    static bool Run(List<string> notes, string name, Func<string> action)
    {
        try
        {
            notes.Add(name + ": " + action());
            return false;
        }
        catch (Exception ex)
        {
            notes.Add(name + " failed: " + ex.Message);
            return true;
        }
    }

    static string ClearAutofill(string profile, DateTime? sinceUtc)
    {
        var removed = 0;
        var files = 0;
        foreach (var name in new[] { "Web Data", "Account Web Data" })
        {
            var path = Path.Combine(profile, name);
            if (!File.Exists(path))
                continue;
            files++;
            removed += ClearAutofillFile(path, sinceUtc);
        }
        return files == 0 ? "no database" : "removed " + removed;
    }

    static int ClearAutofillFile(string path, DateTime? sinceUtc)
    {
        using var connection = OpenSqlite(path);
        using var transaction = connection.BeginTransaction();
        var removed = 0;
        foreach (var table in AutofillTables)
        {
            if (!TableExists(connection, table))
                continue;
            if (sinceUtc == null)
            {
                removed += Execute(connection, "DELETE FROM " + table);
                continue;
            }

            var column = FirstDateColumn(connection, table);
            if (column == null)
                continue;
            var cutoff = (sinceUtc.Value - DataMatch.ChromeEpoch).Ticks / 10;
            removed += Execute(connection, "DELETE FROM " + table + " WHERE " + column + " >= $cutoff", ("$cutoff", cutoff));
        }
        transaction.Commit();
        return removed;
    }

    static string? FirstDateColumn(SqliteConnection connection, string table)
    {
        var columns = ColumnNames(connection, table);
        if (columns.Contains("date_last_used"))
            return "date_last_used";
        if (columns.Contains("date_created"))
            return "date_created";
        return null;
    }

    static string ClearSiteSettings(string profile, DateTime? sinceUtc, string? host)
    {
        var path = Path.Combine(profile, "Preferences");
        if (!File.Exists(path))
            return "no preferences";

        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            return "unreadable";
        if (root["profile"] is not JsonObject profileNode
            || profileNode["content_settings"] is not JsonObject content
            || content["exceptions"] is not JsonObject exceptions)
            return "no exceptions";

        var removed = 0;
        foreach (var group in exceptions)
        {
            if (group.Value is not JsonObject entries)
                continue;
            var drop = new List<string>();
            foreach (var entry in entries)
            {
                if (!SettingInRange(entry.Value, sinceUtc))
                    continue;
                if (!string.IsNullOrEmpty(host) && !DataMatch.ContentSettingMatchesHost(entry.Key, host))
                    continue;
                drop.Add(entry.Key);
            }
            foreach (var key in drop)
            {
                entries.Remove(key);
                removed++;
            }
        }
        if (removed == 0)
            return "removed 0";

        var temporary = path + ".nexzen-tmp";
        File.WriteAllText(temporary, root.ToJsonString());
        File.Move(temporary, path, overwrite: true);
        return "removed " + removed;
    }

    static bool SettingInRange(JsonNode? entry, DateTime? sinceUtc)
    {
        if (sinceUtc == null)
            return true;
        if (entry is not JsonObject obj
            || !obj.TryGetPropertyValue("last_modified", out var node)
            || node is not JsonValue value)
            return false;

        long micros;
        if (value.TryGetValue<string>(out var text))
        {
            if (!long.TryParse(text, out micros))
                return false;
        }
        else if (!value.TryGetValue<long>(out micros))
        {
            return false;
        }
        return DataMatch.ChromeMicrosInRange(micros, sinceUtc);
    }

    static string ClearHostedApps(string profile, DateTime? sinceUtc, string? host)
    {
        var removed = 0;
        foreach (var folder in new[] { "Web Applications", "Hosted Apps" })
        {
            var root = Path.Combine(profile, folder);
            if (!Directory.Exists(root))
                continue;
            foreach (var entry in Directory.GetFileSystemEntries(root))
            {
                var name = Path.GetFileName(entry);
                if (!string.IsNullOrEmpty(host) && !DataMatch.TextRefersToHost(name, host))
                    continue;
                if (sinceUtc != null)
                {
                    var written = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);
                    if (written < sinceUtc.Value)
                        continue;
                }
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
                removed++;
            }
        }
        return "removed " + removed;
    }

    static string ClearChromiumHistory(string profile, DateTime? sinceUtc, string? host)
    {
        var path = Path.Combine(profile, "History");
        if (!File.Exists(path))
            return "no database";

        using var connection = OpenSqlite(path);
        using var transaction = connection.BeginTransaction();
        if (!TableExists(connection, "urls") || !TableExists(connection, "visits"))
        {
            transaction.Commit();
            return "tables missing";
        }

        int visits;
        if (sinceUtc == null && string.IsNullOrEmpty(host))
        {
            visits = Execute(connection, "DELETE FROM visits");
            if (TableExists(connection, "keyword_search_terms") && ColumnNames(connection, "keyword_search_terms").Contains("url_id"))
                Execute(connection, "DELETE FROM keyword_search_terms");
            Execute(connection, "DELETE FROM urls");
        }
        else
        {
            var urls = ReadUrls(connection);
            var doomedVisits = new List<long>();
            var touchedUrls = new HashSet<long>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, url, visit_time FROM visits";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var urlId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    var time = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                    if (!DataMatch.ChromeMicrosInRange(time, sinceUtc))
                        continue;
                    if (!string.IsNullOrEmpty(host))
                    {
                        if (!urls.TryGetValue(urlId, out var url) || !DataMatch.UrlMatchesHost(url, host))
                            continue;
                    }
                    doomedVisits.Add(id);
                    if (urlId > 0)
                        touchedUrls.Add(urlId);
                }
            }

            visits = doomedVisits.Count;
            DeleteIds(connection, "visits", "id", doomedVisits);
            var remaining = ReadVisitUrlIds(connection);
            var removedUrls = touchedUrls.Where(id => !remaining.Contains(id)).ToList();
            if (TableExists(connection, "keyword_search_terms") && ColumnNames(connection, "keyword_search_terms").Contains("url_id"))
                DeleteIds(connection, "keyword_search_terms", "url_id", removedUrls);
            DeleteIds(connection, "urls", "id", removedUrls);
        }

        transaction.Commit();
        return "visits " + visits;
    }

    static Dictionary<long, string> ReadUrls(SqliteConnection connection)
    {
        var urls = new Dictionary<long, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, url FROM urls";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
                urls[reader.GetInt64(0)] = reader.GetString(1);
        }
        return urls;
    }

    static HashSet<long> ReadVisitUrlIds(SqliteConnection connection)
    {
        var ids = new HashSet<long>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT url FROM visits";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    static SqliteConnection OpenSqlite(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 3000";
        command.ExecuteNonQuery();
        return connection;
    }

    static bool TableExists(SqliteConnection connection, string table)
    {
        if (!SafeIdent(table))
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";
        command.Parameters.AddWithValue("$name", table);
        var result = command.ExecuteScalar();
        return result != null && result != DBNull.Value;
    }

    static HashSet<string> ColumnNames(SqliteConnection connection, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!SafeIdent(table))
            return names;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + table + ")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }

    static int Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteNonQuery();
    }

    static int DeleteIds(SqliteConnection connection, string table, string column, List<long> ids)
    {
        if (!SafeIdent(table) || !SafeIdent(column) || ids.Count == 0)
            return 0;
        var removed = 0;
        for (var offset = 0; offset < ids.Count; offset += 400)
        {
            var count = Math.Min(400, ids.Count - offset);
            using var command = connection.CreateCommand();
            var names = new string[count];
            for (var index = 0; index < count; index++)
            {
                names[index] = "$p" + index;
                command.Parameters.AddWithValue(names[index], ids[offset + index]);
            }
            command.CommandText = "DELETE FROM " + table + " WHERE " + column + " IN (" + string.Join(",", names) + ")";
            removed += command.ExecuteNonQuery();
        }
        return removed;
    }

    static bool SafeIdent(string name)
    {
        if (name.Length == 0)
            return false;
        foreach (var ch in name)
        {
            if (ch is not (>= 'a' and <= 'z' or '_'))
                return false;
        }
        return true;
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The next start will try again.
        }
    }
}
