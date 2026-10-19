using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace NexZen.Desktop.Views;

/// <summary>The print dialog; Esc cancels, Enter prints, drag by the background.</summary>
public partial class PrintWindow : Window
{
    public PrintWindow()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CustomPages_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is NexZen.Application.ViewModels.PrintViewModel vm)
            vm.AllPages = false;
    }

    private void Background_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source.FindAncestorOfType<Button>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ComboBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<NumericUpDown>(includeSelf: true) != null
                                          || source.FindAncestorOfType<TextBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ToggleButton>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ScrollBar>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ItemsControl>(includeSelf: true) != null))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
