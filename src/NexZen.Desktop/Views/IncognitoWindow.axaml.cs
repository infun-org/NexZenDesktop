using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Desktop.Handlers;
using NexZen.Desktop.Helpers;
using NexZen.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Views;

public partial class IncognitoWindow : Window
{
    private const string SuggestIcon =
        "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";

    private static int _stagger;

    private readonly MainViewModel? _viewModel;
    private readonly List<IncognitoTab> _tabs = new();
    private readonly SearchSuggestionService _suggestions = new();
    private readonly PageFindUi _find = new();
    private IncognitoTab? _active;
    private CancellationTokenSource? _suggestCts;
    private TaskCompletionSource<bool>? _permissionWait;
    private bool _closing;

    // Parameterless ctor for the XAML runtime loader and designer.
    public IncognitoWindow() : this(null) { }

    private IncognitoWindow(MainViewModel? viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        if (Content is Grid root)
        {
            Grid.SetRowSpan(_find.Anchor, 4);
            _find.Mount(root);
        }
        IncognitoSession.RegisterWindow();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnBrowserZoomKeyDown, RoutingStrategies.Tunnel);
        SuggestBackdrop.PointerPressed += (_, e) =>
        {
            if (e.Source == SuggestBackdrop)
            {
                HideSuggestions();
                e.Handled = true;
            }
        };
    }

    public static void Open(Window? anchor, MainViewModel? viewModel, string? url = null)
    {
        var window = new IncognitoWindow(viewModel);
        if (anchor != null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            var step = _stagger++ % 8;
            window.Position = new PixelPoint(anchor.Position.X + 48 + step * 28, anchor.Position.Y + 48 + step * 28);
        }

        window.Show();
        window.Activate();
        var start = string.IsNullOrWhiteSpace(url) ? "about:blank" : url.Trim();
        window.AddTab(start);
        if (string.Equals(start, "about:blank", StringComparison.OrdinalIgnoreCase))
            window.AddressBox.Focus();
    }

    private void NewTabButton_Click(object? sender, RoutedEventArgs e) => AddTab("about:blank");

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_active?.Browser.CanGoBack == true)
            _active.Browser.GoBack();
    }

    private void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_active?.Browser.CanGoForward == true)
            _active.Browser.GoForward();
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => _active?.Browser.Reload();

    private void AddTab(string url)
    {
        if (_closing)
            return;

        IncognitoSession.HoldBrowser();
        var browser = new AvaloniaCefBrowser(() => IncognitoSession.BorrowForBrowser()) { Address = BlankPage.ForBrowser(url) };
        WebAudioDecodeShim.Attach(browser);
        var domainSession = new DomainLockBrowserSession();
        _find.Attach(browser);
        browser.Settings.WindowlessFrameRate = 60;
        browser.LifeSpanHandler = new IncognitoLifeSpanHandler(this);
        browser.RequestHandler = new NewTabRequestHandler(
            target => AddTab(target),
            installFromStore: null,
            App.ServiceProvider?.GetService<DomainLockStore>(),
            domainSession);
        browser.ConsoleMessage += (_, message) =>
        {
            if (message.Message.StartsWith("[DomainLockPage] ", StringComparison.Ordinal))
                Program.StartupLog.Write(message.Message);
        };
        browser.DialogHandler = new CustomDialogHandler();
        browser.PermissionHandler = new IncognitoPermissionHandler(this);
        if (_viewModel != null)
            browser.DownloadHandler = new CustomDownloadHandler(_viewModel);

        var tab = new IncognitoTab(browser, url);
        browser.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var title = browser.Title;
            tab.Title.Text = string.IsNullOrWhiteSpace(title) ? "New tab" : title;
        });
        browser.AddressChanged += (_, address) => Dispatcher.UIThread.Post(() =>
        {
            var actualAddress = address ?? "";
            tab.Url = BlankPage.ForDisplay(domainSession.TryGetChallenge(actualAddress, out var targetUrl) ? targetUrl : actualAddress);
            if (_active == tab && !AddressBox.IsFocused)
                AddressBox.Text = DisplayUrl(tab.Url);
        });
        browser.LoadingStateChange += (_, _) => Dispatcher.UIThread.Post(UpdateNavButtons);

        _tabs.Add(tab);
        PageHost.Children.Add(browser);
        TabStrip.Children.Add(tab.Header);
        tab.HeaderPressed += (_, _) => Select(tab);
        tab.ClosePressed += (_, _) => CloseTab(tab);
        Select(tab);
    }

    private void Select(IncognitoTab tab)
    {
        _active = tab;
        HideSuggestions();
        foreach (var item in _tabs)
        {
            var on = item == tab;
            item.SetActive(on);
            var browser = item.Browser;
            if (on)
            {
                browser.IsVisible = true;
                browser.Width = double.NaN;
                browser.Height = double.NaN;
                browser.Margin = new Thickness(0);
                browser.HorizontalAlignment = HorizontalAlignment.Stretch;
                browser.VerticalAlignment = VerticalAlignment.Stretch;
                browser.ZIndex = 1;
            }
            else
            {
                browser.HorizontalAlignment = HorizontalAlignment.Left;
                browser.VerticalAlignment = VerticalAlignment.Top;
                browser.Width = 1;
                browser.Height = 1;
                browser.Margin = new Thickness(-9999, -9999, 0, 0);
                browser.IsVisible = false;
                browser.ZIndex = 0;
            }
        }

        AddressBox.Text = DisplayUrl(tab.Url);
        UpdateNavButtons();
        _find.RetargetOpenMain(tab.Browser);
    }

    private void CloseTab(IncognitoTab tab)
    {
        if (_closing)
            return;
        var index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        _tabs.RemoveAt(index);
        TabStrip.Children.Remove(tab.Header);
        PageHost.Children.Remove(tab.Browser);
        if (_find.Targets(tab.Browser))
            _find.ForgetTarget();
        tab.Browser.Dispose();
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _active?.Browser.CanGoBack == true;
        ForwardButton.IsEnabled = _active?.Browser.CanGoForward == true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement() as InputElement;
        if (_find.HandleChromeKey(e, _active?.Browser, focused))
        {
            if (e.Key == Key.F)
                HideSuggestions();
            return;
        }

        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Alt) && !mods.HasFlag(KeyModifiers.Meta);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        if (ctrl && shift && e.Key == Key.N)
        {
            e.Handled = true;
            Open(this, _viewModel);
            return;
        }

        if (e.Key == Key.I && MainWindow.IsCtrlOrCmdShiftChord(mods))
        {
            e.Handled = true;
            if (Avalonia.Application.Current is NexZen.Desktop.App app)
                app.ShowAiFunctionWindow();
            return;
        }

        if (!ctrl || shift)
        {
            if (mods == KeyModifiers.Alt && e.Key == Key.Left && _active?.Browser.CanGoBack == true)
            {
                e.Handled = true;
                _active.Browser.GoBack();
            }
            else if (mods == KeyModifiers.Alt && e.Key == Key.Right && _active?.Browser.CanGoForward == true)
            {
                e.Handled = true;
                _active.Browser.GoForward();
            }
            return;
        }

        switch (e.Key)
        {
            case Key.T:
                e.Handled = true;
                AddTab("about:blank");
                AddressBox.Focus();
                break;
            case Key.W:
                e.Handled = true;
                if (_active != null)
                    CloseTab(_active);
                break;
            case Key.L:
                e.Handled = true;
                AddressBox.Focus();
                AddressBox.SelectAll();
                break;
            case Key.R:
                e.Handled = true;
                _active?.Browser.Reload();
                break;
        }
    }

    private void OnBrowserZoomKeyDown(object? sender, KeyEventArgs e)
    {
        BrowserZoomShortcuts.TryHandle(e, _active?.Browser);
    }

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SuggestLayer.IsVisible)
            {
                HideSuggestions();
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        CommitAddress(AddressBox.Text);
    }

    private async void AddressBox_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Escape)
            return;

        _suggestCts?.Cancel();
        _suggestCts = new CancellationTokenSource();
        var token = _suggestCts.Token;
        var query = AddressBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            HideSuggestions();
            return;
        }

        try
        {
            await Task.Delay(300, token);
            var items = await _suggestions.GetSuggestionsAsync(query, token);
            if (token.IsCancellationRequested)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !string.Equals(AddressBox.Text ?? "", query, StringComparison.Ordinal))
                    return;
                ShowSuggestions(items);
            });
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void CommitAddress(string? raw)
    {
        if (_active == null)
            return;
        var target = SearchTargets.FromAddressBar(_viewModel?.SelectedSearchEngine, raw);
        _suggestCts?.Cancel();
        HideSuggestions();
        if (target.Length == 0)
            return;
        AddressBox.Text = BlankPage.ForDisplay(target);
        _active.Url = BlankPage.ForDisplay(target);
        _active.Browser.Address = BlankPage.ForBrowser(target);
        _active.Browser.Focus();
    }

    private void ShowSuggestions(IReadOnlyList<string> items)
    {
        SuggestList.Children.Clear();
        if (items.Count == 0)
        {
            SuggestLayer.IsVisible = false;
            return;
        }

        foreach (var item in items)
        {
            var suggestion = item;
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 34,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#E8EAED")),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                Content = SuggestionRow(suggestion)
            };
            button.Click += (_, _) => CommitAddress(suggestion);
            SuggestList.Children.Add(button);
        }

        SuggestLayer.IsVisible = true;
    }

    private static Grid SuggestionRow(string text)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        row.Children.Add(new PathIcon
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#9AA0A6")),
            Data = Geometry.Parse(SuggestIcon)
        });
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.Parse("#E8EAED")),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private void HideSuggestions()
    {
        SuggestLayer.IsVisible = false;
        SuggestList.Children.Clear();
    }

    internal Task<bool> AskPermissionAsync(string message)
    {
        if (_closing)
            return Task.FromResult(false);
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            if (_closing || _permissionWait != null)
            {
                wait.TrySetResult(false);
                return;
            }

            _permissionWait = wait;
            PermissionText.Text = message;
            PermissionLayer.IsVisible = true;
        });
        return wait.Task;
    }

    private void PermissionAllow_Click(object? sender, RoutedEventArgs e) => FinishPermission(true);

    private void PermissionDeny_Click(object? sender, RoutedEventArgs e) => FinishPermission(false);

    private void FinishPermission(bool allow)
    {
        PermissionLayer.IsVisible = false;
        var wait = _permissionWait;
        _permissionWait = null;
        wait?.TrySetResult(allow);
    }

    private static string DisplayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || BlankPage.IsBlank(url))
            return BlankPage.DisplayUrl;
        return url;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
            _suggestCts?.Cancel();
            _find.Close();
            FinishPermission(false);
            foreach (var tab in _tabs.ToArray())
            {
                PageHost.Children.Remove(tab.Browser);
                tab.Browser.Dispose();
            }

            _tabs.Clear();
            TabStrip.Children.Clear();
            IncognitoSession.ReleaseWindow();
        }

        base.OnClosing(e);
    }

    private sealed class IncognitoTab
    {
        private readonly Border _header;
        private readonly TextBlock _title;

        public IncognitoTab(AvaloniaCefBrowser browser, string url)
        {
            Browser = browser;
            Url = url;
            _title = new TextBlock
            {
                Text = "New tab",
                Foreground = new SolidColorBrush(Color.Parse("#E8EAED")),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 160
            };
            Title = _title;
            var close = new Button
            {
                Content = "×",
                Width = 22,
                Height = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#E8EAED")),
                BorderThickness = new Thickness(0),
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            close.Click += (_, e) =>
            {
                e.Handled = true;
                ClosePressed?.Invoke(this, EventArgs.Empty);
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(_title);
            Grid.SetColumn(close, 1);
            row.Children.Add(close);
            _header = new Border
            {
                Height = 32,
                Padding = new Thickness(10, 0),
                CornerRadius = new CornerRadius(8),
                Child = row,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            _header.PointerPressed += (_, e) =>
            {
                if (e.Source == close)
                    return;
                HeaderPressed?.Invoke(this, EventArgs.Empty);
            };
            SetActive(false);
        }

        public AvaloniaCefBrowser Browser { get; }
        public TextBlock Title { get; }
        public string Url { get; set; }
        public Control Header => _header;
        public event EventHandler? HeaderPressed;
        public event EventHandler? ClosePressed;

        public void SetActive(bool active)
        {
            _header.Background = new SolidColorBrush(Color.Parse(active ? "#3C4043" : "#202124"));
        }
    }

    private sealed class IncognitoLifeSpanHandler : LifeSpanHandler
    {
        private readonly IncognitoWindow _window;

        public IncognitoLifeSpanHandler(IncognitoWindow window)
        {
            _window = window;
        }

        protected override void OnBeforeClose(CefBrowser browser)
        {
            IncognitoSession.BrowserClosed();
        }

        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            var newSurface = targetDisposition is CefWindowOpenDisposition.NewForegroundTab
                or CefWindowOpenDisposition.NewBackgroundTab
                or CefWindowOpenDisposition.NewPopup
                or CefWindowOpenDisposition.NewWindow;
            if (!newSurface)
                return false;
            // A windowless browser cannot host CEF's own popup window.
            if (!PopupBrowserWindow.TryNormalize(targetUrl, out var target))
                return true;

            var asWindow = targetDisposition is CefWindowOpenDisposition.NewPopup or CefWindowOpenDisposition.NewWindow;
            Dispatcher.UIThread.Post(() =>
            {
                if (_window._closing)
                    return;
                if (asWindow)
                    Open(_window, _window._viewModel, target);
                else
                    _window.AddTab(target);
            });
            return true;
        }
    }

    private sealed class IncognitoPermissionHandler : PermissionHandler
    {
        private readonly IncognitoWindow _window;

        public IncognitoPermissionHandler(IncognitoWindow window)
        {
            _window = window;
        }

        protected override bool OnShowPermissionPrompt(CefBrowser browser, ulong promptId, string requestingOrigin, CefPermissionRequestTypes requestedPermissions, CefPermissionPromptCallback callback)
        {
            var message = $"{requestingOrigin} is requesting permission for {Describe(requestedPermissions)}.";
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var allow = await _window.AskPermissionAsync(message);
                    callback.Continue(allow ? CefPermissionRequestResult.Accept : CefPermissionRequestResult.Deny);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Incognito] permission prompt failed: {ex.Message}");
                    callback.Continue(CefPermissionRequestResult.Deny);
                }
                finally
                {
                    callback.Dispose();
                }
            });
            return true;
        }

        protected override bool OnRequestMediaAccessPermission(CefBrowser browser, CefFrame frame, string requestingOrigin, CefMediaAccessPermissionTypes requestedPermissions, CefMediaAccessCallback callback)
        {
            var message = $"{requestingOrigin} is requesting permission for {requestedPermissions}.";
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var allow = await _window.AskPermissionAsync(message);
                    callback.Continue(allow ? requestedPermissions : CefMediaAccessPermissionTypes.None);
                }
                finally
                {
                    callback.Dispose();
                }
            });
            return true;
        }

        private static string Describe(CefPermissionRequestTypes requested)
        {
            if (requested == CefPermissionRequestTypes.None)
                return "an unspecified permission";
            return requested.ToString();
        }
    }
}

