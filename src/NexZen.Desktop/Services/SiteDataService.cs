using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Services;

/// <summary>
/// Site privacy for one page, with CEF: which sites the page used (its resource timings),
/// cookies (cookie manager), storage (DevTools protocol Storage domain, run in-process),
/// and per-site permissions (Chrome content settings of the request context).
/// </summary>
internal sealed class SiteDataService : ISiteDataService
{
    private readonly AvaloniaCefBrowser _browser;
    // Origins the page used, per site; storage is measured and cleared per origin.
    private readonly ConcurrentDictionary<string, HashSet<string>> _origins = new();

    public SiteDataService(AvaloniaCefBrowser browser)
    {
        _browser = browser;
    }

    // Body of a function (CefGlue's EvaluateJavaScript): the page's origin and every origin it loaded from.
    private const string OriginsScript = """
        var seen = {};
        var list = [];
        function add(u) {
          try {
            var url = new URL(u, location.href);
            if ((url.protocol === 'http:' || url.protocol === 'https:') && !seen[url.origin]) {
              seen[url.origin] = true;
              list.push(url.origin);
            }
          } catch (e) {}
        }
        add(location.href);
        try { performance.getEntriesByType('resource').forEach(function (e) { add(e.name); }); } catch (e) {}
        try { document.querySelectorAll('iframe[src],script[src],img[src],link[href]').forEach(function (el) { add(el.src || el.href); }); } catch (e) {}
        return JSON.stringify(list);
        """;

    public async Task<IReadOnlyList<string>> GetSitesAsync()
    {
        var address = _browser.Address ?? "";
        var json = await _browser.EvaluateJavaScript<string>(OriginsScript, url: address, timeout: TimeSpan.FromSeconds(5));
        var origins = JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? Array.Empty<string>();
        var sites = new List<string>();
        foreach (var origin in origins)
        {
            var site = SiteDomains.SiteOf(origin);
            if (site == null)
                continue;
            _origins.GetOrAdd(site, _ => new HashSet<string>()).Add(origin);
            if (!sites.Contains(site))
                sites.Add(site);
        }
        return sites;
    }

    public async Task<IReadOnlyList<SiteUsage>> GetUsageAsync(IReadOnlyList<string> sites)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cookies = await ReadCookiesAsync();
        Console.WriteLine($"[SitePrivacy] cookies read: {cookies.Count} in {watch.ElapsedMilliseconds} ms");

        // Every origin at once; one slow or failing origin only costs its own timeout.
        var storageBySite = new ConcurrentDictionary<string, long>();
        var queries = sites.SelectMany(site => OriginsOf(site).Select(origin => (site, origin))).Select(async pair =>
        {
            try
            {
                var reply = await DevToolsAsync("Storage.getUsageAndQuota", new Dictionary<string, string> { ["origin"] = pair.origin });
                if (reply.TryGetProperty("usage", out var usage) && usage.TryGetDouble(out var bytes))
                    storageBySite.AddOrUpdate(pair.site, (long)bytes, (_, total) => total + (long)bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SitePrivacy] usage {pair.origin}: {ex.Message}");
            }
        });
        await Task.WhenAll(queries);
        Console.WriteLine($"[SitePrivacy] storage measured for {sites.Count} site(s) in {watch.ElapsedMilliseconds} ms");

