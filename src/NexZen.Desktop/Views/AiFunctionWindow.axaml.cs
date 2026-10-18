using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace NexZen.Desktop.Views;

public partial class AiFunctionWindow : Window
{
    public bool ForceClose { get; set; }
    public bool SuppressHideOnDeactivate { get; set; }

    public AiFunctionWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
            if (ForceClose || !IsVisible)
                return;
            if (IsActive)
                return;
            if (SuppressHideOnDeactivate)
                return;
            Hide();
        }, DispatcherPriority.Input);
    }
}
