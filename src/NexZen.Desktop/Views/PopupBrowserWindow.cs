using System;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Views;

/// <summary>
/// Creates an independent full NexZen browser window for the existing
/// Open-in-new-window call sites.
/// </summary>
public static class PopupBrowserWindow
{
    private static int _stagger;

    public static bool Open(Window? owner, string? url, MainViewModel? viewModel)
    {
        if (App.ServiceProvider is not { } services)
            return false;
        // Each full browser window has its own tabs and browser state while
        // repositories and settings continue to point at the shared database.
        var independentViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services, false);
        return Open(owner, url, independentViewModel, out _);
    }

    private static bool Open(Window? owner, string? url, MainViewModel independentViewModel, out MainWindow window)
    {
        window = null!;
        if (!TryNormalize(url, out var target))
            return false;
        window = new MainWindow(independentViewModel, target);
        if (owner != null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var step = _stagger++ % 8;
            window.Position = new PixelPoint(owner.Position.X + 40 + step * 28, owner.Position.Y + 40 + step * 28);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Show();
        window.Activate();
        return true;
    }

    /// <summary>
    /// Opens one new window holding all of these URLs as tabs, the first one active.
    /// A workspace name is shown first in that window's title.
    /// </summary>
    public static MainWindow? OpenMany(Window? owner, System.Collections.Generic.IReadOnlyList<string> urls, string? workspaceName = null)
    {
        var targets = new System.Collections.Generic.List<string>();
        foreach (var url in urls)
        {
            if (TryNormalize(url, out var target))
                targets.Add(target);
        }
        if (targets.Count == 0 || App.ServiceProvider is not { } services)
            return null;

        var viewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services, false);
        if (!Open(owner, targets[0], viewModel, out var window))
            return null;
        window.WorkspaceName = workspaceName;
        var firstTabId = viewModel.ActiveTab?.Id;
        for (var i = 1; i < targets.Count; i++)
            viewModel.AddTab(targets[i]);
        if (firstTabId != null)
            viewModel.SwitchTab(firstTabId);
        window.Activate();
        return window;
    }

    public static bool CanOpen(string? url) => TryNormalize(url, out _);

    public static bool TryNormalize(string? url, out string target)
    {
        target = (url ?? string.Empty).Trim();
        if (target.Length == 0
            || target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (target.StartsWith("//", StringComparison.Ordinal))
            target = "https:" + target;
        return true;
    }
}
