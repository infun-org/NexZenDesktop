using System;
using Avalonia;
using Avalonia.Input;
using Xilium.CefGlue;

namespace NexZen.Desktop;

/// <summary>
/// DevTools for the active tab. CEF 152 only shows DevTools in their own (Chrome style)
/// window — windowless DevTools are not supported — so the window is opened beside this one,
/// on the right when there is room, otherwise over the right part of it.
/// </summary>
public partial class MainWindow
{
    private const double DevToolsPreferredWidth = 760;
    private const double DevToolsMinWidth = 480;

    // F12, Ctrl+Shift+J, and Cmd+Option+I (macOS) toggle DevTools for the active tab.
    // Ctrl+Shift+I is AI Function, so it is not used here.
    internal static bool IsDevToolsChord(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (e.Key == Key.F12 && mods == KeyModifiers.None)
            return true;
        if (e.Key == Key.J && mods == (KeyModifiers.Control | KeyModifiers.Shift))
            return true;
        return e.Key == Key.I && mods == (KeyModifiers.Meta | KeyModifiers.Alt);
    }

    private void OnDevToolsKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsDevToolsChord(e))
            return;
        e.Handled = true;
        CloseSuperMenu();
        var page = GetActiveTabState()?.Browser;
        if (page == null)
            return;
        if (page.HasDeveloperTools)
            page.CloseDeveloperTools();
        else
            OpenDevTools(0, 0);
    }

    /// <summary>
    /// Opens DevTools for the active tab beside this window, inspecting the element at the point.
    /// When they are open already, CEF focuses them and inspects the new element.
    /// </summary>
    private void OpenDevTools(int inspectX, int inspectY)
    {
        var page = GetActiveTabState()?.Browser;
        if (page == null)
            return;
        if (DevToolsBounds() is { } bounds)
            page.ShowDeveloperTools(inspectX, inspectY, bounds);
        else
            page.ShowDeveloperTools(inspectX, inspectY);
    }

    /// <summary>Where the DevTools window goes, in screen pixels; null when the screen is unknown.</summary>
    private CefRectangle? DevToolsBounds()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
            return null;
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var area = screen.WorkingArea;
        var x = Position.X;
        var y = Position.Y;
        var width = (int)Math.Round(Bounds.Width * scale);
        var height = (int)Math.Round(Bounds.Height * scale);
        var preferred = (int)Math.Round(DevToolsPreferredWidth * scale);
        var minimum = (int)Math.Round(DevToolsMinWidth * scale);

        var rightRoom = area.Right - (x + width);
        int left, devWidth;
        if (rightRoom >= minimum)
        {
            // Room beside the window: dock to its right edge.
            devWidth = Math.Min(preferred, rightRoom);
            left = x + width;
        }
        else
        {
            // Window fills the screen: cover its right part, like a docked panel.
            devWidth = Math.Min(preferred, Math.Max(minimum, width / 2));
            left = Math.Max(area.X, x + width - devWidth);
        }
        var top = Math.Max(area.Y, y);
        var devHeight = Math.Min(height, area.Bottom - top);
        return new CefRectangle(left, top, devWidth, Math.Max(200, devHeight));
    }
}
