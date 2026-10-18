using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Desktop;
using NexZen.Desktop.Handlers;
using NexZen.Desktop.Helpers;
using NexZen.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Controls;

public partial class PinnedPane : UserControl
{
    private const double PhoneWidth = 390;
    private const double PhoneHeight = 844;
    private const double ExpandedMinHeight = 360;
    private const double CollapsedHeight = 46;
    private const int DesktopMinWidth = 768;
    private const double EdgeGap = 20;

    private enum Edge { Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private AvaloniaCefBrowser? _browser;
    private DomainLockBrowserSession? _domainSession;
    private MainViewModel? _viewModel;
    private bool _wantsOpen;
    private bool _suppressed;
    private bool _placed;
    private bool _drag;
    private Edge? _resize;
    private Point _origin;
    private double _startLeft, _startTop, _startWidth, _startHeight;
    private string? _resumeUrl;
    private PageCopyContext? _menuContext;
    private int _mobileToken;
    private int _appliedWidth;
    private int _appliedHeight;
    private bool? _appliedMobile;
    private bool _audioMuted;
    private bool _isCollapsed;
    private double _expandedHeight = PhoneHeight;
    public int CascadeIndex { get; set; }
    private bool _viewportPosted;
    private readonly SearchSuggestionService _suggestions = new();
    private CancellationTokenSource? _suggestCts;

    private const string SuggestIcon =
        "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z";

    public PinnedPane()
    {
        InitializeComponent();
        PointerPressed += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        SizeChanged += (_, _) =>
        {
            if (!_isCollapsed && Bounds.Height >= ExpandedMinHeight)
                _expandedHeight = Bounds.Height;
            ScheduleMobileViewport();
        };
        BackButton.Click += (_, _) => { if (_browser?.CanGoBack == true) _browser.GoBack(); };
        ForwardButton.Click += (_, _) => { if (_browser?.CanGoForward == true) _browser.GoForward(); };
        RefreshButton.Click += (_, _) => ApplyViewport(reload: true);
        ToggleSizeButton.Click += (_, _) => ToggleCollapsed();
        CloseButton.Click += (_, _) => ClosePin();
        AddressBox.KeyDown += AddressBox_KeyDown;
        AddressBox.KeyUp += AddressBox_KeyUp;
        SuggestBackdrop.PointerPressed += (_, e) =>
        {
            if (e.Source == SuggestBackdrop)
            {
                CancelSuggestions();
                e.Handled = true;
            }
        };
        DragGrip.PointerPressed += BeginDrag;
        DragGrip.PointerMoved += OnPointerMoved;
        DragGrip.PointerReleased += EndPointer;
        WireResize(ResizeLeft, Edge.Left);
        WireResize(ResizeRight, Edge.Right);
        WireResize(ResizeTop, Edge.Top);
        WireResize(ResizeBottom, Edge.Bottom);
        WireResize(ResizeTopLeft, Edge.TopLeft);
        WireResize(ResizeTopRight, Edge.TopRight);
        WireResize(ResizeBottomLeft, Edge.BottomLeft);
        WireResize(ResizeBottomRight, Edge.BottomRight);
        MenuLayer.PointerPressed += (_, e) =>
        {
            if (e.Source == MenuLayer)
                CloseMenu();
        };
        CutButton.Click += (_, _) => Raise(CutRequested);
        CopyButton.Click += (_, _) => Raise(CopyRequested);
        PasteButton.Click += (_, _) => Raise(PasteRequested);
        MuteTabButton.Click += (_, _) => ToggleMute();
        SaveImageButton.Click += (_, _) => Raise(SaveImageRequested);
        OpenDeviceButton.Click += (_, _) => OpenDeviceRequested?.Invoke(this, _menuContext ?? new PageCopyContext());
    }

    public AvaloniaCefBrowser? Browser => _browser;
    public string? Address => _browser?.Address ?? _resumeUrl;
    public bool IsMenuOpen => MenuLayer.IsVisible;