internal static class IncognitoSession
{
    private static readonly object Gate = new();
    private static readonly MethodInfo? AddRefMethod = typeof(CefPreferenceManager).GetMethod(
        "AddRef",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static CefRequestContext? _context;
    private static CefRequestContext? _retiring;
    private static int _windows;
    private static int _browsers;

    public static CefRequestContext Context
    {
        get
        {
            lock (Gate)
            {
                _context ??= CefRequestContext.CreateContext(new CefRequestContextSettings
                {
                    CachePath = string.Empty,
                    PersistSessionCookies = false
                }, null);
                return _context;
            }
        }
    }

    // CefRequestContext.ToNative does not add a reference, and CreateBrowser's
    // C wrapper takes ownership of the one it receives. Each browser needs its
    // own extra reference or the second tab frees the shared profile.
    public static CefRequestContext BorrowForBrowser()
    {
        var context = Context;
        if (AddRefMethod == null)
            throw new InvalidOperationException("CefRequestContext.AddRef is not available.");
        AddRefMethod.Invoke(context, null);
        return context;
    }

    public static void RegisterWindow()
    {
        lock (Gate)
            _windows++;
    }

    public static void HoldBrowser()
    {
        lock (Gate)
            _browsers++;
    }

    public static void BrowserClosed()
    {
        CefRequestContext? doomed = null;
        lock (Gate)
        {
            if (_browsers > 0)
                _browsers--;
            if (_browsers == 0 && _windows == 0)
            {
                doomed = _retiring;
                _retiring = null;
            }
        }

        doomed?.Dispose();
    }

    public static void ReleaseWindow()
    {
        CefRequestContext? doomed = null;
        lock (Gate)
        {
            if (_windows > 0)
                _windows--;
            if (_windows > 0)
                return;
            _retiring = _context;
            _context = null;
            if (_browsers == 0)
            {
                doomed = _retiring;
                _retiring = null;
            }
        }

        doomed?.Dispose();
    }
}
