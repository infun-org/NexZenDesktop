using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NexZen.Desktop.Helpers;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Views;

public partial class CompanionStudioWindow : Window
{
    private AvaloniaCefBrowser? _browser;

    public bool ForceClose { get; set; }

    public event EventHandler? StudioClosed;

    public CompanionStudioWindow()
    {
        AvaloniaXamlLoader.Load(this);
        InitBrowser();
    }

    private void InitBrowser()
    {
        _browser = new AvaloniaCefBrowser
        {
            Address = BlankPage.InternalUrl + "?studio=1"
        };
        _browser.Settings.WindowlessFrameRate = 60;
        _browser.ContextMenuHandler = new SilentContextMenuHandler();
        _browser.ConsoleMessage += (_, message) =>
        {
            var text = message.Message ?? "";
            if (text.StartsWith("[Studio]", StringComparison.OrdinalIgnoreCase)
                && text.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Dispatcher.UIThread.Post(Dismiss);
            }
            NexZen.Desktop.Program.StartupLog.Write($"[CompanionStudio] {message.Level} line={message.Line}: {text}");
        };

        var host = this.FindControl<Decorator>("BrowserHost");
        if (host != null)
            host.Child = _browser;
    }

    public void Dismiss()
    {
        if (!IsVisible)
        {
            StudioClosed?.Invoke(this, EventArgs.Empty);
            return;
        }
        Hide();
        StudioClosed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            Dismiss();
            return;
        }

        var browser = _browser;
        _browser = null;
        var host = this.FindControl<Decorator>("BrowserHost");
        if (host != null && ReferenceEquals(host.Child, browser))
            host.Child = null;
        browser?.Dispose();
        base.OnClosing(e);
    }

    private sealed class SilentContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
        {
            model.Clear();
        }
    }
}
