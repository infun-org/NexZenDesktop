using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Views;

public partial class BenchmarkWindow : Window
{
    private const string BenchmarkUrl = "https://browserbench.org/";

    private AvaloniaCefBrowser? _browser;

    public bool ForceClose { get; set; } = false;

    public BenchmarkWindow()
    {
        AvaloniaXamlLoader.Load(this);
        InitBrowser();
    }

    private void InitBrowser()
    {
        _browser = new AvaloniaCefBrowser
        {
            Address = BenchmarkUrl
        };

        // Unlock from default 30fps cap — must be set BEFORE the browser is created
        // (passed into CefBrowserHost.CreateBrowser via CefBrowserSettings).
        _browser.Settings.WindowlessFrameRate = 60;

        // Also call SetWindowlessFrameRate after init, in case the browser was
        // already created by the time Settings was written (e.g. on re-use).
        _browser.BrowserInitialized += () =>
        {
            try
            {
                _browser.GetBrowserHost()?.SetWindowlessFrameRate(60);
            }
            catch { /* best-effort */ }
        };

        _browser.AddressChanged += (_, url) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var label = this.FindControl<TextBlock>("UrlLabel");
                if (label != null) label.Text = url;
            });
        };

        var host = this.FindControl<Decorator>("BrowserHost");
        if (host != null)
            host.Child = _browser;
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        _browser?.Reload();
    }

    private void OnDevToolsClick(object? sender, RoutedEventArgs e)
    {
        _browser?.ShowDeveloperTools();
    }

    private void OnGpuInfoClick(object? sender, RoutedEventArgs e)
    {
        if (_browser != null)
            _browser.Address = "chrome://gpu";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ForceClose)
        {
            _browser?.Dispose();
            _browser = null;
        }
        base.OnClosing(e);
    }
}
