using System;
using Xilium.CefGlue;

namespace NexZen.Desktop.Handlers;

public class CustomDisplayHandler : Xilium.CefGlue.Common.Handlers.DisplayHandler
{
    private readonly Action<double> _onProgressChanged;
    private readonly Action<bool> _onFullscreenChanged;
    private readonly Action<CefBrowser, string[]>? _onFaviconUrlsChanged;

    public CustomDisplayHandler(Action<double> onProgressChanged, Action<bool> onFullscreenChanged,
        Action<CefBrowser, string[]>? onFaviconUrlsChanged = null)
    {
        _onProgressChanged = onProgressChanged;
        _onFullscreenChanged = onFullscreenChanged;
        _onFaviconUrlsChanged = onFaviconUrlsChanged;
    }

    protected override void OnLoadingProgressChange(CefBrowser browser, double progress)
    {
        base.OnLoadingProgressChange(browser, progress);
        _onProgressChanged?.Invoke(progress);
    }

    protected override void OnFullscreenModeChange(CefBrowser browser, bool fullscreen)
    {
        base.OnFullscreenModeChange(browser, fullscreen);
        _onFullscreenChanged?.Invoke(fullscreen);
    }

    protected override void OnFaviconUrlChange(CefBrowser browser, string[] iconUrls)
    {
        base.OnFaviconUrlChange(browser, iconUrls);
        _onFaviconUrlsChanged?.Invoke(browser, iconUrls);
    }
}
