using System;
using System.Threading.Tasks;
using Xilium.CefGlue;

namespace NexZen.Desktop.Helpers;

/// <summary>
/// Requested page size. The on-screen view uses the same CSS pixel size,
/// shrunk only when Fit (or a scale step) cannot place that box in the window.
/// </summary>
internal sealed class PageViewport
{
    public string PresetId { get; set; } = PageViewPresets.ResponsiveId;
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Mobile { get; set; }
    public string? UserAgent { get; set; }
    public bool Fit { get; set; } = true;
    public double Scale { get; set; } = 1;

    public PageViewport Clone()
    {
        return new PageViewport
        {
            PresetId = PresetId,
            Width = Width,
            Height = Height,
            Mobile = Mobile,
            UserAgent = UserAgent,
            Fit = Fit,
            Scale = Scale
        };
    }
}

internal readonly record struct PageViewPreset(
    string Id,
    string Label,
    int Width,
    int Height,
    bool Mobile,
    string? UserAgent);

internal static class PageViewPresets
{
    public const string DesktopId = "desktop";
    public const string ResponsiveId = "responsive";

    public const string AndroidUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Mobile Safari/537.36";

    private const string IphoneUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/128.0.0.0 Mobile/15E148 Safari/604.1";

    private const string IpadUserAgent =
        "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/128.0.0.0 Mobile/15E148 Safari/604.1";

    public static readonly PageViewPreset[] All =
    {
        new(DesktopId, "Desktop", 0, 0, false, null),
        new(ResponsiveId, "Responsive", 0, 0, false, null),
        new("iphone-se", "iPhone SE", 375, 667, true, IphoneUserAgent),
        new("iphone-14", "iPhone 14", 390, 844, true, IphoneUserAgent),
        new("pixel-7", "Pixel 7", 412, 915, true, AndroidUserAgent),
        new("ipad", "iPad", 820, 1180, true, IpadUserAgent),
        new("laptop", "Laptop", 1366, 768, false, null)
    };
}

internal static class PageViewportClient
{
    public static Task SubmitAsync(
        Func<CefBrowserHost?> hostFactory,
        PageViewport? viewport,
        int viewWidth,
        int viewHeight,
        double deviceScaleFactor)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            CefBrowserHost? host = null;
            try
            {
                host = hostFactory();
                if (host == null)
                    return;

                if (viewport == null)
                    Clear(host);
                else
                    Apply(host, viewport, viewWidth, viewHeight, deviceScaleFactor);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PageView] {ex.Message}");
            }
            finally
            {
                host?.Dispose();
                done.TrySetResult();
            }
        }));
        return done.Task;
    }

    private static void Apply(
        CefBrowserHost host,
        PageViewport viewport,
        int viewWidth,
        int viewHeight,
        double deviceScaleFactor)
    {
        // The Avalonia control is already the view size. dontSetVisibleSize keeps
        // CEF from fighting that offscreen view rectangle.
        var metrics = CefDictionaryValue.Create();
        metrics.SetInt("width", viewWidth);
        metrics.SetInt("height", viewHeight);
        metrics.SetDouble("deviceScaleFactor", deviceScaleFactor);
        metrics.SetBool("mobile", viewport.Mobile);
        metrics.SetDouble("scale", 1);
        metrics.SetInt("screenWidth", viewWidth);
        metrics.SetInt("screenHeight", viewHeight);
        metrics.SetBool("dontSetVisibleSize", true);

        var orientation = CefDictionaryValue.Create();
        bool portrait = viewHeight >= viewWidth;
        orientation.SetString("type", portrait ? "portraitPrimary" : "landscapePrimary");
        orientation.SetInt("angle", portrait ? 0 : 90);
        metrics.SetDictionary("screenOrientation", orientation);
        Invoke(host, "Emulation.setDeviceMetricsOverride", metrics);

        var touch = CefDictionaryValue.Create();
        touch.SetBool("enabled", viewport.Mobile);
        touch.SetInt("maxTouchPoints", viewport.Mobile ? 5 : 1);
        Invoke(host, "Emulation.setTouchEmulationEnabled", touch);

        var agent = CefDictionaryValue.Create();
        agent.SetString("userAgent", viewport.UserAgent ?? "");
        Invoke(host, "Emulation.setUserAgentOverride", agent);

        Console.WriteLine($"[PageView] {viewWidth}x{viewHeight} mobile={viewport.Mobile} dsf={deviceScaleFactor:0.00}");
    }

    private static void Clear(CefBrowserHost host)
    {
        Invoke(host, "Emulation.clearDeviceMetricsOverride", CefDictionaryValue.Create());

        var touch = CefDictionaryValue.Create();
        touch.SetBool("enabled", false);
        Invoke(host, "Emulation.setTouchEmulationEnabled", touch);

        var agent = CefDictionaryValue.Create();
        agent.SetString("userAgent", "");
        Invoke(host, "Emulation.setUserAgentOverride", agent);

        Console.WriteLine("[PageView] cleared");
    }

    private static void Invoke(CefBrowserHost host, string method, CefDictionaryValue parameters)
    {
        try
        {
            host.ExecuteDevToolsMethod(0, method, parameters);
        }
        finally
        {
            parameters.Dispose();
        }
    }
}
