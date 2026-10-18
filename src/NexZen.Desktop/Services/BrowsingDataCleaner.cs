using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Core;
using NexZen.Infrastructure.Services;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Services;

/// <summary>
/// Clears the normal browsing profile. Incognito's in-memory jar is left alone.
/// Download files on disk are left alone.
/// </summary>
public static class BrowsingDataCleaner
{
    const int OriginCap = 400;

    public static async Task<string> ClearAsync(MainViewModel viewModel, AvaloniaCefBrowser? browser, DataClearRequest request)
    {
        var host = string.IsNullOrWhiteSpace(request.Host) ? null : DataMatch.NormalizeHost(request.Host);
        request.Host = host;
        var parts = new List<string>();
        var origins = new HashSet<string>(StringComparer.Ordinal);
        var userDataDir = ResolveUserDataDir();

        if (host != null && (request.Cookies || request.Cache))
        {
            AddOrigin(origins, "https://" + host);
            AddOrigin(origins, "http://" + host);
        }
        else if (host == null && (request.Cookies || request.Cache))
        {
            foreach (var url in viewModel.HistoryUrlsMatching(request.SinceUtc, host))
                AddOrigin(origins, OriginOf(url));
        }

        if (request.History)
            await Run(parts, "history", async () =>
            {
                var count = await viewModel.DeleteHistoryMatchingAsync(request.SinceUtc, host);
                return count == 1 ? "1 history entry removed." : count + " history entries removed.";
            });

        if (request.DownloadHistory)
            await Run(parts, "downloads", async () =>
            {
                var keptActive = viewModel.Downloads.Any(item =>
                    item.State == "Downloading"
                    && DataMatch.InRange(item.Timestamp, storedIsUtc: false, request.SinceUtc)
                    && DataMatch.UrlMatchesHost(item.Url, host));
                var count = await viewModel.DeleteDownloadHistoryMatchingAsync(request.SinceUtc, host);
                var sentence = count == 1
                    ? "1 download history entry removed. The file on disk was kept."
                    : count + " download history entries removed. The files on disk were kept.";
                if (keptActive)
                    sentence += " Downloads still in progress were kept.";
                return sentence;
            });

        if (request.Cookies)
            await Run(parts, "cookies", async () =>
            {
                var (count, domains) = await DeleteCookiesAsync(request.SinceUtc, host);
                if (host == null)
                {
                    foreach (var domain in domains)
                    {
                        AddOrigin(origins, "https://" + domain);
                        AddOrigin(origins, "http://" + domain);
                    }
                }
                return count == 1 ? "1 cookie removed." : count + " cookies removed.";
            });

        if (host == null && request.SinceUtc == null && (request.Cookies || request.Cache))
            AddIndexedDbOrigins(origins, userDataDir);

        var storageTypes = StorageTypes(request);
        var clearedStorage = false;
        if (storageTypes.Length > 0 && origins.Count > 0)
        {
            if (!HasBrowserHost(browser))
            {
                parts.Add("Open a page to clear site storage.");
            }
            else
            {
                await Run(parts, "site storage", async () =>
                {
                    await ClearOriginsAsync(browser!, origins, storageTypes);
                    clearedStorage = true;
                    var sites = origins.Select(OriginHost).Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Count();
                    var sentence = sites == 1
                        ? "Site storage cleared for 1 site."
                        : "Site storage cleared for " + sites + " sites.";
                    if (host != null && request.SinceUtc != null)
                        sentence += " Site storage is not split by time, so that domain was cleared for all time.";
                    return sentence;
                });
            }
        }

        var fullCache = request.Cache && host == null && request.SinceUtc == null;
        if (request.Cache && !fullCache)
            parts.Add("The shared image cache is only removed for all sites and all time.");
        if (fullCache)
            await Run(parts, "cache", async () =>
            {
                await ClearHttpCacheAsync();
                return "Shared image cache cleared.";
            });

        if ((request.Cookies || request.Cache || clearedStorage) && browser != null
            && (host == null || DataMatch.UrlMatchesHost(browser.Address, host)))
        {
            try
            {
                browser.Reload(request.Cache);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DataManager] reload " + ex.Message);
            }
        }

