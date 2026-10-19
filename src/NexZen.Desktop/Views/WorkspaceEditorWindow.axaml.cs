using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Views;

/// <summary>
/// Pinned "Add new" workspace window. Unlike the workspace window it does not hide when it
/// loses focus, so the user can switch to the browser, copy a link and paste it here.
/// </summary>
public partial class WorkspaceEditorWindow : Window
{
    public bool ForceClose { get; set; }

    public WorkspaceEditorWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
                Hide();
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("NameBox")?.Focus(), DispatcherPriority.Background);
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

    // No title bar: drag the window by its background.
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source.FindAncestorOfType<Button>(includeSelf: true) != null
                                          || source.FindAncestorOfType<TextBox>(includeSelf: true) != null
                                          || source.FindAncestorOfType<ScrollViewer>(includeSelf: true) != null))
            return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void UrlBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WorkspaceEditorEntry entry } || DataContext is not WorkspaceEditorViewModel vm)
            return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.ConfirmEntryCommand.Execute(entry);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.RemoveEntryCommand.Execute(entry);
        }
    }

    // "Add URL" creates the row already in edit mode: focus its box, ready for Ctrl+V.
    private void UrlBox_Attached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox { DataContext: WorkspaceEditorEntry { IsEditing: true } } box)
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
    }
}
