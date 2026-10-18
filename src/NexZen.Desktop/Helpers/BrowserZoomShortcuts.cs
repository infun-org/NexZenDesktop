using System;
using Avalonia.Input;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Helpers;

internal static class BrowserZoomShortcuts
{
    public static bool TryHandle(KeyEventArgs e, AvaloniaCefBrowser? browser)
    {
        var modifiers = e.KeyModifiers;
        var primary = OperatingSystem.IsMacOS()
            ? modifiers.HasFlag(KeyModifiers.Meta) && !modifiers.HasFlag(KeyModifiers.Control)
            : modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Meta);

        if (!primary || modifiers.HasFlag(KeyModifiers.Alt) || browser == null)
            return false;

        CefZoomCommand command;
        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                command = CefZoomCommand.In;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                command = CefZoomCommand.Out;
                break;
            case Key.D0:
            case Key.NumPad0:
                if (modifiers.HasFlag(KeyModifiers.Shift))
                    return false;
                command = CefZoomCommand.Reset;
                break;
            default:
                return false;
        }

        e.Handled = true;
        CefRuntime.PostTask(CefThreadId.UI, new BrowserZoomTask(browser, command));
        return true;
    }
}

internal sealed class BrowserZoomTask : CefTask
{
    private AvaloniaCefBrowser? _browser;
    private readonly CefZoomCommand _command;

    public BrowserZoomTask(AvaloniaCefBrowser browser, CefZoomCommand command)
    {
        _browser = browser;
        _command = command;
    }

    protected override void Execute()
    {
        var browser = _browser;
        _browser = null;
        if (browser == null)
            return;

        var host = browser.GetBrowserHost();
        try
        {
            host?.Zoom(_command);
        }
        finally
        {
            host?.Dispose();
        }
    }
}
