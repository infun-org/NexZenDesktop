using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace NexZen.Desktop.Views;

/// <summary>"Site privacy" (Privacy and security) window; Esc closes, drag by the background.</summary>
public partial class SitePrivacyWindow : Window
{
    public SitePrivacyWindow()
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

    private void Background_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source.FindAncestorOfType<Button>(includeSelf: true) != null
                                          || source.FindAncestorOfType<TextBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ComboBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ListBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ScrollBar>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ScrollViewer>(includeSelf: true) != null))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }
}