        var result = new List<SiteUsage>();
        foreach (var site in sites)
        {
            var siteCookies = cookies.Where(c => SiteDomains.SiteOfHost(c.Domain) == site).ToList();
            var cookieBytes = siteCookies.Sum(c => (long)(c.Name.Length + c.Value.Length));
            result.Add(new SiteUsage(site, storageBySite.GetValueOrDefault(site) + cookieBytes, siteCookies.Count));
        }
        return result;
    }

    public async Task DeleteDataAsync(string site)
    {
        foreach (var origin in OriginsOf(site))
        {
            try
            {
                await DevToolsAsync("Storage.clearDataForOrigin", new Dictionary<string, string>
                {
                    ["origin"] = origin,
                    ["storageTypes"] = "all"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SitePrivacy] clear {origin}: {ex.Message}");
            }
        }

        // Cookies of every host of the site (domain cookies like ".google.com" included).
        var cookies = await ReadCookiesAsync();
        var manager = CefCookieManager.GetGlobal(null);
        foreach (var cookie in cookies.Where(c => SiteDomains.SiteOfHost(c.Domain) == site))
        {
            var host = cookie.Domain.TrimStart('.');
            var scheme = cookie.Secure ? "https" : "http";
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.DeleteCookies($"{scheme}://{host}{(string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path)}", cookie.Name,
                new DeleteCallback(() => done.TrySetResult()));
            await Task.WhenAny(done.Task, Task.Delay(2000));
        }
        manager.FlushStore(null);
    }

    public void Apply(string site, string key, SitePermissionValue value)
    {
        SiteContentSettings.Apply(_browser.RequestContext, site, key, value);
        // Sound takes effect at once on the page that is open (tab mute).
        if (key == SitePermissionKeys.Sound && SiteDomains.SiteOf(_browser.Address) == site)
            SoundChanged?.Invoke(_browser);
    }

    /// <summary>Raised when the open page's site got a new Sound choice.</summary>
    public static event Action<AvaloniaCefBrowser>? SoundChanged;

    // The origins seen on the page, plus the bare and www origins of the site.
    private IEnumerable<string> OriginsOf(string site)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"https://{site}", $"https://www.{site}", $"http://{site}", $"http://www.{site}"
        };
        if (_origins.TryGetValue(site, out var seen))
            origins.UnionWith(seen);
        return origins;
    }

    private sealed record CookieInfo(string Domain, string Name, string Value, string Path, bool Secure);

    private static Task<List<CookieInfo>> ReadCookiesAsync()
    {
        var done = new TaskCompletionSource<List<CookieInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visitor = new CookieCollector(done);
        if (!CefCookieManager.GetGlobal(null).VisitAllCookies(visitor))
            done.TrySetResult(new List<CookieInfo>());
        return Task.WhenAny(done.Task, Task.Delay(3000)).ContinueWith(_ =>
            done.Task.IsCompleted ? done.Task.Result : visitor.Snapshot());
    }

    private sealed class CookieCollector : CefCookieVisitor
    {
        private readonly TaskCompletionSource<List<CookieInfo>> _done;
        private readonly List<CookieInfo> _cookies = new();

        public CookieCollector(TaskCompletionSource<List<CookieInfo>> done) => _done = done;

        public List<CookieInfo> Snapshot()
        {
            lock (_cookies)
                return _cookies.ToList();
        }

        protected override bool Visit(CefCookie cookie, int count, int total, out bool delete)
        {
            delete = false;
            lock (_cookies)
                _cookies.Add(new CookieInfo(cookie.Domain ?? "", cookie.Name ?? "", cookie.Value ?? "", cookie.Path ?? "/", cookie.Secure));
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            // Called once the visit is over (also when there are no cookies at all).
            _done.TrySetResult(Snapshot());
            base.Dispose(disposing);
        }
    }

    private sealed class DeleteCallback : CefDeleteCookiesCallback
    {
        private readonly Action _done;
        public DeleteCallback(Action done) => _done = done;
        protected override void OnComplete(int numDeleted) => _done();
    }

    // ---- DevTools protocol, in-process (no debugging port) ----

    private static int _nextMessageId = 1000;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private CefRegistration? _registration;
    private DevToolsObserver? _observer;

    /// <summary>Runs a DevTools protocol method on this page's browser and returns its result object.</summary>
    private Task<JsonElement> DevToolsAsync(string method, IReadOnlyDictionary<string, string> parameters)
    {
        var host = _browser.GetBrowserHost();
        if (host == null)
            return Task.FromException<JsonElement>(new InvalidOperationException("The page is not ready."));
        var id = Interlocked.Increment(ref _nextMessageId);
        var done = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = done;

        // ExecuteDevToolsMethod must run on CEF's UI thread.
        CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            try
            {
                if (_observer == null)
                {
                    _observer = new DevToolsObserver(this);
                    _registration = host.AddDevToolsMessageObserver(_observer);
                }
                var args = CefDictionaryValue.Create();
                foreach (var (name, value) in parameters)
                    args.SetString(name, value);
                if (host.ExecuteDevToolsMethod(id, method, args) == 0)
                    Fail(id, "DevTools method could not be sent.");
            }
            catch (Exception ex)
            {
                Fail(id, ex.Message);
            }
        }));

        return Task.WhenAny(done.Task, Task.Delay(3000)).ContinueWith(t =>
        {
            _pending.TryRemove(id, out _);
            if (!done.Task.IsCompleted)
                throw new TimeoutException(method + " timed out.");
            return done.Task.Result;
        });
    }

    private void Fail(int id, string message)
    {
        if (_pending.TryRemove(id, out var done))
            done.TrySetException(new InvalidOperationException(message));
    }

    private void Complete(int id, bool success, string json)
    {
        if (!_pending.TryRemove(id, out var done))
            return;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            var root = document.RootElement.Clone();
            if (success)
                done.TrySetResult(root);
            else
                done.TrySetException(new InvalidOperationException(
                    root.TryGetProperty("message", out var message) ? message.GetString() : json));
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }

    private sealed class DevToolsObserver : CefDevToolsMessageObserver
    {
        private readonly SiteDataService _owner;
        public DevToolsObserver(SiteDataService owner) => _owner = owner;

        protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize) => false;

        protected override void OnDevToolsMethodResult(CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
        {
            var json = result == IntPtr.Zero || resultSize <= 0 ? "{}" : Marshal.PtrToStringUTF8(result, resultSize);
            _owner.Complete(messageId, success, json);
        }

        protected override void OnDevToolsEvent(CefBrowser browser, string method, IntPtr parameters, int parametersSize) { }
        protected override void OnDevToolsAgentAttached(CefBrowser browser) { }
        protected override void OnDevToolsAgentDetached(CefBrowser browser) { }
    }
}

