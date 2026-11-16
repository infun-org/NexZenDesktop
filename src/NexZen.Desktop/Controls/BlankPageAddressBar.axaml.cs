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
using Avalonia.Controls.Primitives;

namespace NexZen.Desktop.Controls;

/// <summary>
/// The address bar drawn over a new tab (about:blank): domain lock, address/search box with
/// suggestions, bookmark star and search engine picker, like the SuperMenu address bar.
/// </summary>
public partial class BlankPageAddressBar : UserControl
{
    public event EventHandler<string>? NavigateRequested;
    /// <summary>A shortcut opened with Ctrl+click or the middle button.</summary>
    public event EventHandler<string>? OpenInNewTabRequested;

    private static readonly Lazy<QuickLinkStore> Links = new(QuickLinkStore.ForUser);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> IconFetches = new();
    public event EventHandler? LockDomainRequested;

    private CancellationTokenSource? _searchCts;
    private int _selectedSuggestionIndex = -1;

    public BlankPageAddressBar()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnSuggestionNavigationKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            Links.Value.Changed += OnLinksChanged;
            RebuildQuickLinks();
        };
        DetachedFromVisualTree += (_, _) => Links.Value.Changed -= OnLinksChanged;
    }

    // ---- shortcuts ------------------------------------------------------------------------------

    private void OnLinksChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RebuildQuickLinks);

    private void RebuildQuickLinks()
    {
        if (this.FindControl<WrapPanel>("QuickLinksPanel") is not { } panel)
            return;
        panel.Children.Clear();
        var links = Links.Value.All;
        foreach (var link in links)
        {
            panel.Children.Add(LinkTile(link));
            if (link.Icon == null)
                FetchIcon(link.Url);
        }
        if (links.Count < QuickLinkStore.MaxLinks)
            panel.Children.Add(AddTile());
    }

    private static Avalonia.Media.Imaging.Bitmap? IconBitmap(string? dataUrl)
    {
        var comma = dataUrl?.IndexOf(',') ?? -1;
        if (dataUrl == null || comma < 0 || !dataUrl.Contains(";base64", StringComparison.Ordinal))
            return null;
        try
        {
            using var stream = new System.IO.MemoryStream(Convert.FromBase64String(dataUrl[(comma + 1)..]));
            return new Avalonia.Media.Imaging.Bitmap(stream);
        }
        catch
        {
            return null; // e.g. SVG icons: the letter is shown instead
        }
    }

    private static void FetchIcon(string url)
    {
        if (!IconFetches.TryAdd(url, 0))
            return;
        if (App.ServiceProvider?.GetService(typeof(NexZen.Core.Interfaces.IFaviconFetcher)) is not NexZen.Core.Interfaces.IFaviconFetcher fetcher)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var icon = await fetcher.FetchDataUrlAsync(url, cts.Token);
                if (icon != null) Links.Value.SetIcon(url, icon);
            }
            catch { }
        });
    }

    private static Avalonia.Media.IBrush LetterBrush(string text)
    {
        var palette = new[] { "#5B8DEF", "#E8710A", "#1E8E3E", "#D93025", "#9334E6", "#12A4AF", "#F9AB00", "#E52592" };
        var hash = 0;
        foreach (var c in text) hash = (hash * 31 + c) & 0x7fffffff;
        return Avalonia.Media.Brush.Parse(palette[hash % palette.Length]);
    }

    private Control TileBody(Control icon, string title)
    {
        return new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 48, Height = 48, CornerRadius = new Avalonia.CornerRadius(24),
                    Background = Avalonia.Media.Brush.Parse("#F1F3F4"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Child = icon,
                },
                new TextBlock
                {
                    Text = title, FontSize = 12, Foreground = Avalonia.Media.Brush.Parse("#333333"), MaxWidth = 72,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                },
            },
        };
    }

    private Control LinkTile(QuickLink link)
    {
        Control icon;
        if (IconBitmap(link.Icon) is { } bitmap)
        {
            icon = new Image { Source = bitmap, Width = 24, Height = 24 };
        }
        else
        {
            var letter = string.IsNullOrEmpty(link.Title) ? "?" : link.Title[..1].ToUpperInvariant();
            icon = new Border
            {
                Width = 28, Height = 28, CornerRadius = new Avalonia.CornerRadius(14), Background = LetterBrush(link.Url),
                Child = new TextBlock
                {
                    Text = letter, Foreground = Avalonia.Media.Brushes.White, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
        }
        icon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        icon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var button = new Button
        {
            Classes = { "QuickLink" }, Background = Avalonia.Media.Brushes.Transparent, Padding = new Avalonia.Thickness(4, 8),
            CornerRadius = new Avalonia.CornerRadius(10), Width = 76, Height = 84,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Content = TileBody(icon, link.Title),
        };
        ToolTip.SetTip(button, $"{link.Title}\n{link.Url}");
        button.Click += (_, _) => Navigate(link.Url);
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            var props = e.GetCurrentPoint(button).Properties;
            var newTab = props.IsMiddleButtonPressed ||
                         (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control));
            if (!newTab) return;
            e.Handled = true;
            OpenInNewTabRequested?.Invoke(this, link.Url);
        }, RoutingStrategies.Tunnel);

        var edit = new MenuItem { Header = "Edit shortcut" };
        edit.Click += (_, _) => ShowEditor(button, link);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) => Links.Value.Remove(link.Url);
        button.ContextMenu = new ContextMenu { Items = { edit, remove } };
        return button;
    }

    private Control AddTile()
    {
        var plus = new TextBlock
        {
            Text = "+", FontSize = 24, Foreground = Avalonia.Media.Brush.Parse("#5F6368"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var button = new Button
        {
            Classes = { "QuickLink" }, Background = Avalonia.Media.Brushes.Transparent, Padding = new Avalonia.Thickness(4, 8),
            CornerRadius = new Avalonia.CornerRadius(10), Width = 76, Height = 84,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Content = TileBody(plus, "Add shortcut"),
        };
        button.Click += (_, _) => ShowEditor(button, null);
        return button;
    }

    /// <summary>A small flyout to add a shortcut, or edit <paramref name="link"/>.</summary>
    private void ShowEditor(Control anchor, QuickLink? link)
    {
        var name = new TextBox { Watermark = "Name (optional)", Text = link?.Title ?? "", Width = 260 };
        var url = new TextBox { Watermark = "URL, e.g. youtube.com", Text = link?.Url ?? "", Width = 260 };
        var error = new TextBlock { Foreground = Avalonia.Media.Brush.Parse("#D93025"), FontSize = 12, IsVisible = false };
        var save = new Button { Content = link == null ? "Add" : "Save", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Placement = PlacementMode.Bottom,
            Content = new StackPanel
            {
                Spacing = 8, Margin = new Avalonia.Thickness(4),
                Children =
                {
                    new TextBlock { Text = link == null ? "Add shortcut" : "Edit shortcut", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    name, url, error, save,
                },
            },
        };
        void Commit()
        {
            var ok = link == null ? Links.Value.Add(url.Text ?? "", name.Text) : Links.Value.Update(link.Url, url.Text ?? "", name.Text);
            if (ok)
            {
                flyout.Hide();
                return;
            }
            error.Text = QuickLinkStore.Normalize(url.Text) == null
                ? "Enter a web address (http or https)."
                : $"You can keep up to {QuickLinkStore.MaxLinks} shortcuts.";
            error.IsVisible = true;
        }
        save.Click += (_, _) => Commit();
        url.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.Post(() => (link == null ? url : name).Focus(), DispatcherPriority.Background);
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
