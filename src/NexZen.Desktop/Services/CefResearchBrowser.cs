using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using NexZen.Application.Services;
using NexZen.Core.AI;
using NexZen.Desktop.Helpers;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Services;

public sealed class CefResearchBrowser : IBrowserResearch, IDisposable
{
    private static readonly MethodInfo? AddRefMethod = typeof(CefPreferenceManager).GetMethod(
        "AddRef",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public CefResearchBrowser(IWebLookup? webLookup = null)
    {
        _webLookup = webLookup;
    }

    private readonly IWebLookup? _webLookup;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _navLock = new();
    private Border? _host;
    private AvaloniaCefBrowser? _browser;
    private CefRequestContext? _context;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<bool>? _loaded;
    private int _nav;

    public void Attach(Border host)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Attach(host));
            return;
        }

        if (_host != null && !ReferenceEquals(_host, host) && _browser != null)
        {
            _host.Child = null;
            _host.IsVisible = false;
        }

        _host = host;
        host.IsVisible = false;
        if (_browser != null)
        {
            host.Child = _browser;
            return;
        }

        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context = CefRequestContext.CreateContext(new CefRequestContextSettings
        {
            CachePath = string.Empty,
            PersistSessionCookies = false
        }, null);
        var browser = new AvaloniaCefBrowser(BorrowContext) { Address = "about:blank" };
        browser.Settings.WindowlessFrameRate = 5;
        browser.ContextMenuHandler = new SilentMenu();
        browser.LifeSpanHandler = new BlockPopups();
        browser.BrowserInitialized += () =>
        {
            TabAudio.Apply(browser, true);
            _ready?.TrySetResult(true);
        };
        browser.LoadEnd += (_, e) =>
        {
            if (e.Frame.IsMain)
                _loaded?.TrySetResult(true);
        };
        browser.LoadError += (_, e) =>
        {
            if (e.Frame is { IsMain: true })
                _loaded?.TrySetResult(false);
        };
        _browser = browser;
        host.Child = browser;
    }

    public async Task<WebLookupResult> ResearchAsync(
        WebLookupRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var query = WebLookupParser.Clip((request.Query ?? "").Trim(), 180);
        if (query.Length == 0)
            return new WebLookupResult("", "", Array.Empty<WebLookupHit>(), "Empty query");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Log("start");
            // The off-screen browser is only created once its control has a size, and the
            // host is collapsed while idle; show it (transparent) before waiting for init.
            await ShowHostAsync();
            await EnsureReadyAsync(cancellationToken);
            Log("browser ready");
            var engine = string.IsNullOrWhiteSpace(request.SearchEngine) ? "Google" : request.SearchEngine;
            var searchQuery = query;
            if (!SearchTargets.IsVietnamese(request.Language) && WebLookupParser.LooksVietnamese(query))
                searchQuery = SerpParser.RewriteQuery(query);
            var serpUrl = SearchTargets.ResearchUrl(engine, searchQuery, request.Language);
            progress?.Report("Searching " + SearchTargets.DisplayName(engine) + "…");
            await NavigateAsync(serpUrl, TimeSpan.FromSeconds(16), cancellationToken);
            await DismissConsentIfNeededAsync(cancellationToken);
            var serp = SerpParser.RankHits(await WaitForSerpAsync(cancellationToken));
            var toRead = SerpParser.PickPages(serp, SerpParser.MaxPageAttempts);
            Log($"{SearchTargets.DisplayName(engine)}: {serp.Count} results, {toRead.Count} readable");
            // Results with no link we can open (e.g. a SERP layout we do not parse) are as good as none.
            if (toRead.Count == 0)
            {
                progress?.Report("Searching DuckDuckGo…");
                await NavigateAsync(SearchTargets.ResearchUrl("DuckDuckGo", searchQuery, request.Language), TimeSpan.FromSeconds(16), cancellationToken);
                var ddg = SerpParser.RankHits(await WaitForSerpAsync(cancellationToken));
                var ddgRead = SerpParser.PickPages(ddg, SerpParser.MaxPageAttempts);
                Log($"DuckDuckGo: {ddg.Count} results, {ddgRead.Count} readable");
                if (ddgRead.Count > 0 || serp.Count == 0)
                {
                    serp = ddg;
                    toRead = ddgRead;
                }
            }

            var pages = new List<WebLookupHit>();
            // Pages already opened, so a redirect or a Google Translate copy of one is not read twice.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TryAddOpenTab(request, pages))
            {
                seen.Add(SerpParser.PageKey(pages[0].Url));
                Log($"open tab kept: {SerpParser.HostLabel(pages[0].Url)} ({pages[0].Snippet.Length} chars)");
            }
            foreach (var hit in toRead)
            {
                if (pages.Count >= SerpParser.MaxPages)
                    break;
                cancellationToken.ThrowIfCancellationRequested();
                var label = SerpParser.HostLabel(hit);
                progress?.Report("Reading " + label + "…");
                var (page, skip) = await ReadPageAsync(hit, query, seen, cancellationToken);
                // A page that never mentions what was asked (e.g. only FAQ headings) would
                // block the other sources, so try the next result instead.
                if (skip != null)
                    Log($"skip {label}: {skip}");
                else if (page == null)
                    Log($"skip {label}: no readable text");
                else if (!PageFocus.LooksRelevant(page.Snippet, query))
                    Log($"skip {SerpParser.HostLabel(page.Url)}: does not mention the question ({page.Snippet.Length} chars)");
                else
                {
                    Log($"keep {SerpParser.HostLabel(page.Url)} ({page.Snippet.Length} chars)");
                    pages.Add(page);
                }
            }

            Log($"{pages.Count} page(s) for the model");
            var hits = WebLookupParser.Combine(pages, serp);
            if (hits.Count == 0)
                return new WebLookupResult(query, "", Array.Empty<WebLookupHit>(), "Search returned no results.");
            return new WebLookupResult(query, "", hits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("stopped");
            throw;
        }
        catch (Exception ex)
        {
            // The caller falls back to the plain web lookup without saying why.
            Log($"failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            HideHost();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess())
            DisposeCore();
        else
            Dispatcher.UIThread.Post(DisposeCore);
    }

    private void DisposeCore()
    {
        if (_host != null)
            _host.Child = null;
        _browser?.Dispose();
        _browser = null;
        _context?.Dispose();
        _context = null;
        _gate.Dispose();
    }

    private CefRequestContext BorrowContext()
    {
        var context = _context ?? throw new InvalidOperationException("Research context is missing.");
        AddRefMethod?.Invoke(context, null);
        return context;
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_browser == null || _host == null)
            throw new InvalidOperationException("Research browser is not attached.");
        var ready = _ready;
        if (ready == null)
            return;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken);
    }

    private async Task NavigateAsync(string url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> loaded = null!;
        await OnUiAsync(() =>
        {
            lock (_navLock)
            {
                _nav++;
                loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _loaded = loaded;
            }

            var browser = _browser ?? throw new InvalidOperationException("Research browser is not attached.");
            if (string.Equals(browser.Address, url, StringComparison.OrdinalIgnoreCase))
            {
                loaded.TrySetResult(true);
                return;
            }

            browser.Address = url;
        });
        try
        {
            await loaded.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task DismissConsentIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_browser == null)
            return;
        string? url = null;
        string? title = null;
        await OnUiAsync(() =>
        {
            url = _browser.Address;
            title = _browser.Title;
        });
        if (!SerpParser.LooksLikeConsent(url, title))
            return;
        try
        {
            await EvalAsync(SerpExtractScript.DismissConsent, TimeSpan.FromSeconds(4));
        }
        catch
        {
            return;
        }

        await NavigateWaitQuietAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private async Task NavigateWaitQuietAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> loaded = null!;
        await OnUiAsync(() =>
        {
            lock (_navLock)
            {
                _nav++;
                loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _loaded = loaded;
            }
        });
        try
        {
            await loaded.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task<IReadOnlyList<WebLookupHit>> WaitForSerpAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WebLookupHit> hits = Array.Empty<WebLookupHit>();
        for (var i = 0; i < 8; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hits = await ExtractSerpAsync();
            if (hits.Count >= 3)
                return hits;
            if (i == 0)
                await TryDismissOverlayAsync();
            await Task.Delay(450, cancellationToken);
        }

        return hits;
    }

    private async Task TryDismissOverlayAsync()
    {
        try
        {
            await EvalAsync(SerpExtractScript.DismissConsent, TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
    }

    private async Task<IReadOnlyList<WebLookupHit>> ExtractSerpAsync()
    {
        try
        {
            var json = await EvalAsync(SerpExtractScript.Extract, TimeSpan.FromSeconds(8));
            var fromJson = SerpParser.ParseJson(json);
            if (fromJson.Count > 0)
                return fromJson;
            return SerpParser.ParseHtml(json);
        }
        catch
        {
            return Array.Empty<WebLookupHit>();
        }
    }

    /// <returns>The page, or a reason it was skipped without reading.</returns>
    private async Task<(WebLookupHit? Page, string? Skip)> ReadPageAsync(
        WebLookupHit hit, string query, ISet<string> seen, CancellationToken cancellationToken)
    {
        WebLookupHit? extracted = null;
        var landed = hit.Url;
        try
        {
            var redirect = SerpParser.IsRedirect(hit.Url);
            if (!redirect && !seen.Add(SerpParser.PageKey(hit.Url)))
                return (null, "same page as one already read");
            await NavigateAsync(hit.Url, TimeSpan.FromSeconds(16), cancellationToken);
            // A search redirect (Google /goto) only tells us the real page once it has loaded.
            if (redirect)
            {
                landed = await WaitForRedirectAsync(cancellationToken);
                if (SerpParser.IsSearchHost(landed))
                    return (null, $"redirect stayed on {SerpParser.HostLabel(landed)}");
                Log($"redirect landed on {SerpParser.HostLabel(landed)}");
                // Google may send us to its machine-translated copy (*.translate.goog).
                var original = SerpParser.FromTranslateProxy(landed);
                if (!seen.Add(SerpParser.PageKey(original)))
                    return (null, $"same page as one already read ({SerpParser.HostLabel(original)})");
                if (!string.Equals(original, landed, StringComparison.Ordinal))
                {
                    Log($"translated copy, reading the original {SerpParser.HostLabel(original)}");
                    await NavigateAsync(original, TimeSpan.FromSeconds(16), cancellationToken);
                    landed = original;
                }
                hit = hit with { Url = landed };
            }
            for (var i = 0; i < 4 && extracted == null; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                extracted = await TryExtractPageAsync(hit, query);
                if (extracted != null)
                    break;
                await Task.Delay(400, cancellationToken);
            }

            if (extracted != null && PageFocus.LooksRelevant(extracted.Snippet, query))
                return (extracted, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        // The rendered text was missing or off-topic; the static HTML may still hold it.
        if (_webLookup == null)
            return (extracted, null);
        try
        {
            var article = await _webLookup.ReadArticleAsync(landed, hit.Title, cancellationToken, query);
            if (article != null && (extracted == null || PageFocus.LooksRelevant(article.Snippet, query)))
                return (article, null);
            return (extracted, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (extracted, null);
        }
    }

    private async Task<string> WaitForRedirectAsync(CancellationToken cancellationToken)
    {
        var address = "";
        for (var i = 0; i < 20; i++)
        {
            await OnUiAsync(() => address = _browser?.Address ?? "");
            if (!SerpParser.IsSearchHost(address))
            {
                // Left the search host after the first load finished (script redirect):
                // let the real page load before reading it.
                if (i > 0)
                    await NavigateWaitQuietAsync(TimeSpan.FromSeconds(8), cancellationToken);
                break;
            }

            await Task.Delay(400, cancellationToken);
        }

        return address;
    }

    private static readonly object LogLock = new();

    // Site names, sizes and reasons only; never page text.
    private static void Log(string message)
    {
        var line = "[Research] " + message;
        Console.WriteLine(line);
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZenDesktop");
            Directory.CreateDirectory(dir);
            lock (LogLock)
                File.AppendAllText(Path.Combine(dir, "research.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch (Exception)
        {
            // Logging must never break research.
        }
    }

    private async Task<WebLookupHit?> TryExtractPageAsync(WebLookupHit hit, string query)
    {
        if (_browser == null)
            return null;
        var json = await EvalAsync(PageContentExtractScript.Script, TimeSpan.FromSeconds(8));
        var page = PageContentBuilder.FromExtractJson(json, PageFocus.MaxSourceChars);
        var body = PageFocus.Focus(
            page.Segments.Select(s => s.Text).Where(t => t.Length > 0).ToList(),
            query,
            WebLookupParser.MaxPageChars);
        if (body.Length < 80)
            return null;
        var title = string.IsNullOrWhiteSpace(page.Title) ? hit.Title : page.Title;
        var url = string.IsNullOrWhiteSpace(page.Url) ? hit.Url : page.Url;
        return new WebLookupHit(title, url, body, "Page");
    }

    private static bool TryAddOpenTab(WebLookupRequest request, List<WebLookupHit> pages)
    {
        if (string.IsNullOrWhiteSpace(request.OpenTabExtractJson))
            return false;
        if (!SerpParser.QueryLooksRelated(request.Query, request.OpenTabTitle, request.OpenTabUrl))
            return false;
        var page = PageContentBuilder.FromExtractJson(request.OpenTabExtractJson, PageFocus.MaxSourceChars);
        var body = PageFocus.Focus(
            page.Segments.Select(s => s.Text).Where(t => t.Length > 0).ToList(),
            request.Query,
            WebLookupParser.MaxPageChars);
        if (body.Length < 40)
            return false;
        var title = string.IsNullOrWhiteSpace(page.Title) ? (request.OpenTabTitle ?? "Open tab") : page.Title;
        var url = string.IsNullOrWhiteSpace(page.Url) ? (request.OpenTabUrl ?? "") : page.Url;
        pages.Add(new WebLookupHit(title, url, body, "Page"));
        return true;
    }

    private async Task ShowHostAsync()
    {
        await OnUiAsync(() =>
        {
            if (_host == null)
                return;
            _host.Opacity = 0;
            _host.IsHitTestVisible = false;
            _host.IsVisible = true;
            if (_host.Bounds.Width < 800 || _host.Bounds.Height < 600)
            {
                _host.Width = 1024;
                _host.Height = 768;
            }

            if (_browser != null)
                _browser.IsVisible = true;
        });
        await Task.Delay(120);
    }

    private void HideHost()
    {
        void Run()
        {
            if (_host == null)
                return;
            _host.IsVisible = false;
            if (_browser != null)
                _browser.IsVisible = false;
        }

        if (Dispatcher.UIThread.CheckAccess())
            Run();
        else
            Dispatcher.UIThread.Post(Run);
    }

    private async Task<string?> EvalAsync(string script, TimeSpan timeout)
    {
        var browser = _browser;
        if (browser == null)
            return null;
        if (Dispatcher.UIThread.CheckAccess())
            return await browser.EvaluateJavaScript<string>(script, url: browser.Address ?? "", timeout: timeout);
        return await Dispatcher.UIThread.InvokeAsync(
            () => browser.EvaluateJavaScript<string>(script, url: browser.Address ?? "", timeout: timeout));
    }

    private static async Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private sealed class SilentMenu : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(
            CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
            => model.Clear();

        protected override bool OnContextMenuCommand(
            CefBrowser browser, CefFrame frame, CefContextMenuParams state, int commandId, CefEventFlags eventFlags)
            => true;

        protected override bool RunContextMenu(
            CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model,
            CefRunContextMenuCallback callback)
        {
            callback.Cancel();
            return true;
        }
    }

    private sealed class BlockPopups : LifeSpanHandler
    {
        protected override bool OnBeforePopup(
            CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName,
            CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures,
            CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings,
            ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
            => true;
    }
}
