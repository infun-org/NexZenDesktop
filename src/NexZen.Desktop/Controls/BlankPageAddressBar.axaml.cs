using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Controls;

/// <summary>
/// The address bar drawn over a new tab (about:blank): domain lock, address/search box with
/// suggestions, bookmark star and search engine picker, like the SuperMenu address bar.
/// </summary>
public partial class BlankPageAddressBar : UserControl
{
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler? LockDomainRequested;

    private CancellationTokenSource? _searchCts;
    private int _selectedSuggestionIndex = -1;

    public BlankPageAddressBar()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnSuggestionNavigationKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusAddressBar()
    {
        var textBox = this.FindControl<TextBox>("UrlTextBox");
        if (textBox == null)
            return;
        textBox.Focus();
        textBox.SelectAll();
    }

    public void HideSuggestions()
    {
        _searchCts?.Cancel();
        _selectedSuggestionIndex = -1;
        var border = this.FindControl<Border>("SuggestionBorder");
        if (border != null)
            border.IsVisible = false;
        // The list is shared with the SuperMenu; do not leave it showing there.
        if (DataContext is MainViewModel vm)
            vm.IsSearchSuggestionVisible = false;
    }

    private void LockDomain_Click(object? sender, RoutedEventArgs e)
        => LockDomainRequested?.Invoke(this, EventArgs.Empty);

    private void AddressBar_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
            Dispatcher.UIThread.Post(textBox.SelectAll, DispatcherPriority.Background);
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideSuggestions();
            return;
        }
        if (e.Key != Key.Enter || sender is not TextBox textBox || string.IsNullOrWhiteSpace(textBox.Text))
            return;
        var engine = DataContext is MainViewModel vm ? vm.SelectedSearchEngine : "Google";
        Navigate(SearchTargets.FromAddressBar(engine, textBox.Text));
    }

    private void OnSuggestionNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !SuggestionsShown())
            return;
        var count = vm.SearchSuggestions.Count;
        if (count == 0)
            return;

        if (e.Key is Key.Down or Key.Up)
        {
            if (_selectedSuggestionIndex < 0)
                _selectedSuggestionIndex = e.Key == Key.Down ? 0 : count - 1;
            else
                _selectedSuggestionIndex = (_selectedSuggestionIndex + (e.Key == Key.Down ? 1 : count - 1)) % count;
            UpdateSuggestionSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < count)
        {
            Navigate(SearchTargets.FromAddressBar(vm.SelectedSearchEngine, vm.SearchSuggestions[_selectedSuggestionIndex]));
            e.Handled = true;
        }
    }

    private async void AddressBar_KeyUp(object? sender, KeyEventArgs e)
    {
        // Arrow and Enter key-ups belong to suggestion navigation, not edits.
        if (e.Key is Key.Enter or Key.Escape or Key.Up or Key.Down)
            return;
        if (sender is not TextBox textBox || DataContext is not MainViewModel vm)
            return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _selectedSuggestionIndex = -1;
        var query = textBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            HideSuggestions();
            return;
        }

        try
        {
            await Task.Delay(300, token);
            await vm.FetchSearchSuggestionsAsync(query, token);
            var border = this.FindControl<Border>("SuggestionBorder");
            if (border != null && !token.IsCancellationRequested)
                border.IsVisible = vm.SearchSuggestions.Count > 0;
            Dispatcher.UIThread.Post(UpdateSuggestionSelectionVisuals, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void SuggestionItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string suggestion } && DataContext is MainViewModel vm)
            Navigate(SearchTargets.FromAddressBar(vm.SelectedSearchEngine, suggestion));
    }

    private void Navigate(string url)
    {
        HideSuggestions();
        NavigateRequested?.Invoke(this, url);
    }

    private bool SuggestionsShown() => this.FindControl<Border>("SuggestionBorder")?.IsVisible == true;

    private void UpdateSuggestionSelectionVisuals()
    {
        var buttons = this.FindControl<ItemsControl>("SuggestionItems")?
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Tag is string)
            .ToList();
        if (buttons == null)
            return;
        for (var i = 0; i < buttons.Count; i++)
        {
            if (i == _selectedSuggestionIndex)
                buttons[i].Classes.Add("suggestion-selected");
            else
                buttons[i].Classes.Remove("suggestion-selected");
        }
    }
}