    public event EventHandler<AvaloniaCefBrowser>? BrowserCreated;
    public event EventHandler? BrowserDisposed;
    public event EventHandler? Closed;
    public event EventHandler? Activated;
    public event EventHandler<PageCopyContext>? CutRequested;
    public event EventHandler<PageCopyContext>? CopyRequested;
    public event EventHandler<PageCopyContext>? PasteRequested;
    public event EventHandler<PageCopyContext>? SaveImageRequested;
    public event EventHandler<PageCopyContext>? OpenDeviceRequested;
    public event EventHandler<int>? ChoiceSelected;

    public void Bind(MainViewModel? viewModel)
    {
        _viewModel = viewModel;
    }

    public void ShowUrl(string url)
    {
        _wantsOpen = true;
        _resumeUrl = url;
        CancelSuggestions();
        CloseMenu();
        if (_suppressed)
            return;
        IsVisible = true;
        AddressBox.Text = url;
        EnsureBrowser();
        if (_browser != null)
        {
            _browser.Address = url;
            _browser.Focus();
        }
        PlaceIfNeeded();
    }

    public void ClosePin()
    {
        _wantsOpen = false;
        _resumeUrl = null;
        CancelSuggestions();
        CloseMenu();
        _audioMuted = false;
        DestroyBrowser();
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Suppress(bool hide)
    {
        _suppressed = hide;
        if (hide)
            CloseMenu();
        IsVisible = _wantsOpen && !hide;
    }

    public void ReleaseBrowser()
    {
        _suppressed = false;
        if (!string.IsNullOrWhiteSpace(_browser?.Address))
            _resumeUrl = _browser.Address;
        CloseMenu();
        DestroyBrowser();
        IsVisible = false;
    }

    public void ParkForMainWindowHide()
    {
        CloseMenu();
        _domainSession?.LockAll();
        if (_browser == null)
            return;

        var url = _browser.Address ?? string.Empty;
        if (_domainSession?.TryGetChallenge(url, out var challengeTarget) == true)
            url = challengeTarget;
        if (!string.IsNullOrWhiteSpace(url))
        {
            _resumeUrl = url;
            AddressBox.Text = url;
        }

        // Closing the CEF control releases this pinned page's renderer while
        // keeping the pinned pane and its last URL available for tray restore.
        DestroyBrowser();
    }

    public void ResumeAfterMainWindowShow()
    {
        if (_browser != null)
            _browser.IsVisible = true;
        else
            RestoreBrowser();
    }

    public void RestoreBrowser()
    {
        if (!_wantsOpen || string.IsNullOrWhiteSpace(_resumeUrl) || _suppressed)
            return;
        ShowUrl(_resumeUrl);
    }

    public void CloseMenu()
    {
        MenuLayer.IsVisible = false;
        MenuStatus.IsVisible = false;
        ChoiceScroll.IsVisible = false;
        ActionList.IsVisible = true;
        ChoiceList.Children.Clear();
    }

    public void ShowShareStatus(string text)
    {
        ActionList.IsVisible = false;
        ChoiceScroll.IsVisible = false;
        MenuStatus.Text = text;
        MenuStatus.IsVisible = true;
        MenuLayer.IsVisible = true;
    }

    public void ShowChoices(IReadOnlyList<string> names)
    {
        ChoiceList.Children.Clear();
        for (var i = 0; i < names.Count; i++)
        {
            var index = i;
            var button = new Button { Content = names[i] };
            button.Classes.Add("ListItem");
            button.Click += (_, _) => ChoiceSelected?.Invoke(this, index);
            ChoiceList.Children.Add(button);
        }
        ActionList.IsVisible = false;
        MenuStatus.Text = "Choose a device";
        MenuStatus.IsVisible = true;
        ChoiceScroll.IsVisible = true;
        MenuLayer.IsVisible = true;
    }

    public void ClampToParent()
    {
        if (!IsVisible || Parent is not Control parent || parent.Bounds.Width < 40 || parent.Bounds.Height < 40)
            return;
        var maxW = Math.Max(MinWidth, parent.Bounds.Width - EdgeGap * 2);
        var minH = _isCollapsed ? CollapsedHeight : ExpandedMinHeight;
        var maxH = Math.Max(minH, parent.Bounds.Height - EdgeGap * 2);
        Width = Math.Clamp(double.IsNaN(Width) || Width < 1 ? PhoneWidth : Width, MinWidth, maxW);
        Height = Math.Clamp(double.IsNaN(Height) || Height < 1 ? PhoneHeight : Height, minH, maxH);
        var left = Margin.Left;
        var top = Margin.Top;
        if (left + Width > parent.Bounds.Width - EdgeGap)
            left = Math.Max(EdgeGap, parent.Bounds.Width - Width - EdgeGap);
        if (top + Height > parent.Bounds.Height - EdgeGap)
            top = Math.Max(EdgeGap, parent.Bounds.Height - Height - EdgeGap);
        if (left < EdgeGap)
            left = EdgeGap;
        if (top < EdgeGap)
            top = EdgeGap;
        Margin = new Thickness(left, top, 0, 0);
    }

    private void ToggleCollapsed()
    {
        CloseMenu();
        if (_isCollapsed)
        {
            _isCollapsed = false;
            BrowserHost.IsVisible = true;
            SuggestLayer.IsVisible = false;
            SetResizeHandlesVisible(true);
            ToolTip.SetTip(ToggleSizeButton, "Collapse Pinned window");
            ToggleSizeIcon.Text = "−";
            Height = Math.Max(ExpandedMinHeight, _expandedHeight);
        }
        else
        {
            if (Bounds.Height >= ExpandedMinHeight)
                _expandedHeight = Bounds.Height;
            _isCollapsed = true;
            BrowserHost.IsVisible = false;
            SuggestLayer.IsVisible = false;
            SetResizeHandlesVisible(false);
            ToolTip.SetTip(ToggleSizeButton, "Expand Pinned window");
            ToggleSizeIcon.Text = "□";
            Height = CollapsedHeight;
        }
        ClampToParent();
    }

    private void SetResizeHandlesVisible(bool visible)
    {
        ResizeLeft.IsVisible = visible;
        ResizeRight.IsVisible = visible;
        ResizeTop.IsVisible = visible;
        ResizeBottom.IsVisible = visible;
        ResizeTopLeft.IsVisible = visible;
        ResizeTopRight.IsVisible = visible;
        ResizeBottomLeft.IsVisible = visible;
        ResizeBottomRight.IsVisible = visible;
    }

    private void PlaceIfNeeded()
    {
        if (_placed)
        {
            ClampToParent();
            return;
        }
        if (Parent is not Control parent || parent.Bounds.Width < 40 || parent.Bounds.Height < 40)
        {
            Dispatcher.UIThread.Post(PlaceIfNeeded, DispatcherPriority.Background);
            return;
        }
        var maxW = Math.Max(MinWidth, parent.Bounds.Width - EdgeGap * 2);
        var maxH = Math.Max(MinHeight, parent.Bounds.Height - EdgeGap * 2);
        Width = Math.Min(PhoneWidth, maxW);
        Height = Math.Min(PhoneHeight, maxH);
        Margin = new Thickness(
            Math.Max(EdgeGap, parent.Bounds.Width - Width - EdgeGap - CascadeIndex * 28),
            Math.Max(EdgeGap, parent.Bounds.Height - Height - EdgeGap - CascadeIndex * 28),
            0, 0);
        _placed = true;
    }

    private void EnsureBrowser()
    {
        if (_browser != null)
            return;
        var browser = new AvaloniaCefBrowser();
        WebAudioDecodeShim.Attach(browser);
        var domainSession = new DomainLockBrowserSession();
        _domainSession = domainSession;
        browser.Settings.WindowlessFrameRate = 60;
        browser.JavascriptContextCreated += (_, e) =>
        {
            if (e.Frame.IsMain)
            {
                var frameUrl = e.Frame.Url ?? string.Empty;
                var host = Uri.TryCreate(frameUrl, UriKind.Absolute, out var frameUri) ? frameUri.Host : "unknown";
                Program.StartupLog.Write($"[PasswordCapture] pinned JS context ready host={host}");
            }
        };
        browser.ConsoleMessage += (_, message) =>
        {
            if (message.Message.StartsWith("[DomainLockPage] ", StringComparison.Ordinal))
            {
                Program.StartupLog.Write(message.Message);
                return;
            }
            if (message.Message.StartsWith("[PasswordCapture] ", StringComparison.Ordinal))
            {
                var pageHost = Uri.TryCreate(browser.Address, UriKind.Absolute, out var diagnosticPage) ? diagnosticPage.Host : "unknown";
                Program.StartupLog.Write($"[PasswordCapture] pinned page host={pageHost} {message.Message[18..]}");
                return;
            }
            if (message.Level < CefLogSeverity.Error)
                return;
            if (!message.Message.Contains("nexzenPasswordBridge", StringComparison.OrdinalIgnoreCase)
                && !message.Message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase)
                && !message.Message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase))
                return;
            var detail = message.Message.Length > 240 ? message.Message[..240] : message.Message;
            Program.StartupLog.Write($"[PasswordCapture] pinned JS error line={message.Line}: {detail}");
        };
        browser.LoadEnd += (_, e) =>
        {
            var url = e.Frame.Url ?? string.Empty;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Frame.ExecuteJavaScript(PasswordCaptureBridge.ObserverScript, url, 1);
                if (e.Frame.IsMain)
                    Program.StartupLog.Write($"[PasswordCapture] pinned observer injection requested main={e.Frame.IsMain} host={new Uri(url).Host}");
            }
        };
        browser.BrowserInitialized += () =>
        {
            if (_audioMuted)
                TabAudio.Apply(browser, true);
            _ = ApproximateGeolocation.PublishAsync(() => browser.GetBrowserHost());
            Dispatcher.UIThread.Post(() => ApplyViewport(reload: true));
        };
        browser.AddressChanged += (_, address) => Dispatcher.UIThread.Post(() =>
        {
            var displayAddress = domainSession.TryGetChallenge(address, out var targetUrl) ? targetUrl : address;
            _resumeUrl = displayAddress;
            if (!AddressBox.IsFocused)
                AddressBox.Text = displayAddress;
            UpdateNavButtons();
        });
        browser.LoadingStateChange += (_, _) => Dispatcher.UIThread.Post(UpdateNavButtons);
        if (_viewModel != null)
        {
            browser.DownloadHandler = new CustomDownloadHandler(_viewModel);
            browser.PermissionHandler = new CustomPermissionHandler(_viewModel);
        }
        // Pinned Window owns a separate CEF browser from the regular tab strip,
        // so it needs the same intent:// fallback handling explicitly.
        browser.RequestHandler = new NewTabRequestHandler(
            target => browser.Address = target,
            installFromStore: null,
            App.ServiceProvider?.GetService<DomainLockStore>(),
            domainSession);
        browser.DialogHandler = new CustomDialogHandler();
        browser.LifeSpanHandler = new CustomLifeSpanHandler((_, target) =>
        {
            if (!string.IsNullOrWhiteSpace(target))
                browser.Address = target;
        });
        browser.ContextMenuHandler = new CustomContextMenuHandler((x, y, copy) =>
            Dispatcher.UIThread.Post(() => ShowMenu(x, y, copy)));
        _browser = browser;
        BrowserCreated?.Invoke(this, browser);
        BrowserHost.Child = browser;
    }

    private void DestroyBrowser()
    {
        var browser = _browser;
        _browser = null;
        _domainSession = null;
        BrowserDisposed?.Invoke(this, EventArgs.Empty);
        if (browser != null)
            browser.IsVisible = false;
        browser?.Dispose();
        BrowserHost.Child = null;
        _appliedMobile = null;
        _appliedWidth = 0;
        _appliedHeight = 0;
        ++_mobileToken;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _browser?.CanGoBack == true;
        ForwardButton.IsEnabled = _browser?.CanGoForward == true;
    }

    private void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SuggestLayer.IsVisible)
            {
                CancelSuggestions();
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Enter || _browser == null)
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
        if (_browser == null)
            return;
        var text = (raw ?? "").Trim();
        _suggestCts?.Cancel();
        HideSuggestions();
        if (text.Length == 0)
            return;

        var target = SearchTargets.FromAddressBar(_viewModel?.SelectedSearchEngine, text);
        if (target.Length == 0)
            return;
        AddressBox.Text = target;
        _browser.Address = target;
        _browser.Focus();
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
            var button = new Button();
            button.Classes.Add("ListItem");
            button.Content = SuggestionRow(suggestion);
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
        var icon = new PathIcon
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.Parse("#999999")),
            Data = Geometry.Parse(SuggestIcon)
        };
        var label = new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(icon);
        row.Children.Add(label);
        return row;
    }

    private void HideSuggestions()
    {
        SuggestLayer.IsVisible = false;
        SuggestList.Children.Clear();
    }

    private void CancelSuggestions()
    {
        _suggestCts?.Cancel();
        HideSuggestions();
    }

    private void ShowMenu(int x, int y, PageCopyContext copy)
    {
        _menuContext = copy;
        CancelSuggestions();
        CloseMenu();
        SaveImageButton.IsVisible = copy.IsImage;
        MuteTabButton.Content = _audioMuted ? "Unmute this tab" : "Mute this tab";
        ActionList.IsVisible = true;
        var cardWidth = MenuCard.Width > 1 ? MenuCard.Width : 260;
        var chrome = Toolbar.Bounds.Height > 1 ? Toolbar.Bounds.Height : 46;
        var left = Math.Clamp(x, 8, Math.Max(8, Bounds.Width - cardWidth - 8));
        var top = Math.Clamp(chrome + y, 8, Math.Max(8, Bounds.Height - 220));
        MenuCard.Margin = new Thickness(left, top, 0, 0);
        MenuLayer.IsVisible = true;
    }

    private void ToggleMute()
    {
        _audioMuted = !_audioMuted;
        if (_browser != null)
            TabAudio.Apply(_browser, _audioMuted);
        CloseMenu();
    }

    private void Raise(EventHandler<PageCopyContext>? handler)
    {
        var copy = _menuContext;
        CloseMenu();
        if (copy != null)
            handler?.Invoke(this, copy);
    }

    private void BeginDrag(object? sender, PointerPressedEventArgs e)
    {
        if (Parent is not Visual parent)
            return;
        _drag = true;
        _origin = e.GetPosition(parent);
        _startLeft = Margin.Left;
        _startTop = Margin.Top;
        e.Pointer.Capture(DragGrip);
        e.Handled = true;
    }

    private void WireResize(Control handle, Edge edge)
    {
        handle.PointerPressed += (_, e) =>
        {
            if (Parent is not Visual parent)
                return;
            _resize = edge;
            _origin = e.GetPosition(parent);
            _startLeft = Margin.Left;
            _startTop = Margin.Top;
            _startWidth = Bounds.Width;
            _startHeight = Bounds.Height;
            e.Pointer.Capture(handle);
            e.Handled = true;
        };
        handle.PointerMoved += OnPointerMoved;
        handle.PointerReleased += EndPointer;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Parent is not Visual parent)
            return;
        var point = e.GetPosition(parent);
        if (_drag)
        {
            Margin = new Thickness(_startLeft + point.X - _origin.X, _startTop + point.Y - _origin.Y, 0, 0);
            _placed = true;
            ClampToParent();
            return;
        }
        if (_resize == null)
            return;

        var dx = point.X - _origin.X;
        var dy = point.Y - _origin.Y;
        var left = _startLeft;
        var top = _startTop;
        var width = _startWidth;
        var height = _startHeight;
        var edge = _resize.Value;
        if (edge is Edge.Left or Edge.TopLeft or Edge.BottomLeft)
        {
            left = _startLeft + dx;
            width = _startWidth - dx;
        }
        if (edge is Edge.Right or Edge.TopRight or Edge.BottomRight)
            width = _startWidth + dx;
        if (edge is Edge.Top or Edge.TopLeft or Edge.TopRight)
        {
            top = _startTop + dy;
            height = _startHeight - dy;
        }
        if (edge is Edge.Bottom or Edge.BottomLeft or Edge.BottomRight)
            height = _startHeight + dy;
        if (width < MinWidth)
        {
            if (edge is Edge.Left or Edge.TopLeft or Edge.BottomLeft)
                left -= MinWidth - width;
            width = MinWidth;
        }
        if (height < ExpandedMinHeight)
        {
            if (edge is Edge.Top or Edge.TopLeft or Edge.TopRight)
                top -= ExpandedMinHeight - height;
            height = ExpandedMinHeight;
        }
        Width = width;
        Height = height;
        Margin = new Thickness(left, top, 0, 0);
        _placed = true;
        ClampToParent();
    }

    private void EndPointer(object? sender, PointerReleasedEventArgs e)
    {
        var resized = _resize != null;
        _drag = false;
        _resize = null;
        e.Pointer.Capture(null);
        if (resized)
            Dispatcher.UIThread.Post(FlushBrowserLayout, DispatcherPriority.Background);
    }

    private void FlushBrowserLayout()
    {
        // The last drag step can land after the browser has already been arranged
        // at the previous size. Queue one more pass so Chromium sees the final slot.
        _browser?.InvalidateMeasure();
        _browser?.InvalidateArrange();
        ScheduleMobileViewport();
    }

    private void ScheduleMobileViewport()
    {
        if (_browser == null || _viewportPosted)
            return;
        _viewportPosted = true;
        Dispatcher.UIThread.Post(() => _ = PublishMobileViewport(), DispatcherPriority.Background);
    }

    private async Task PublishMobileViewport()
    {
        try
        {
            await Task.Delay(80);
            ApplyViewport(reload: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Pinned] mobile viewport {ex.Message}");
        }
        finally
        {
            _viewportPosted = false;
        }
    }

    private void ApplyViewport(bool reload)
    {
        var browser = _browser;
        if (browser == null)
            return;

        var slot = browser.Bounds;
        int width = (int)Math.Round(slot.Width);
        int height = (int)Math.Round(slot.Height);
        if (width < 50 || height < 50)
        {
            width = (int)PhoneWidth;
            height = (int)Math.Max(200, PhoneHeight - 48);
        }

        bool mobile = width < DesktopMinWidth;
        bool modeChanged = _appliedMobile != mobile;
        if (!reload && !modeChanged && width == _appliedWidth && height == _appliedHeight && _appliedMobile != null)
            return;

        _appliedWidth = width;
        _appliedHeight = height;
        _appliedMobile = mobile;
        bool needReload = reload || modeChanged;

        var viewport = new PageViewport
        {
            PresetId = mobile ? "pinned-mobile" : "pinned-desktop",
            Width = width,
            Height = height,
            Mobile = mobile,
            UserAgent = mobile ? PageViewPresets.AndroidUserAgent : null,
            Fit = false,
            Scale = 1
        };
        int token = needReload ? ++_mobileToken : _mobileToken;
        double dsf = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        if (dsf < 0.5 || double.IsNaN(dsf) || double.IsInfinity(dsf))
            dsf = 1;

        _ = Push();

        async Task Push()
        {
            try
            {
                await PageViewportClient.SubmitAsync(
                    () => browser.GetBrowserHost(),
                    viewport,
                    width,
                    height,
                    dsf);
                if (!needReload || token != _mobileToken)
                    return;
                await Task.Delay(150);
                if (token != _mobileToken)
                    return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token != _mobileToken || !ReferenceEquals(_browser, browser))
                        return;
                    var address = browser.Address;
                    if (string.IsNullOrWhiteSpace(address) || address.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                        return;
                    browser.Reload();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pinned] viewport {ex.Message}");
            }
        }
    }
}