/// <summary>Applies Site privacy choices as Chrome content settings of a request context.</summary>
internal static class SiteContentSettings
{
    // CEF 152 cef_content_setting_types_t values. CefGlue's enum predates several newer
    // types and its numbering drifts after JAVASCRIPT_JIT (67), so the numbers are used directly.
    private static int[] TypesOf(string key) => key switch
    {
        SitePermissionKeys.Location => new[] { 4 },                 // GEOLOCATION
        SitePermissionKeys.Camera => new[] { 9 },                   // MEDIASTREAM_CAMERA
        SitePermissionKeys.Microphone => new[] { 8 },               // MEDIASTREAM_MIC
        SitePermissionKeys.Motion => new[] { 32 },                  // SENSORS
        SitePermissionKeys.Notifications => new[] { 5 },            // NOTIFICATIONS
        SitePermissionKeys.JavaScript => new[] { 2 },               // JAVASCRIPT
        SitePermissionKeys.Images => new[] { 1 },                   // IMAGES
        SitePermissionKeys.Popups => new[] { 3 },                   // POPUPS
        SitePermissionKeys.Ads => new[] { 25 },                     // ADS
        SitePermissionKeys.BackgroundSync => new[] { 21 },          // BACKGROUND_SYNC
        SitePermissionKeys.Sound => new[] { 30 },                   // SOUND
        SitePermissionKeys.AutomaticDownloads => new[] { 12 },      // AUTOMATIC_DOWNLOADS
        SitePermissionKeys.Midi => new[] { 13 },                    // MIDI_SYSEX
        SitePermissionKeys.Usb => new[] { 35 },                     // USB_GUARD
        SitePermissionKeys.Serial => new[] { 39 },                  // SERIAL_GUARD
        SitePermissionKeys.FileEditing => new[] { 48 },             // FILE_SYSTEM_WRITE_GUARD
        SitePermissionKeys.Hid => new[] { 43 },                     // HID_GUARD
        SitePermissionKeys.Clipboard => new[] { 51 },               // CLIPBOARD_READ_WRITE
        SitePermissionKeys.PaymentHandlers => new[] { 34 },         // PAYMENT_HANDLER
        SitePermissionKeys.InsecureContent => new[] { 7 },          // MIXEDSCRIPT
        SitePermissionKeys.JavaScriptOptimization => new[] { 110 }, // JAVASCRIPT_OPTIMIZER
        SitePermissionKeys.ThirdPartySignIn => new[] { 73 },        // FEDERATED_IDENTITY_API
        SitePermissionKeys.AugmentedReality => new[] { 55 },        // AR
        SitePermissionKeys.VirtualReality => new[] { 54 },          // VR
        SitePermissionKeys.DeviceUse => new[] { 38 },               // IDLE_DETECTION
        SitePermissionKeys.WindowManagement => new[] { 59 },        // WINDOW_MANAGEMENT
        SitePermissionKeys.Fonts => new[] { 61 },                   // LOCAL_FONTS
        SitePermissionKeys.AutoPictureInPicture => new[] { 94 },    // AUTO_PICTURE_IN_PICTURE
        SitePermissionKeys.SharedTabsControl => new[] { 97 },       // CAPTURED_SURFACE_CONTROL
        SitePermissionKeys.WebAppInstallations => new[] { 113 },    // WEB_APP_INSTALLATION
        SitePermissionKeys.LocalNetwork => new[] { 119, 127 },      // LOCAL_NETWORK_ACCESS, LOCAL_NETWORK
        SitePermissionKeys.AppsOnDevice => new[] { 128 },           // LOOPBACK_NETWORK
        _ => Array.Empty<int>()
    };

    /// <summary>Sets the choice for the site's bare and www origins (http and https). Default removes it.</summary>
    public static void Apply(CefRequestContext? context, string site, string key, SitePermissionValue value)
    {
        var types = TypesOf(key);
        if (types.Length == 0)
            return;
        var target = context ?? CefRequestContext.GetGlobalContext();
        var setting = value switch
        {
            SitePermissionValue.Allow => CefContentSettingValue.Allow,
            SitePermissionValue.Block => CefContentSettingValue.Block,
            _ => CefContentSettingValue.Default
        };
        // Content settings are changed on CEF's UI thread.
        CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            foreach (var url in new[] { $"https://{site}/", $"https://www.{site}/", $"http://{site}/", $"http://www.{site}/" })
            foreach (var type in types)
            {
                try
                {
                    target.setContentSetting(url, url, (CefContentSettingType)type, setting);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SitePrivacy] content setting {key} {url}: {ex.Message}");
                }
            }
        }));
    }

    /// <summary>Re-applies every saved choice (CEF keeps content settings only in memory here).</summary>
    public static void ApplyAll(SitePermissionStore store)
    {
        foreach (var (site, key, value) in store.All())
            Apply(null, site, key, value);
    }
}