        QueuePending(request, host, userDataDir, parts);
        return parts.Count == 0 ? "Nothing matched." : string.Join(" ", parts);
    }

    static async Task Run(List<string> parts, string name, Func<Task<string>> action)
    {
        try
        {
            parts.Add(await action());
        }
        catch (Exception ex)
        {
            parts.Add("Could not remove " + name + ".");
            Console.WriteLine("[DataManager] " + name + " " + ex.Message);
        }
    }

    static void QueuePending(DataClearRequest request, string? host, string userDataDir, List<string> parts)
    {
        var pending = new DataClearRequest
        {
            SinceUtc = request.SinceUtc,
            Host = host,
            UserDataDir = userDataDir
        };
        var names = new List<string>();
        if (request.History)
        {
            pending.History = true;
            names.Add("address bar history");
        }
        if (request.Autofill)
        {
            if (host == null)
            {
                pending.Autofill = true;
                names.Add("autofill data");
            }
            else
            {
                parts.Add("Autofill data is kept with the browser, not with one site. Choose All sites to remove it.");
            }
        }
        if (request.SiteSettings)
        {
            pending.SiteSettings = true;
            names.Add("site settings");
        }
        if (request.HostedAppData)
        {
            pending.HostedAppData = true;
            names.Add("hosted app data");
        }
        if (names.Count == 0)
            return;

        try
        {
            PendingProfileCleaner.Enqueue(pending);
            parts.Add(Sentence(names) + " will finish clearing the next time NexZen starts, because Chromium keeps those files open.");
        }
        catch (Exception ex)
        {
            parts.Add("Could not schedule the remaining data for the next start.");
            Console.WriteLine("[DataManager] pending " + ex.Message);
        }
    }

    static string Sentence(List<string> names)
    {
        var text = names.Count switch
        {
            1 => names[0],
            2 => names[0] + " and " + names[1],
            _ => string.Join(", ", names.Take(names.Count - 1)) + ", and " + names[^1]
        };
        if (text.Length == 0)
            return text;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    static string StorageTypes(DataClearRequest request)
    {
        if (request.Cookies)
            return "local_storage,session_storage,indexeddb,websql,file_systems,service_workers,cache_storage,shared_storage";
        if (request.Cache)
            return "cache_storage";
        return "";
    }

    static bool HasBrowserHost(AvaloniaCefBrowser? browser)
    {
        if (browser == null)
            return false;
        var host = browser.GetBrowserHost();
        if (host == null)
            return false;
        host.Dispose();
        return true;
    }

    static Task ClearOriginsAsync(AvaloniaCefBrowser browser, IReadOnlyCollection<string> origins, string storageTypes)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            CefBrowserHost? host = null;
            try
            {
                host = browser.GetBrowserHost();
                if (host == null)
                    throw new InvalidOperationException("No page is open.");
                foreach (var origin in origins)
                {
                    var parameters = CefDictionaryValue.Create();
                    try
                    {
                        parameters.SetString("origin", origin);
                        parameters.SetString("storageTypes", storageTypes);
                        host.ExecuteDevToolsMethod(0, "Storage.clearDataForOrigin", parameters);
                    }
                    finally
                    {
                        parameters.Dispose();
                    }
                }
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                host?.Dispose();
            }
        }));
        if (!posted)
            done.TrySetException(new InvalidOperationException("Could not reach Chromium."));
        return done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    static async Task ClearHttpCacheAsync()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CacheClearedCallback? callback = null;
        var posted = CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            CefRequestContext? context = null;
            try
            {
                context = CefRequestContext.GetGlobalContext();
                callback = new CacheClearedCallback(done, context);
                context = null;
                callback.Clear();
            }
            catch (Exception ex)
            {
                if (callback != null)
                    callback.Release();
                else
                {
                    try { context?.Dispose(); } catch { /* Already failing. */ }
                }
                done.TrySetException(ex);
            }
        }));
        if (!posted)
            throw new InvalidOperationException("Could not reach Chromium.");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        GC.KeepAlive(callback);
    }

    static async Task<(int Count, List<string> Domains)> DeleteCookiesAsync(DateTime? sinceUtc, string? host)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var init = new ReadyCallback(ready);
        var manager = CefCookieManager.GetGlobal(init);
        if (manager == null)
            return (0, new List<string>());

        try
        {
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // Storage may already be open. The visit below still runs.
            }
            GC.KeepAlive(init);

            if (host == null && sinceUtc == null)
            {
                var callback = new DeleteCountCallback();
                if (!manager.DeleteCookies(string.Empty, string.Empty, callback))
                    return (0, new List<string>());
                var count = await callback.Done.WaitAsync(TimeSpan.FromSeconds(20));
                GC.KeepAlive(callback);
                return (count, new List<string>());
            }

            var sweep = new CookieSweep(sinceUtc, host);
            if (!manager.VisitAllCookies(sweep))
            {
                sweep.Finish();
                return (0, new List<string>());
            }

            CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
            {
                if (sweep.Seen == 0)
                    sweep.Finish();
            }), 1500);

            try
            {
                await sweep.Done.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                sweep.Finish();
            }
            GC.KeepAlive(sweep);
            return (sweep.Deleted, sweep.Domains.ToList());
        }
        finally
        {
            manager.Dispose();
        }
    }

    public static string ResolveUserDataDir()
    {
        try
        {
            var context = CefRequestContext.GetGlobalContext();
            try
            {
                var cache = context.CachePath;
                if (!string.IsNullOrWhiteSpace(cache))
                {
                    if (File.Exists(Path.Combine(cache, "Preferences")))
                    {
                        var parent = Directory.GetParent(cache);
                        if (parent != null)
                            return parent.FullName;
                    }
                    if (File.Exists(Path.Combine(cache, "Default", "Preferences")))
                        return cache;
                }
            }
            finally
            {
                context.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DataManager] user data dir " + ex.Message);
        }
        return PendingProfileCleaner.FallbackUserDataDir();
    }

    static void AddIndexedDbOrigins(HashSet<string> origins, string userDataDir)
    {
        var root = Path.Combine(userDataDir, "Default", "IndexedDB");
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (origins.Count >= OriginCap)
                return;
            var name = Path.GetFileName(dir);
            const string suffix = ".indexeddb.leveldb";
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var body = name[..^suffix.Length];
            var first = body.IndexOf('_');
            var last = body.LastIndexOf('_');
            if (first <= 0 || last <= first)
                continue;
            var scheme = body[..first];
            if (scheme != "https" && scheme != "http")
                continue;
            if (!int.TryParse(body[(last + 1)..], out var port))
                continue;
            var siteHost = body[(first + 1)..last];
            if (siteHost.Length == 0)
                continue;
            var defaultPort = port == 0 || (scheme == "https" && port == 443) || (scheme == "http" && port == 80);
            AddOrigin(origins, defaultPort ? scheme + "://" + siteHost : scheme + "://" + siteHost + ":" + port);
        }
    }

    static void AddOrigin(HashSet<string> origins, string? origin)
    {
        if (string.IsNullOrEmpty(origin) || origins.Count >= OriginCap)
            return;
        origins.Add(origin);
    }

    static string? OriginOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return null;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        return uri.Scheme.ToLowerInvariant() + "://" + uri.Host.ToLowerInvariant() + port;
    }

    static string OriginHost(string origin)
    {
        return DataMatch.NormalizeHost(origin) ?? "";
    }

    sealed class ReadyCallback : CefCompletionCallback
    {
        readonly TaskCompletionSource<bool> _done;
        public ReadyCallback(TaskCompletionSource<bool> done) => _done = done;
        protected override void OnComplete() => _done.TrySetResult(true);
    }

    sealed class DeleteCountCallback : CefDeleteCookiesCallback
    {
        readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Done => _done.Task;
        protected override void OnComplete(int numDeleted) => _done.TrySetResult(numDeleted);
    }

    sealed class CacheClearedCallback : CefCompletionCallback
    {
        readonly TaskCompletionSource<bool> _done;
        readonly CefRequestContext _context;
        int _released;

        public CacheClearedCallback(TaskCompletionSource<bool> done, CefRequestContext context)
        {
            _done = done;
            _context = context;
        }

        public void Clear() => _context.ClearHttpCache(this);

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try { _context.Dispose(); }
                catch { /* The clear already finished. */ }
            }
        }

        protected override void OnComplete()
        {
            Release();
            _done.TrySetResult(true);
        }
    }

    sealed class CookieSweep : CefCookieVisitor
    {
        readonly DateTime? _since;
        readonly string? _host;
        readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CookieSweep(DateTime? since, string? host)
        {
            _since = since;
            _host = host;
        }

        public Task<int> Done => _done.Task;
        public int Seen;
        public int Deleted;
        public HashSet<string> Domains { get; } = new(StringComparer.Ordinal);

        public void Finish() => _done.TrySetResult(Deleted);

        protected override bool Visit(CefCookie cookie, int count, int total, out bool deleteCookie)
        {
            Seen++;
            deleteCookie = Selected(cookie);
            if (deleteCookie)
            {
                Deleted++;
                var domain = DataMatch.NormalizeHost(cookie.Domain);
                if (!string.IsNullOrEmpty(domain))
                    Domains.Add(domain);
            }
            if (total <= 0 || count >= total - 1)
                Finish();
            return true;
        }

        bool Selected(CefCookie cookie)
        {
            if (_host != null && !DataMatch.CookieMatchesHost(cookie.Domain, _host))
                return false;
            if (_since == null)
                return true;
            var when = ToUtc(cookie.LastAccess) ?? ToUtc(cookie.Creation);
            if (when == null)
                return false;
            return when >= _since.Value;
        }

        static DateTime? ToUtc(CefBaseTime time)
        {
            if (time.Ticks <= 0 || !time.UtcExplode(out var exploded))
                return null;
            if (exploded.Year is < 1 or > 9999)
                return null;
            var second = exploded.Second >= 60 ? 59 : exploded.Second;
            var millisecond = exploded.Millisecond is >= 0 and <= 999 ? exploded.Millisecond : 0;
            try
            {
                return new DateTime(
                    exploded.Year,
                    exploded.Month,
                    exploded.DayOfMonth,
                    exploded.Hour,
                    exploded.Minute,
                    second,
                    millisecond,
                    DateTimeKind.Utc);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
