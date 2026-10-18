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
        if (!TryNormalize(url, out var target) || App.ServiceProvider is not { } services)
            return false;

        // Each full browser window has its own tabs and browser state while
        // repositories and settings continue to point at the shared database.
        var independentViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(services, false);
        var window = new MainWindow(independentViewModel, target);
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
