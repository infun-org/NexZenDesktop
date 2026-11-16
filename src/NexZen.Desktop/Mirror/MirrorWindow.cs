using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NexZen.Core.NexShare.Interfaces;

namespace NexZen.Desktop.Mirror;

/// <summary>NexZen Mirror in its own window (the default); "⇔" moves it beside the web page.</summary>
public sealed class MirrorWindow : Window, IMirrorHost
{
    public MirrorView View { get; }
    private bool _handingOver;
    private bool _sized;

    public MirrorWindow(INexShareOrchestrator orchestrator, INexShareCrypto crypto)
        : this(new MirrorView(orchestrator, crypto), start: true)
    {
    }

    /// <summary>Hosts <paramref name="view"/>; <paramref name="start"/> = a new session (false when it moves here).</summary>
    public MirrorWindow(MirrorView view, bool start)
    {
        View = view;
        Title = view.TitleText;
        Width = 420;
        Height = 860;
        MinWidth = 240;
        MinHeight = 320;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = view;
        view.Host = this;
        Activated += (_, _) => view.FocusTyping();
        if (start)
            Opened += async (_, _) => await view.StartAsync();
    }

    public bool IsDocked => false;

    public void SetTitle(string title) => Title = title;

    public void CloseMirror() => Close();

    public void ToggleDock()
    {
        var target = MainWindow.LastBrowserWindow();
        if (target == null) return;
        _handingOver = true;
        Content = null; // the view keeps running in the browser window
        target.DockMirror(View);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_handingOver)
            View.Shutdown();
    }

    public void FitVideo(int width, int height)
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var scale = screen.Scaling;
        var maxH = screen.WorkingArea.Height / scale * 0.88;
        var maxW = screen.WorkingArea.Width / scale * 0.88;
        var f = Math.Min(maxH / height, maxW / width);
        var newW = Math.Round(width * f);
        var newH = Math.Round(height * f);
        if (!_sized || Math.Abs(Width / Height - newW / newH) > 0.01)
        {
            Width = newW;
            Height = newH + 34; // bottom bar
            _sized = true;
        }
    }
}
