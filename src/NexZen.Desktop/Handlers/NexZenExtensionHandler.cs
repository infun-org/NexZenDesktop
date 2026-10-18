using System;
using Xilium.CefGlue;

namespace NexZen.Desktop.Handlers;

public sealed class NexZenExtensionHandler : CefExtensionHandler
{
    public event Action<CefExtension>? Loaded;
    public event Action<CefErrorCode>? Failed;
    public event Action<CefExtension>? Unloaded;

    protected override void OnExtensionLoaded(CefExtension extension) => Loaded?.Invoke(extension);

    protected override void OnExtensionLoadFailed(CefErrorCode result) => Failed?.Invoke(result);

    protected override void OnExtensionUnloaded(CefExtension extension) => Unloaded?.Invoke(extension);

    protected override bool OnBeforeBackgroundBrowser(CefExtension extension, string url, ref CefClient client, CefBrowserSettings settings)
        => false;

    protected override bool OnBeforeBrowser(CefExtension extension, CefBrowser browser, CefBrowser activeBrowser, int index, string url, bool active, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings)
        => false;
}
