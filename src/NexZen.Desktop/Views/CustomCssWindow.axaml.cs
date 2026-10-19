using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Desktop.Controls;

namespace NexZen.Desktop.Views;

/// <summary>Pinned Custom CSS editor; Ctrl+S saves, Esc closes, drag by the background.</summary>
public partial class CustomCssWindow : Window
{
    public bool ForceClose { get; set; }

    public CustomCssWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<CssEditor>("Editor")?.FocusEditor(), DispatcherPriority.Background);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Hide();
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                 && DataContext is NexZen.Application.ViewModels.CustomCssViewModel vm)
        {
            e.Handled = true;
            vm.SaveCommand.Execute(null);
        }
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

    private void Background_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source.FindAncestorOfType<Button>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ToggleSwitch>(includeSelf: true) != null
                                          || source.FindAncestorOfType<CssEditor>(includeSelf: true) != null))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
