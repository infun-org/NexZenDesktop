using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace NexZen.Desktop.Views;

/// <summary>Floating workspace window; hides like AiFunctionWindow when it loses focus.</summary>
public partial class WorkspaceWindow : Window
{
    public bool ForceClose { get; set; }
    /// <summary>Set while a card menu is open: its popup can take activation on X11.</summary>
    public bool SuppressHideOnDeactivate { get; set; }

    public WorkspaceWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
                Hide();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ForceClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void Window_Deactivated(object? sender, System.EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // A card menu or the rename box keeps focus inside; only hide when another window took it.
            if (ForceClose || !IsVisible || IsActive || SuppressHideOnDeactivate)
                return;
            Hide();
        }, DispatcherPriority.Input);
    }
}
