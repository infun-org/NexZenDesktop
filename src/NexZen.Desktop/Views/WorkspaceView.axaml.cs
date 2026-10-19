using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    // A click on the card itself opens the workspace; clicks on its tab rows
    // and menu button are handled by those controls.
    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Control { DataContext: WorkspaceCardViewModel card })
            return;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) != null)
            return;
        ViewModel?.OpenCommand.Execute(card);
    }

    private void AddNew_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
            ViewModel?.AddNewCommand.Execute(null);
    }

    private void CardMenu_Opened(object? sender, System.EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is WorkspaceWindow window)
            window.SuppressHideOnDeactivate = true;
    }

    private void CardMenu_Closed(object? sender, System.EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not WorkspaceWindow window)
            return;
        // Let the click that closed the menu land first; then the normal rule applies again.
        Dispatcher.UIThread.Post(() => window.SuppressHideOnDeactivate = false, DispatcherPriority.Background);
    }
}
