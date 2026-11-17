using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Core.NexShare.Models;
using NexZen.Desktop.Helpers;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Desktop.Handlers;
using NexZen.Desktop.Services;
using Avalonia.Platform.Storage;

namespace NexZen.Desktop;

public partial class MainWindow : Window
{
    private static DateTime _lastTopLevelDragOverLog = DateTime.MinValue;

    class TabState {
        public AvaloniaCefBrowser? Browser { get; set; }
        public List<string> History { get; set; } = new();
        public int HistoryIndex { get; set; } = -1;
        public bool IsNavigatingFromHistory { get; set; } = false;
        public PageViewport? Viewport { get; set; }
        public string? AppliedUserAgent { get; set; }
        public bool ContentFullscreen { get; set; }
        public bool AudioMuted { get; set; }
        /// <summary>Muted because Site privacy has Sound = Mute for the page's site (not by the user).</summary>
        public bool MutedBySite { get; set; }
        public int FaviconRequestVersion { get; set; }
    }

    private Dictionary<string, TabState> _tabs = new();
    private MainViewModel? _viewModel;
    private Window? _menuWindow;
    private NexZen.Desktop.Controls.SuperMenuControl _superMenuControl;
    private readonly PageFindUi _findUi = new();
    private readonly DispatcherTimer _passwordCapturePollTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _tabSwitcherCommitTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _tabSwitcherAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Window? _tabSwitcherWindow;
    private Border? _tabSwitcherPanel;
    private TranslateTransform? _tabSwitcherEntranceTransform;
    private int _tabSwitcherIndex = -1;
    private DateTime _tabSwitcherAnimationStarted;
    private bool _passwordCapturePollRunning;
    private bool _passwordAutofillPickerOpen;
    private int _vaultReminderStarted;
    private WindowNotificationManager? _notificationManager;
    private readonly ExtensionCatalog _extensionCatalog = new();
    private readonly NexZenExtensionHandler _extensionHandler = new();
    private bool _closingMenu = false;
    private int _openOnDeviceGeneration;
    private int _openOnRequestGeneration;
    private double _menuAnchorX;
    private double _menuAnchorY;
    private PageCopyContext? _copyContext;
    private bool _slideInNextTab;

    // Window title: "<active page title> - NexZen Desktop" (task windows keep their own title).
    /// <summary>
    /// Shows the centered address bar while the active tab is a new tab (about:blank),
    /// and puts the caret in it when it appears so the user can type right away.
    /// </summary>
    private void UpdateBlankPageAddressBar()
    {
        if (this.FindControl<NexZen.Desktop.Controls.BlankPageAddressBar>("BlankPageAddressBar") is not { } bar)
            return;
        var url = _viewModel?.ActiveTab?.Url;
        var show = _viewModel?.ActiveTab != null && BlankPage.IsBlank(url) && !_browsersParked;
        if (this.FindControl<Button>("BlankBackgroundButton") is { } backgroundButton)
            backgroundButton.IsVisible = show;
        if (bar.IsVisible == show)
            return;
        bar.IsVisible = show;
        if (show)
            Dispatcher.UIThread.Post(bar.FocusAddressBar, DispatcherPriority.Background);
        else
            bar.HideSuggestions();
    }

    private async void ChooseNewTabBackground_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose a background picture",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("Pictures")
                {
                    Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.gif", "*.bmp" },
                    MimeTypes = new[] { "image/*" },
                },
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null)
            return;
        var error = NewTabBackground.Set(path);
        if (error != null)
            ShowNotification("Background", error, NotificationType.Error);
    }

    private void RemoveNewTabBackground_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => NewTabBackground.Remove();

    /// <summary>The new-tab picture changed (in any window): reload the blank tabs of this window.</summary>
    private void OnNewTabBackgroundChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var state in _tabs.Values)
            if (state.Browser is { } b && BlankPage.IsBlank(b.Address))
                b.Reload();
    });

    private static NexZen.Application.Services.CustomStyleStore? CustomStyles
        => App.ServiceProvider?.GetService<NexZen.Application.Services.CustomStyleStore>();

    /// <summary>Puts the site's saved Custom CSS (if any, and switched on) into the page.</summary>
    internal static void ApplyCustomCss(Xilium.CefGlue.CefFrame? frame)
    {
        try
        {
            var url = frame?.Url;
            var css = CustomStyles?.CssForPage(url);
            if (css != null)
                frame!.ExecuteJavaScript(NexZen.Application.Services.CustomCss.InjectionScript(css), url, 0);
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write("[CustomCss] inject failed: " + ex.Message);
        }
    }

    /// <summary>SuperMenu › Tools › Custom CSS: edit the active tab's site stylesheet.</summary>
    private void OpenCustomCss()
    {
        CloseSuperMenu();
        var tab = _viewModel?.ActiveTab;
        if (tab == null || NexZen.Application.Services.CustomCss.SiteKey(tab.Url) == null)
        {
            ShowNotification("Custom CSS", "Open a web page first.", NotificationType.Warning);
            return;
        }
        var tabId = tab.Id;
        void Reload()
        {
            if (_tabs.TryGetValue(tabId, out var state) && state.Browser != null)
                state.Browser.Reload();
        }
        if (Avalonia.Application.Current is App app)
            app.ShowCustomCssWindow(tab.Url, Reload);
    }

    /// <summary>The browser window that was focused most recently (main or independent).</summary>
    internal static WeakReference<MainWindow>? LastActiveBrowserWindow { get; private set; }

    /// <summary>Tabs of the most recently focused browser window that is still open.</summary>
    internal static MainViewModel? LastActiveTabs(MainViewModel? fallback)
        => LastActiveBrowserWindow != null && LastActiveBrowserWindow.TryGetTarget(out var window)
           && window.IsVisible && window._viewModel != null
            ? window._viewModel
            : fallback;

    /// <summary>The browser window to dock NexZen Mirror into: the last focused one, else the main window.</summary>
    internal static MainWindow? LastBrowserWindow()
    {
        if (LastActiveBrowserWindow != null && LastActiveBrowserWindow.TryGetTarget(out var window) && window.IsVisible)
            return window;
        return (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow as MainWindow;
    }

    // ---- NexZen Mirror beside the web page ------------------------------------------------------

    private sealed class DockedMirrorHost : NexZen.Desktop.Mirror.IMirrorHost
    {
        private readonly MainWindow _w;
        public DockedMirrorHost(MainWindow w) => _w = w;
        public bool IsDocked => true;
        public void SetTitle(string title) { }
        public void FitVideo(int width, int height) => _w.FitDockedMirror(width, height);
        public void CloseMirror() => _w.UndockMirror(keepRunning: false);
        public void ToggleDock() => _w.UndockMirror(keepRunning: true);
    }

    private NexZen.Desktop.Mirror.MirrorView? _dockedMirror;
    private Border? _dockedMirrorPanel;
    private GridSplitter? _dockedMirrorSplitter;

    /// <summary>
    /// Shows a running mirror session in a column on the right: the page on the left, a splitter, the
    /// phone on the right. Every other child of MainGrid stays in the page column.
    /// </summary>
    internal void DockMirror(NexZen.Desktop.Mirror.MirrorView view)
    {
        if (_dockedMirror != null)
            UndockMirror(keepRunning: true); // one docked mirror per window; the old one gets its own window
        if (this.FindControl<Grid>("MainGrid") is not { } grid)
            return;
        _dockedMirror = view;
        view.Host = new DockedMirrorHost(this);
        _dockedMirrorPanel = new Border { Background = Avalonia.Media.Brushes.Black, Child = view, ClipToBounds = true };
        _dockedMirrorSplitter = new GridSplitter
        {
            Width = 6, Background = Avalonia.Media.Brush.Parse("#2A2A2A"), ResizeDirection = GridResizeDirection.Columns,
        };
        grid.ColumnDefinitions = new ColumnDefinitions
        {
            new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 320 },
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(new GridLength(DockedMirrorWidth(9, 20))) { MinWidth = 200 },
        };
        Grid.SetColumn(_dockedMirrorSplitter, 1);
        Grid.SetColumn(_dockedMirrorPanel, 2);
        grid.Children.Add(_dockedMirrorSplitter);
        grid.Children.Add(_dockedMirrorPanel);
        Activate();
        view.FocusTyping();
    }

    /// <summary>Width for the mirror column so the phone fills the window's height.</summary>
    private double DockedMirrorWidth(int width, int height)
    {
        var h = Math.Max(300, Bounds.Height - 34);
        return Math.Clamp(Math.Round(h * width / height), 200, Math.Max(240, Bounds.Width * 0.6));
    }

    private void FitDockedMirror(int width, int height)
    {
        if (this.FindControl<Grid>("MainGrid") is { ColumnDefinitions.Count: 3 } grid)
            grid.ColumnDefinitions[2].Width = new GridLength(DockedMirrorWidth(width, height));
    }

    /// <summary>Removes the mirror column; keepRunning moves the session to its own window, else it stops.</summary>
    private void UndockMirror(bool keepRunning)
    {
        var view = _dockedMirror;
        if (view == null) return;
        _dockedMirror = null;
        if (this.FindControl<Grid>("MainGrid") is { } grid)
        {
            if (_dockedMirrorPanel != null) grid.Children.Remove(_dockedMirrorPanel);
            if (_dockedMirrorSplitter != null) grid.Children.Remove(_dockedMirrorSplitter);
            grid.ColumnDefinitions.Clear();
        }
        if (_dockedMirrorPanel != null) _dockedMirrorPanel.Child = null;
        _dockedMirrorPanel = null;
        _dockedMirrorSplitter = null;
        if (keepRunning)
            new NexZen.Desktop.Mirror.MirrorWindow(view, start: false).Show();
        else
            view.Shutdown();
    }

    /// <summary>Set when the window was opened from a workspace; shown first in the title.</summary>
    internal string? WorkspaceName
    {
        get => _workspaceName;
        set
        {
            _workspaceName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            UpdateWindowTitle();
        }
    }
    private string? _workspaceName;

    /// <summary>Workspace border color (#RRGGBB) drawn around the window, or null for none.</summary>
    internal string? WorkspaceColor
    {
        get => _workspaceColor;
        set
        {
            _workspaceColor = value;
            if (this.FindControl<Border>("WorkspaceFrame") is not { } frame)
                return;
            if (!string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out var color))
            {
                frame.BorderBrush = new SolidColorBrush(color);
                frame.IsVisible = true;
            }
            else
                frame.IsVisible = false;
        }
    }
    private string? _workspaceColor;

    /// <summary>Brings this window forward on <paramref name="url"/>'s tab, adding the tab if it was closed.</summary>
    internal void ShowTab(string? url)
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (_viewModel == null || string.IsNullOrWhiteSpace(url))
            return;
        static string Key(string? u) => (u ?? "").Trim().TrimEnd('/').ToLowerInvariant();
        static string Host(string? u) => Uri.TryCreate(u, UriKind.Absolute, out var p)
            ? (p.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? p.Host[4..] : p.Host).ToLowerInvariant()
            : "";
        // Exact address first; else the same site (the page may have redirected or moved on).
        var tab = _viewModel.Tabs.FirstOrDefault(t => Key(t.Url) == Key(url))
                  ?? _viewModel.Tabs.FirstOrDefault(t => Host(t.Url) is { Length: > 0 } h && h == Host(url));
        if (tab != null)
            _viewModel.SwitchTab(tab.Id);
        else
            _viewModel.AddTab(url);
    }

    private void UpdateWindowTitle()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXZEN_TASK_ID")))
            return;
        Title = NexZen.Application.Services.WindowTitle.Compose(_workspaceName, _viewModel?.ActiveTab?.Title);
        if (_incognito)
            Title = "Incognito - " + Title;
    }
    private int _historySlideToken;
    private int _slideOutToken;
    private Image? _slideOutOverlay;
    private bool _osFullscreen;
    private bool _restoreNormalBounds;
    private bool _restorePageViewBar;
    private WindowState _restoreWindowState;
    private WindowDecorations _restoreDecorations;
    private PixelPoint _restorePosition;
    private double _restoreWidth;
    private double _restoreHeight;
    private int _fullscreenMetricsWidth;
    private int _fullscreenMetricsHeight;
    private int _pageViewSubmit;
    private bool _browsersParked;
    private readonly bool _isIndependentWindow;
    private Controls.PinnedPane? _pinnedPane;
    private readonly List<Controls.PinnedPane> _pinnedPanes = new();
    private readonly Dictionary<AvaloniaCefBrowser, DomainLockBrowserSession> _domainLockSessions = new();
    private const int MaxPinnedWindows = 5;
    private int _pinnedCascadeSequence;
    private AvaloniaCefBrowser? _actionBrowser;
    private int _pinShareGeneration;
    private List<DiscoveryMessage>? _pinShareDevices;
    private string? _pinShareUrl;
    private string? _pinShareTitle;
    private const double MenuEdgePadding = 8;
    private const int PageViewMin = 200;
    private const int PageViewMax = 3840;

    private NexZenExtensionBridge? _extensionBridge;

    public MainWindow() : this(null, "https://google.com", false)
    {
    }

    internal MainWindow(MainViewModel viewModel, string initialUrl) : this(viewModel, initialUrl, true)
    {
    }

    /// <summary>
    /// An incognito window: the full NexZen window on an in-memory CEF profile shared by all
    /// incognito windows (gone when the last one closes); no history, downloads list, saved
    /// passwords or session restore.
    /// </summary>
    internal static MainWindow CreateIncognito(MainViewModel viewModel, string initialUrl)
    {
        viewModel.IsIncognito = true;
        return new MainWindow(viewModel, initialUrl, true, incognito: true);
    }

    private readonly bool _incognito;

    /// <summary>
    /// Incognito marks that never cover the page: a purple frame around the window and a small
    /// "Incognito" badge at the top right (both ignore the mouse).
    /// </summary>
    private void AddIncognitoMarks(Grid grid)
    {
        var accent = Avalonia.Media.Color.Parse("#8B5CF6");
        var frame = new Border
        {
            BorderBrush = new Avalonia.Media.SolidColorBrush(accent),
            BorderThickness = new Thickness(3),
            IsHitTestVisible = false,
            ZIndex = 10_000,
        };
        var badge = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E6201A2E")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 12, 4),
            Margin = new Thickness(0, 8, 14, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0.92,
            ZIndex = 10_001,
            Child = new TextBlock
            {
                Text = "🕶  Incognito",
                FontSize = 12,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Foreground = Avalonia.Media.Brushes.White,
            },
        };
        ToolTip.SetTip(badge, "Incognito window: history, downloads and passwords are not saved; cookies are cleared when the last incognito window closes.");
        grid.Children.Add(frame);
        grid.Children.Add(badge);
    }

    private MainWindow(MainViewModel? viewModel, string? initialUrl, bool isIndependentWindow, bool incognito = false)
    {
        _isIndependentWindow = isIndependentWindow;
        _incognito = incognito;
        if (incognito)
        {
            Views.IncognitoSession.RegisterWindow();
            Closed += (_, _) => Views.IncognitoSession.ReleaseWindow();
        }
        if (!isIndependentWindow)
            StartupTimer.MarkOnce("MainWindow XAML load");
        InitializeComponent();
        if (!isIndependentWindow)
        {
            StartupTimer.MarkOnce("MainWindow XAML loaded");
            Opened += (_, _) => StartupTimer.MarkOnce("MainWindow opened (visible)");
        }
        // Bookmarks, history, downloads and saved passwords load once the window is up
        // (every window: independent windows have their own view model).
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = _viewModel?.EnsureInitialDataLoadedAsync(),
            DispatcherPriority.Background);
        _passwordCapturePollTimer.Tick += async (_, _) => await PollPasswordCaptureQueuesAsync();
        _passwordCapturePollTimer.Start();
        Closed += (_, _) => _passwordCapturePollTimer.Stop();
        _tabSwitcherCommitTimer.Tick += (_, _) => CommitTabSwitcher();
        _tabSwitcherAnimationTimer.Tick += (_, _) => AnimateTabSwitcherEntrance();
        if (!isIndependentWindow)
            Opened += async (_, _) => await CheckPasswordVaultSetupReminderAsync();
        if (this.FindControl<Grid>("MainGrid") is { } findGrid)
            _findUi.Mount(findGrid);
        NewTabBackground.Changed += OnNewTabBackgroundChanged;
        Closed += (_, _) => UndockMirror(keepRunning: false);
        Closed += (_, _) => NewTabBackground.Changed -= OnNewTabBackgroundChanged;
        if (_incognito && this.FindControl<Grid>("MainGrid") is { } incognitoGrid)
            AddIncognitoMarks(incognitoGrid);
        
        var taskId = Environment.GetEnvironmentVariable("NEXZEN_TASK_ID");
        if (!string.IsNullOrEmpty(taskId))
        {
            this.Title = $"NexZen - {taskId}";
        }
        
        _viewModel = viewModel ?? App.ServiceProvider?.GetRequiredService<MainViewModel>();
        // Workspace "this window" actions use the browser window the user was last in.
        Activated += (_, _) => LastActiveBrowserWindow = new WeakReference<MainWindow>(this);
        if (!_isIndependentWindow)
            StartupTimer.MarkOnce("MainViewModel resolved");
        if (_viewModel != null)
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ActiveTab))
                    Dispatcher.UIThread.Post(UpdateWindowTitle);
                if (e.PropertyName is nameof(MainViewModel.ActiveTab) or nameof(MainViewModel.CurrentUrl))
                    Dispatcher.UIThread.Post(UpdateBlankPageAddressBar);
            };
        DataContext = _viewModel;
        if (this.FindControl<NexZen.Desktop.Controls.BlankPageAddressBar>("BlankPageAddressBar") is { } blankBar)
        {
            blankBar.NavigateRequested += (_, url) => NavigateFromMenu(url);
            blankBar.OpenInNewTabRequested += (_, url) => OpenLinkInNewTab(url);
            blankBar.LockDomainRequested += async (_, _) => await LockCurrentDomainAsync();
        }
        
        if (_viewModel != null && !_isIndependentWindow)
        {
            _extensionBridge = new NexZenExtensionBridge(_viewModel);
            _extensionBridge.Start();
            StartupTimer.MarkOnce("extension bridge started");
            
            this.Closed += (s, e) => _extensionBridge.Stop();
        }

        // Wire up Tab events
        if (_viewModel != null)
        {
            _viewModel.TabAdded += OnTabAdded;
            _viewModel.TabClosed += OnTabClosed;
            _viewModel.TabSwitched += OnTabSwitched;
            
            // NexShare toasts are for real pairing and transfer events.
            // Startup lines ("Starting service...", "Started listening...") stay quiet.
            _notificationManager = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
            if (!_isIndependentWindow)
            {
                _viewModel.NexShare.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName != nameof(_viewModel.NexShare.ConnectionStatus))
                        return;
                    var status = _viewModel.NexShare.ConnectionStatus;
                    if (IsQuietNexShareStatus(status))
                        return;
                    var type = status.Contains("Error", StringComparison.OrdinalIgnoreCase) || status.Contains("Lỗi", StringComparison.OrdinalIgnoreCase)
                               ? NotificationType.Error
                               : NotificationType.Information;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (type == NotificationType.Error) ShowNotification("NexShare", status, NotificationType.Error);
                        else _notificationManager?.Show(new Notification("NexShare", status, type));
                    });
                };
            }
            
            AddHandler(KeyDownEvent, OnContentFullscreenKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnBrowserZoomKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(KeyDownEvent, OnTabSwitcherKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnTabSwitcherKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            // Register the native window as a drop target as well as the browser
            // control. Avalonia's platform drag target is attached at the TopLevel;
            // without this, OS file drags may never be routed into child controls.
            DragDrop.SetAllowDrop(this, true);
            Console.WriteLine("[CEF-DND] MainWindow drop target enabled");
            const Avalonia.Interactivity.RoutingStrategies dragRoutes =
                Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble;
            AddHandler(DragDrop.DragEnterEvent, OnTopLevelDragEnter, dragRoutes, handledEventsToo: true);
            AddHandler(DragDrop.DragOverEvent, OnTopLevelDragOver, dragRoutes, handledEventsToo: true);
            AddHandler(DragDrop.DragLeaveEvent, OnTopLevelDragLeave, dragRoutes, handledEventsToo: true);
            AddHandler(DragDrop.DropEvent, OnTopLevelDrop, dragRoutes, handledEventsToo: true);
            if (this.FindControl<Grid>("MainGrid") is { } dragGrid)
            {
                DragDrop.SetAllowDrop(dragGrid, true);
                Console.WriteLine("[CEF-DND] MainGrid drop target enabled");
                dragGrid.AddHandler(DragDrop.DragEnterEvent, OnBrowserAreaDragEnter,
                    Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                dragGrid.AddHandler(DragDrop.DragOverEvent, OnBrowserAreaDragOver,
                    Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                dragGrid.AddHandler(DragDrop.DropEvent, OnBrowserAreaDrop,
                    Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            }

            // Setup global Ctrl+M Hotkey
            this.AddHandler(InputElement.KeyDownEvent, (s, e) =>
            {
                if (e.Key == Avalonia.Input.Key.M && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    e.Handled = true;
                    ForceOpenSuperMenuCenter();
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnReopenClosedTabKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnNewTabKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnDevToolsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnNewIncognitoKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnPrintKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnAiFunctionKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnWorkspaceKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            this.AddHandler(InputElement.KeyDownEvent, OnFindKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // Setup Failsafe Button
            var failsafeBtn = this.FindControl<Button>("FailsafeMenuButton");
            if (failsafeBtn != null)
            {
                failsafeBtn.Click += (s, e) => ForceOpenSuperMenuCenter();
            }
        }
        
        void ForceOpenSuperMenuCenter()
        {
            var activeState = GetActiveTabState();
            if (activeState?.Browser != null)
            {
                var context = new PageCopyContext();
                // Open roughly in center top
                int x = (int)(this.Bounds.Width / 2 - 200);
                int y = 50;
                if (x < 0) x = 50;
                ShowSuperMenu(x, y, context);
            }
        }

        // Create SuperMenu control once, wire up events
        _superMenuControl = new NexZen.Desktop.Controls.SuperMenuControl();
        _superMenuControl.DataContext = _viewModel;
        _superMenuControl.LayoutChanged += (_, _) =>
        {
            if (_menuWindow != null)
                PositionSuperMenu();
        };
        _superMenuControl.ClearDataRequested += (_, request) =>
        {
            _ = ClearBrowsingDataFromMenuAsync(request);
        };
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.TabColumns) && _menuWindow != null)
                    PositionSuperMenu();
            };
            _viewModel.Tabs.CollectionChanged += (_, _) => _superMenuControl.SetTabCount(_viewModel.Tabs.Count);
            _superMenuControl.SetTabCount(_viewModel.Tabs.Count);
        }

        SizeChanged += (_, _) =>
        {
            if (_menuWindow != null && !_closingMenu)
            {
                SyncMenuOverlay();
                PositionSuperMenu();
            }

            ResizeTabSwitcherOverlay();

            ReflowPageView();
            foreach (var pane in _pinnedPanes)
                pane.ClampToParent();
        };
        PositionChanged += (_, _) =>
        {
            var windowPosition = this.PointToScreen(new Point(0, 0));
            if (_menuWindow != null && !_closingMenu)
                _menuWindow.Position = windowPosition;
            if (_tabSwitcherWindow != null)
                _tabSwitcherWindow.Position = windowPosition;
        };
        
        _superMenuControl.BackRequested += (_, _) => NavigateHistory(back: true);
        _superMenuControl.ForwardRequested += (_, _) => NavigateHistory(back: false);
        
        _superMenuControl.BackHistoryRequested += (s, e) => {
            var state = GetActiveTabState();
            if (state != null)
            {
                var backHistory = state.History.Take(state.HistoryIndex).Reverse().ToList();
                _superMenuControl.ShowHistoryList(backHistory);
            }
        };
        
        _superMenuControl.HistoryItemClicked += (s, url) => {
            var state = GetActiveTabState();
            if (state?.Browser != null) {
                state.IsNavigatingFromHistory = true;
                state.HistoryIndex = state.History.IndexOf(url);
                state.Browser.Address = BlankPage.ForBrowser(url);
            }
            CloseSuperMenu();
        };

        _superMenuControl.RefreshRequested += (s, e) => {
            ShowProgressBar(true);
            var state = GetActiveTabState();
            if (state?.Browser != null) state.Browser.Reload();
            CloseSuperMenu();
        };
        _superMenuControl.WorkspaceRequested += (_, _) => OpenWorkspace();

        _superMenuControl.NavigateRequested += (_, url) => NavigateFromMenu(url);
        _superMenuControl.LockDomainRequested += async (_, _) => await LockCurrentDomainAsync();

        _superMenuControl.NewTabRequested += (s, e) => {
            _viewModel?.AddTab("about:blank");
            // Focus the SuperMenu address bar once the new tab (and its browser,
            // which may grab focus) has been created and CurrentUrl has updated.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _superMenuControl.FocusAddressBar(),
                Avalonia.Threading.DispatcherPriority.Background);
        };
        _superMenuControl.SwitchTabRequested += (_, tabId) =>
        {
            _viewModel?.SwitchTab(tabId);
            CloseSuperMenu();
        };
        _superMenuControl.CloseTabRequested += (_, tabId) => _viewModel?.CloseTab(tabId);
        _superMenuControl.OpenTabInNewWindowRequested += (_, tabId) => OpenTabInNewWindow(tabId);
        _superMenuControl.CloseActiveTabRequested += (_, _) =>
        {
            var id = _viewModel?.ActiveTab?.Id;
            if (!string.IsNullOrEmpty(id))
                _viewModel?.CloseTab(id);
        };
        _superMenuControl.ReopenClosedTabRequested += (_, _) => ReopenClosedTabFromUi();

        _superMenuControl.OpenLinkInNewTabRequested += (_, _) => OpenLinkInNewTab(NewTabTarget(_copyContext));
        _superMenuControl.OpenInNewWindowRequested += (_, _) => OpenInNewWindowFromMenu();
        _superMenuControl.NewIncognitoWindowRequested += (_, _) => OpenIncognitoWindow();
        _superMenuControl.OpenOnPinnedWindowRequested += (_, _) => OpenOnPinnedWindowFromMenu();
        _superMenuControl.MuteTabRequested += (_, _) => ToggleMuteActiveTab();
        _superMenuControl.ReadAloudRequested += (_, _) => _ = ReadAloudFromMenuAsync();
        _superMenuControl.NexZenVoiceRequested += (_, _) =>
        {
            var selection = _copyContext is { IsImage: false } c ? c.SelectionText : null;
            CloseSuperMenu();
            Speech.VoiceWindow.Open(this, selection);
        };
        MountReadAloudBar();
        _superMenuControl.InspectRequested += (_, _) =>
        {
            var copy = _copyContext;
            CloseSuperMenu();
            OpenDevTools(copy?.X ?? 0, copy?.Y ?? 0);
        };
        // A new Sound choice in Site privacy applies to the open page right away.
        Services.SiteDataService.SoundChanged += browser => Dispatcher.UIThread.Post(() =>
        {
            foreach (var state in _tabs.Values)
            {
                if (ReferenceEquals(state.Browser, browser))
                    ApplySiteSound(state, browser, browser.Address ?? "");
            }
        });
        _superMenuControl.SitePrivacyRequested += (_, _) =>
        {
            CloseSuperMenu();
            var tab = _viewModel?.ActiveTab;
            if (Avalonia.Application.Current is App app)
                app.ShowSitePrivacyWindow(this, tab?.Url, tab?.FaviconPng, GetActiveTabState()?.Browser);
        };
        _superMenuControl.PageViewRequested += (_, _) =>
        {
            CloseSuperMenu();
            OpenPageView();
        };
        _superMenuControl.CreateQrRequested += (_, _) => _ = CreateQrFromMenuAsync();
        _superMenuControl.CustomCssRequested += (_, _) => OpenCustomCss();
        _superMenuControl.AiFunctionRequested += (_, _) =>
        {
            CloseSuperMenu();
            if (Avalonia.Application.Current is App app)
                app.ShowAiFunctionWindow();
        };
        _superMenuControl.SearchWithRequested += (_, _) => _ = SearchFromMenuAsync();

        var pageViewBar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        if (pageViewBar != null)
        {
            pageViewBar.ViewportChanged += (_, _) => OnPageViewEdited();
            pageViewBar.CloseRequested += (_, _) =>
            {
                pageViewBar.IsVisible = false;
                var state = GetActiveTabState();
                if (state != null)
                    LayoutPageView(state, pushMetrics: state.Viewport != null, reload: false);
            };
        }
        _superMenuControl.ShareDeviceSelected += async (_, device) =>
        {
            string? url = _copyContext?.LinkUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = _viewModel?.ActiveTab?.Url;
            }
            if (!string.IsNullOrEmpty(url) && _viewModel?.NexShare != null && device != null)
            {
                string title = _viewModel.ActiveTab?.Title ?? "Shared Link";
                await _viewModel.NexShare.SendUrlToDeviceAsync(device, url, title);
            }
        };
        _superMenuControl.OpenOnAnotherDeviceRequested += (_, _) => _ = OpenOnAnotherDeviceAsync();

        _superMenuControl.CopyRequested += async (_, _) =>
        {
            try
            {
                await CopyFromPageAsync();
            }
            catch (Exception ex)
            {
                ShowCopyNotification("Copy", "Couldn't copy.", null, NotificationType.Error);
                Console.WriteLine($"[Copy] {ex}");
            }
        };

        _superMenuControl.CutRequested += async (_, _) =>
        {
            try
            {
                await CutFromMenuAsync();
            }
            catch (Exception ex)
            {
                ShowCopyNotification("Cut", "Couldn't cut.", null, NotificationType.Error);
                Console.WriteLine($"[Cut] {ex}");
            }
        };

        _superMenuControl.PrintRequested += (_, _) => PrintFromMenu();

        _superMenuControl.PasteRequested += async (_, _) =>
        {
            try
            {
                await PasteFromMenuAsync();
            }
            catch (Exception ex)
            {
                ShowCopyNotification("Paste", "Couldn't paste.", null, NotificationType.Error);
                Console.WriteLine($"[Paste] {ex}");
            }
        };

        _extensionHandler.Loaded += OnExtensionLoaded;
        _extensionHandler.Failed += error => Dispatcher.UIThread.Post(() =>
            ShowCopyNotification("Extension", "Couldn't run the extension: " + error, null, NotificationType.Error));
        _extensionHandler.Unloaded += extension => Dispatcher.UIThread.Post(() => MarkExtensionStopped(extension));
        if (_viewModel != null)
        {
            foreach (var installed in _extensionCatalog.Load())
                _viewModel.Extensions.Add(installed);
        }

        _superMenuControl.InstallExtensionRequested += async (_, _) =>
        {
            try { await InstallExtensionAsync(); }
            catch (Exception ex)
            {
                ShowCopyNotification("Extension", ex.Message, null, NotificationType.Warning);
            }
        };
        _superMenuControl.InstallCurrentStoreExtensionRequested += async (_, _) =>
        {
            var address = GetActiveTabState()?.Browser?.Address;
            if (!WebStoreInstaller.TryParseStoreId(address, out var id))
            {
                ShowCopyNotification("Extension", "Open the extension's detail page in the store.", null, NotificationType.Warning);
                return;
            }

            await InstallFromWebStoreAsync(id);
        };
        _superMenuControl.RunExtensionRequested += (_, id) => RunExtension(id);
        _superMenuControl.StopExtensionRequested += (_, id) => StopExtension(id);
        _superMenuControl.DeleteExtensionRequested += (_, id) => DeleteExtension(id);

        _superMenuControl.SaveImageRequested += async (_, _) =>
        {
            try
            {
                await SaveImageFromMenuAsync();
            }
            catch (Exception ex)
            {
                ShowCopyNotification("Save Image", "Couldn't save the image.", null, NotificationType.Error);
                Console.WriteLine($"[SaveImage] {ex}");
            }
        };
        _superMenuControl.CopyImageAddressRequested += async (_, _) =>
        {
            try
            {
                await CopyImageAddressFromMenuAsync();
            }
            catch (Exception ex)
            {
                ShowCopyNotification("Copy image address", "Couldn't copy the image address.", null, NotificationType.Error);
                Console.WriteLine($"[CopyImageAddress] {ex}");
            }
        };

        AddHandler(KeyDownEvent, OnEditKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // Create initial tab
        if (!_isIndependentWindow)
            StartupTimer.MarkOnce("first tab add");
        // NEXZEN_START_URL: open this page in the first tab (testing / debugging).
        var startUrl = _isIndependentWindow ? null : Environment.GetEnvironmentVariable("NEXZEN_START_URL");
        _viewModel?.AddTab(initialUrl ?? (string.IsNullOrWhiteSpace(startUrl) ? "https://google.com" : startUrl));
        if (!_isIndependentWindow)
            StartupTimer.MarkOnce("MainWindow constructor done");
    }

    public bool ForceClose { get; set; } = false;

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        CloseTabSwitcher(commit: false);

        if (_isIndependentWindow)
        {
            CloseIndependentWindowResources();
            base.OnClosing(e);
            return;
        }

        if (!ForceClose)
        {
            e.Cancel = true;
            Program.StartupLog.Write("[MainWindowClose] close requested; parking browsers");
            try
            {
                ParkBrowsers();
                Hide();
                Program.StartupLog.Write("[MainWindowClose] browser parking complete; main window hidden");
            }
            catch (Exception ex)
            {
                Program.StartupLog.Write($"[MainWindowClose] close handling failed: {ex}");
                try { Hide(); }
                catch (Exception hideException)
                {
                    Program.StartupLog.Write($"[MainWindowClose] hide failed: {hideException}");
                }
            }
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private void CloseIndependentWindowResources()
    {
        _passwordCapturePollTimer.Stop();
        CloseTabSwitcher(commit: false);
        CloseSuperMenu();
        _findUi.Close();
        foreach (var pane in _pinnedPanes.ToArray())
            pane.ClosePin();

        var grid = this.FindControl<Grid>("MainGrid");
        foreach (var (tabId, state) in _tabs.ToArray())
        {
            var browser = state.Browser;
            if (browser == null)
                continue;
            try
            {
                browser.CloseDeveloperTools();
                if (_findUi.Targets(browser))
                    _findUi.ForgetTarget();
                if (grid?.Children.Contains(browser) == true)
                    grid.Children.Remove(browser);
                browser.Dispose();
            }
            catch (Exception ex)
            {
                Program.StartupLog.Write($"[NewWindowClose] browser cleanup failed tab={tabId} type={ex.GetType().Name}");
            }
        }
        _tabs.Clear();
        if (_viewModel != null)
        {
            _viewModel.TabAdded -= OnTabAdded;
            _viewModel.TabClosed -= OnTabClosed;
            _viewModel.TabSwitched -= OnTabSwitched;
        }
    }

    private TabState? GetActiveTabState()
    {
        if (_viewModel?.ActiveTab?.Id != null && _tabs.TryGetValue(_viewModel.ActiveTab.Id, out var state))
            return state;
        return null;
    }

    public async Task<string?> TryExtractPageJsonAsync(string tabId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => TryExtractPageJsonAsync(tabId));

        if (string.IsNullOrWhiteSpace(tabId) || !_tabs.TryGetValue(tabId, out var state))
            return null;
        var browser = state.Browser;
        if (browser == null || !browser.IsBrowserInitialized)
            return null;
        var address = browser.Address ?? "";
        if (BlankPage.IsBlank(address))
            return null;
        try
        {
            return await browser.EvaluateJavaScript<string>(
                PageContentExtractScript.Script,
                url: address,
                timeout: TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[AI Function] extract failed type={ex.GetType().Name}");
            return null;
        }
    }

    public async Task<string?> TryExtractRemotePageJsonAsync(string tabId, string pageUrl)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => TryExtractRemotePageJsonAsync(tabId, pageUrl));

        if (string.IsNullOrWhiteSpace(tabId) || string.IsNullOrWhiteSpace(pageUrl)
            || !_tabs.TryGetValue(tabId, out var state))
            return null;
        var browser = state.Browser;
        if (browser == null || !browser.IsBrowserInitialized)
            return null;
        var address = browser.Address ?? "";
        if (BlankPage.IsBlank(address))
            return null;
        try
        {
            return await browser.EvaluateJavaScript<string>(
                PageContentExtractScript.RemotePageScript(pageUrl),
                url: address,
                timeout: TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[AI Function] thread page extract failed type={ex.GetType().Name}");
            return null;
        }
    }

    private string GetCurrentPageAddress()
    {
        var browser = GetActiveTabState()?.Browser;
        if (browser == null)
            return _viewModel?.ActiveTab?.Url ?? string.Empty;
        if (_domainLockSessions.TryGetValue(browser, out var session)
            && session.TryGetChallenge(browser.Address, out var targetUrl))
            return targetUrl;
        return browser.Address;
    }

    private Controls.PinnedPane CreatePinnedPane()
    {
        var pane = new Controls.PinnedPane
        {
            CascadeIndex = _pinnedCascadeSequence++
        };
        pane.Bind(_viewModel);
        pane.Activated += (_, _) => _pinnedPane = pane;
        pane.Closed += (_, _) =>
        {
            _pinnedPanes.Remove(pane);
            this.FindControl<Grid>("MainGrid")?.Children.Remove(pane);
            if (ReferenceEquals(_pinnedPane, pane))
                _pinnedPane = _pinnedPanes.LastOrDefault();
        };
        pane.CutRequested += async (_, copy) =>
        {
            _pinnedPane = pane;
            try { await RunPinEditAsync(copy, CutFromMenuAsync, pane); }
            catch (Exception ex)
            {
                ShowCopyNotification("Cut", "Couldn't cut.", null, NotificationType.Error);
                Console.WriteLine($"[Cut] {ex}");
            }
        };
        pane.CopyRequested += async (_, copy) =>
        {
            _pinnedPane = pane;
            try { await RunPinEditAsync(copy, CopyFromPageAsync, pane); }
            catch (Exception ex)
            {
                ShowCopyNotification("Copy", "Couldn't copy.", null, NotificationType.Error);
                Console.WriteLine($"[Copy] {ex}");
            }
        };
        pane.PasteRequested += async (_, copy) =>
        {
            _pinnedPane = pane;
            try { await RunPinEditAsync(copy, PasteFromMenuAsync, pane); }
            catch (Exception ex)
            {
                ShowCopyNotification("Paste", "Couldn't paste.", null, NotificationType.Error);
                Console.WriteLine($"[Paste] {ex}");
            }
        };
        pane.SaveImageRequested += async (_, copy) =>
        {
            _pinnedPane = pane;
            try { await RunPinEditAsync(copy, SaveImageFromMenuAsync, pane); }
            catch (Exception ex)
            {
                ShowCopyNotification("Save Image", "Couldn't save the image.", null, NotificationType.Error);
                Console.WriteLine($"[SaveImage] {ex}");
            }
        };
        pane.BrowserCreated += (_, browser) => _findUi.Attach(browser);
        pane.BrowserDisposed += (_, _) =>
        {
            if (_findUi.IsPin)
                _findUi.Close();
        };
        pane.OpenDeviceRequested += (_, copy) =>
        {
            _pinnedPane = pane;
            _ = OpenPinnedOnAnotherDeviceAsync(copy);
        };
        pane.ChoiceSelected += async (_, index) =>
        {
            var devices = _pinShareDevices;
            var url = _pinShareUrl;
            var title = string.IsNullOrWhiteSpace(_pinShareTitle) ? "Shared Link" : _pinShareTitle;
            Interlocked.Increment(ref _pinShareGeneration);
            pane.CloseMenu();
            if (devices == null || string.IsNullOrWhiteSpace(url) || _viewModel?.NexShare == null)
                return;
            if (index < 0 || index >= devices.Count)
                return;
            await SendSharedUrlAsync(devices[index], url, title);
        };

        _pinnedPanes.Add(pane);
        _pinnedPane = pane;
        this.FindControl<Grid>("MainGrid")?.Children.Add(pane);
        return pane;
    }

    private CancellationTokenSource? _progressBarCts;

    private void ShowProgressBar(bool isVisible)
    {
        Dispatcher.UIThread.Post(async () => {
            var popup = this.FindControl<Avalonia.Controls.Primitives.Popup>("ProgressBarPopup");
            if (popup != null) popup.IsOpen = isVisible;
            
            if (isVisible)
            {
                _progressBarCts?.Cancel();
                _progressBarCts = new CancellationTokenSource();
                try
                {
                    await Task.Delay(5000, _progressBarCts.Token);
                    if (popup != null) popup.IsOpen = false;
                }
                catch (TaskCanceledException) { }
            }
        });
    }

    private int? _currentRefreshRate;
    
    private async Task UpdateRefreshRateAsync(AvaloniaCefBrowser browser)
    {
        if (_currentRefreshRate.HasValue) 
        {
            ApplyRefreshRate(browser, _currentRefreshRate.Value);
            return;
        }

        int rate = 60;
        try
        {
            var env = Environment.GetEnvironmentVariable("NEXZEN_FRAMERATE");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int parsed) && parsed > 0)
            {
                rate = parsed;
            }
            else if (Xilium.CefGlue.Common.GpuPaint.Enabled)
            {
                rate = Xilium.CefGlue.Common.GpuPaint.FrameRate;
            }
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                rate = await Task.Run(() => 
                {
                    try {
                        var process = new System.Diagnostics.Process()
                        {
                            StartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "bash",
                                Arguments = "-c \"xrandr 2>/dev/null | grep '*' | awk '{print $2}'\"",
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            }
                        };
                        process.Start();
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit();
                        
                        output = output.Replace("+", "").Replace("*", "");
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0 && double.TryParse(lines[0], out double parsedRate) && parsedRate > 0)
                        {
                            return (int)Math.Round(parsedRate);
                        }
                    } catch { }
                    return 60;
                });
            }
        }
        catch { }
        
        _currentRefreshRate = rate;
        ApplyRefreshRate(browser, rate);
    }
    
    private void ApplyRefreshRate(AvaloniaCefBrowser browser, int rate)
    {
        if (browser.IsBrowserInitialized)
        {
            browser.GetBrowserHost()?.SetWindowlessFrameRate(rate);
        }
        else
        {
            browser.BrowserInitialized += () => browser.GetBrowserHost()?.SetWindowlessFrameRate(rate);
        }
    }

    private int _nexShareQuietStartScheduled;

    private static bool IsQuietNexShareStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;
        if (status is "Ready to pair" or "Starting service...")
            return true;
        return status.StartsWith("Started listening on port ", StringComparison.Ordinal);
    }

    private void OnFirstBrowserReadyForNexShare()
    {
        if (_isIndependentWindow)
            return;
        if (Interlocked.Exchange(ref _nexShareQuietStartScheduled, 1) != 0)
            return;
        _ = QuietStartNexShareAsync();
    }

    private async Task QuietStartNexShareAsync()
    {
        try
        {
            // Let the first page start loading before key generation, the
            // WebSocket listener, and LAN discovery.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(() => _viewModel?.NexShare.StartAfterBrowserReady());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexShare] deferred start failed: {ex.Message}");
        }
    }

    private void OnTabAdded(object? sender, TabItemViewModel tab)
    {
        var state = new TabState();
        _tabs[tab.Id] = state;
        var reveal = _browsersParked;
        if (reveal)
            ResumeBrowsers();
        if (state.Browser == null)
            CreateTabBrowser(tab, state);
        if (reveal)
            RevealMainWindow();
    }

    private void RevealMainWindow()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Release CEF browser instances when the main window is hidden to tray.
    /// The tab view models retain their current URLs and are recreated on show.
    /// </summary>
    private void ParkBrowsers()
    {
        if (_browsersParked)
            return;

        _browsersParked = true;
        _passwordCapturePollTimer.Stop();
        Program.StartupLog.Write($"[MainWindowClose] parking started tabs={_tabs.Count} pinned={_pinnedPanes.Count}");
        RunCloseStep("close super menu", CloseSuperMenu);
        RunCloseStep("close find UI", _findUi.Close);
        foreach (var pane in _pinnedPanes.ToArray())
            RunCloseStep("release pinned browser", pane.ParkForMainWindowHide);
        _historySlideToken++;
        if (ExtensionPopupContent.Content is AvaloniaCefBrowser popup)
        {
            RunCloseStep("close extension popup", () =>
            {
                popup.IsVisible = false;
                popup.Dispose();
                if (ReferenceEquals(ExtensionPopupContent.Content, popup))
                    ExtensionPopupContent.Content = null;
                ExtensionPopup.IsOpen = false;
            });
        }

        foreach (var state in _tabs.Values)
            state.ContentFullscreen = false;
        _pageViewSubmit++;
        RestoreWindowChrome();

        var browsersToClose = _tabs
            .Where(pair => pair.Value.Browser != null)
            .Select(pair => (TabId: pair.Key, State: pair.Value, Browser: pair.Value.Browser!))
            .ToArray();

        foreach (var item in browsersToClose)
        {
            var waitForCefClose = item.Browser.IsInitialized;
            RunCloseStep($"close developer tools tab={item.TabId}", item.Browser.CloseDeveloperTools);
            if (_domainLockSessions.TryGetValue(item.Browser, out var session))
            {
                session.LockAll();
                var currentUrl = item.Browser.Address ?? string.Empty;
                if (session.TryGetChallenge(currentUrl, out var challengeTarget))
                    currentUrl = challengeTarget;
                var tab = _viewModel?.Tabs.FirstOrDefault(candidate => candidate.Id == item.TabId);
                if (tab != null && !string.IsNullOrWhiteSpace(currentUrl))
                    tab.Url = currentUrl;
            }
            else
            {
                var currentUrl = item.Browser.Address ?? string.Empty;
                var tab = _viewModel?.Tabs.FirstOrDefault(candidate => candidate.Id == item.TabId);
                if (tab != null && !string.IsNullOrWhiteSpace(currentUrl))
                    tab.Url = currentUrl;
            }

            RunCloseStep($"hide browser tab={item.TabId}", () => item.Browser.IsVisible = false);
            RunCloseStep($"dispose browser tab={item.TabId}", item.Browser.Dispose);
            item.State.Browser = null;
            _domainLockSessions.Remove(item.Browser);
            if (!waitForCefClose)
                DetachBrowserControlAfterClose(item.TabId, item.Browser);
        }

        Program.StartupLog.Write("[MainWindowClose] parking finished");
    }

    private static void RunCloseStep(string step, Action action)
    {
        try
        {
            Program.StartupLog.Write($"[MainWindowClose] {step} started");
            action();
            Program.StartupLog.Write($"[MainWindowClose] {step} finished");
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[MainWindowClose] {step} failed: {ex}");
        }
    }

    private void DetachBrowserControlAfterClose(string tabId, AvaloniaCefBrowser browser)
    {
        Dispatcher.UIThread.Post(() =>
        {
            Program.StartupLog.Write($"[BrowserLifecycle] detach started tab={tabId}");
            RunCloseStep($"detach browser tab={tabId}", () =>
            {
                if (_findUi.Targets(browser))
                    _findUi.ForgetTarget();
                var grid = this.FindControl<Grid>("MainGrid");
                if (grid?.Children.Contains(browser) == true)
                    grid.Children.Remove(browser);
            });
            Program.StartupLog.Write($"[BrowserLifecycle] detach requested tab={tabId}");
        });
    }

    public void ResumeBrowsers()
    {
        if (!_browsersParked || _viewModel == null)
            return;

        _browsersParked = false;
        _passwordCapturePollTimer.Start();
        foreach (var tab in _viewModel.Tabs.ToList())
        {
            if (!_tabs.TryGetValue(tab.Id, out var state))
            {
                state = new TabState();
                _tabs[tab.Id] = state;
            }
            if (state.Browser == null)
                CreateTabBrowser(tab, state);
            else
                state.Browser.IsVisible = true;
        }

        var activeId = _viewModel.ActiveTab?.Id;
        if (!string.IsNullOrEmpty(activeId))
            ApplyTabChrome(activeId);
        foreach (var pane in _pinnedPanes.ToArray())
            pane.ResumeAfterMainWindowShow();
    }

    private void CreateTabBrowser(TabItemViewModel tab, TabState state)
    {
        if (state.Browser != null)
            return;

        StartupTimer.MarkOnce("first tab browser create");
        if (_incognito)
            Views.IncognitoSession.HoldBrowser();
        var browser = _incognito
            ? new AvaloniaCefBrowser(() => Views.IncognitoSession.BorrowForBrowser())
            : new AvaloniaCefBrowser();
        browser.Address = BlankPage.ForBrowser(tab.Url);
        WebAudioDecodeShim.Attach(browser);
        _findUi.Attach(browser);
        state.Browser = browser;
        browser.BrowserInitialized += () => StartupTimer.MarkOnce("first tab browser initialized (CEF)");
        browser.LoadStart += (_, e) =>
        {
            if (e.Frame.IsMain)
            {
                StartupTimer.MarkOnce("first page load start");
                // Early, so the site's own styles do not flash first; repeated at LoadEnd.
                ApplyCustomCss(e.Frame);
            }
        };
        browser.BrowserInitialized += () => { _ = ApproximateGeolocation.PublishAsync(() => browser.GetBrowserHost()); };
        browser.BrowserInitialized += () =>
        {
            if (state.AudioMuted)
                TabAudio.Apply(browser, true);
        };
        browser.BrowserInitialized += OnFirstBrowserReadyForNexShare;
        browser.Settings.WindowlessFrameRate = 60; // Safe default for startup
        _ = UpdateRefreshRateAsync(browser); // Async update

        // Intercept raw mouse events before CEF to implement Shift+RightClick and Double RightClick bypasses
        browser.AddHandler(InputElement.PointerPressedEvent, OnBrowserPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        browser.ContextMenuHandler = new CustomContextMenuHandler((x, y, copy) => ShowSuperMenu(x, y, copy));
        
        // -----------------------------
        
        if (_viewModel != null)
        {
            browser.DownloadHandler = new CustomDownloadHandler(_viewModel);
            browser.PermissionHandler = new Handlers.CustomPermissionHandler(_viewModel);
            browser.DialogHandler = new Handlers.CustomDialogHandler();
            browser.DisplayHandler = new Handlers.CustomDisplayHandler(progress => {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    tab.LoadingProgress = (int)(progress * 100);
                });
            }, fullscreen => {
                Dispatcher.UIThread.Post(() => SetContentFullscreen(tab.Id, fullscreen));
            }, (faviconBrowser, iconUrls) =>
            {
                var iconUrl = iconUrls.FirstOrDefault(url =>
                    Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == "data"));
                if (string.IsNullOrWhiteSpace(iconUrl))
                    return;

                var requestVersion = ++state.FaviconRequestVersion;
                try
                {
                    faviconBrowser.GetHost().DownloadImage(iconUrl, true, 64, false,
                        new FaviconImageCallback(png => Dispatcher.UIThread.Post(() =>
                        {
                            if (png == null || requestVersion != state.FaviconRequestVersion ||
                                !_tabs.TryGetValue(tab.Id, out var current) || !ReferenceEquals(current, state))
                                return;

                            tab.FaviconSource = PageCopy.DecodeBitmap(png);
                            tab.FaviconPng = png;
                        })));
                }
                catch (Exception ex)
                {
                    Program.StartupLog.Write($"[Favicon] download failed host={faviconBrowser.GetMainFrame()?.Url} error={ex.Message}");
                }
            });
        }
        
        var domainSession = new DomainLockBrowserSession();
        _domainLockSessions[browser] = domainSession;
        browser.RequestHandler = new NewTabRequestHandler(OpenLinkFromPointer, InstallStoreExtension,
            App.ServiceProvider?.GetService<DomainLockStore>(), domainSession);
        browser.JavascriptContextCreated += (_, e) =>
        {
            if (e.Frame.IsMain)
            {
                var frameUrl = e.Frame.Url ?? string.Empty;
                var host = Uri.TryCreate(frameUrl, UriKind.Absolute, out var frameUri) ? frameUri.Host : "unknown";
                Program.StartupLog.Write($"[PasswordCapture] main JS context ready host={host}");
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
                var diagnosticHost = Uri.TryCreate(browser.Address, UriKind.Absolute, out var diagnosticPage) ? diagnosticPage.Host : "unknown";
                Program.StartupLog.Write($"[PasswordCapture] page host={diagnosticHost} {message.Message[18..]}");
                return;
            }
            var pageUrl = browser.Address ?? "";
            if (pageUrl.Contains("newtab", StringComparison.OrdinalIgnoreCase)
                || pageUrl.Contains("nexzen://", StringComparison.OrdinalIgnoreCase)
                || message.Message.StartsWith("[NewTab]", StringComparison.Ordinal))
            {
                Program.StartupLog.Write($"[NewTab] console {message.Level} line={message.Line}: {message.Message}");
                return;
            }
            if (message.Level < CefLogSeverity.Error)
                return;
            if (!message.Message.Contains("nexzenPasswordBridge", StringComparison.OrdinalIgnoreCase)
                && !message.Message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase)
                && !message.Message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase))
                return;
            var detail = message.Message.Length > 240 ? message.Message[..240] : message.Message;
            var errorHost = Uri.TryCreate(browser.Address, UriKind.Absolute, out var page) ? page.Host : "unknown";
            Program.StartupLog.Write($"[PasswordCapture] JS error host={errorHost} line={message.Line}: {detail}");
        };
        browser.LifeSpanHandler = new CustomLifeSpanHandler(async (sourceUrl, targetUrl) => 
        {
            if (_viewModel == null) return;

            bool isSameDomain = false;
            try
            {
                if (!string.IsNullOrEmpty(sourceUrl))
                {
                    var sourceUri = new Uri(sourceUrl);
                    var targetUri = new Uri(targetUrl);
                    isSameDomain = sourceUri.Host.Equals(targetUri.Host, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Ignore parse errors, assume different domain to be safe
            }

            if (!isSameDomain)
            {
                var targetDomain = "Unknown";
                try { targetDomain = new Uri(targetUrl).Host; } catch {}
                
                string result = await _viewModel.ConfirmPrompt.ShowAsync(
                    "Open a new page?",
                    $"This page wants to open a new tab to: {targetDomain}\n\nAllow it?",
                    "Block",
                    null,
                    "Allow"
                );

                if (result == "Positive")
                {
                    OpenLinkFromPointer(targetUrl);
                }
            }
            else
            {
                OpenLinkFromPointer(targetUrl);
            }
        }, () =>
        {
            Program.StartupLog.Write($"[MainWindowClose] CEF before-close tab={tab.Id}");
            if (_incognito)
                Views.IncognitoSession.BrowserClosed();
            Dispatcher.UIThread.Post(() => DetachBrowserControlAfterClose(tab.Id, browser));
        }, (sourceUrl, _) =>
        {
            if (_viewModel?.ActiveTab?.Id == tab.Id)
                ShowNotification("Pop-up blocked",
                    $"{SiteDomains.SiteOf(sourceUrl) ?? "This page"} tried to open a pop-up. Allow it in Tools > Site privacy.",
                    NotificationType.Warning);
        });
        
        browser.BrowserInitialized += () =>
        {
            // CEF's global request context exists now: apply saved Site privacy choices.
            Services.SiteContentSettings.BrowserReady();
            Dispatcher.UIThread.Post(() =>
            {
                if (state.Viewport != null && GetActiveTabState() == state)
                    LayoutPageView(state, pushMetrics: true, reload: false);
            });
        };

        browser.LoadStart += (s, e) =>
        {
            ShowProgressBar(true);
            if (!e.Frame.IsMain)
                return;
            var url = e.Frame.Url ?? "";
            if (url.Contains("chromewebstore.google.com", StringComparison.OrdinalIgnoreCase))
                e.Frame.ExecuteJavaScript(WebStoreInstaller.StoreScript, url, 1);
            Dispatcher.UIThread.Post(() => ApplySiteSound(state, browser, url));
        };
        browser.LoadEnd += (s, e) =>
        {
            if (e.Frame.IsMain)
            {
                StartupTimer.MarkOnce("first page load end");
                ApplyCustomCss(e.Frame);
            }
            var url = e.Frame.Url ?? "";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!_incognito)
                    e.Frame.ExecuteJavaScript(PasswordCaptureBridge.ObserverScript, url, 1);
                if (e.Frame.IsMain)
                {
                    Program.StartupLog.Write($"[PasswordCapture] observer injection requested main={e.Frame.IsMain} host={new Uri(url).Host}");
                    _ = LogPasswordObserverStateAsync(browser, url);
                }
            }
            if (!e.Frame.IsMain) return;
            // Pinterest fix: patch drag detection so CEF sub-pixel rounding doesn't
            // cause clicks to be misidentified as drags and swallowed.
            if (url.Contains("pinterest.com", StringComparison.OrdinalIgnoreCase))
            {
                const string pinterestFix = @"
(function() {
    // Pinterest uses a drag threshold to distinguish pin-click vs pin-drag.
    // CEF offscreen coordinate rounding can produce a 1-2px delta between
    // mousedown and mouseup, tripping the threshold. We patch MouseEvent to
    // make Pinterest see the same position on both events.
    var _downX = 0, _downY = 0;
    document.addEventListener('mousedown', function(e) {
        _downX = e.clientX; _downY = e.clientY;
    }, true);
    document.addEventListener('mouseup', function(e) {
        // If the delta is tiny (< 4px), snap to the original mousedown coords
        // so Pinterest's drag check always sees 0 movement on a normal click.
        var dx = Math.abs(e.clientX - _downX);
        var dy = Math.abs(e.clientY - _downY);
        if (dx < 4 && dy < 4) {
            Object.defineProperty(e, 'clientX', { get: function() { return _downX; } });
            Object.defineProperty(e, 'clientY', { get: function() { return _downY; } });
        }
    }, true);
})();
";
                e.Frame.ExecuteJavaScript(pinterestFix, url, 1);
            }
        };
        browser.LoadEnd += async (s, e) => {
            ShowProgressBar(false);
            if (e.Frame.IsMain)
            {
                var title = browser.Title;
                var address = browser.Address;
                if (!domainSession.TryGetChallenge(address, out _))
                    Dispatcher.UIThread.Post(() => _ = _viewModel?.RecordVisitAsync(title, address));
            }
            
            // Capture thumbnail
            if (_viewModel?.ActiveTab?.Id == tab.Id)
            {
                var bitmap = await NexZen.Desktop.Helpers.ScreenshotHelper.CaptureAsync(browser);
                if (bitmap != null) {
                    Dispatcher.UIThread.Post(() => tab.ThumbnailSource = bitmap);
                }
            }
        };
        browser.TitleChanged += (s, e) => {
            Dispatcher.UIThread.Post(() =>
            {
                tab.Title = browser.Title;
                UpdateWindowTitle();
                if (!domainSession.TryGetChallenge(browser.Address, out _))
                    _ = _viewModel?.RecordVisitAsync(browser.Title, browser.Address);
            });
        };
        browser.AddressChanged += (s, e) => {
            Dispatcher.UIThread.Post(() => {
                if (e != null && e is string address)
                {
                    state.FaviconRequestVersion++;
                    tab.FaviconSource = null;
                    tab.FaviconPng = null;
                    var isLockPage = domainSession.TryGetChallenge(address, out var pageTarget);
                    var displayAddress = BlankPage.ForDisplay(isLockPage ? pageTarget : address);
                    if (_viewModel?.ActiveTab?.Id == tab.Id)
                        _viewModel.CurrentUrl = displayAddress;
                    
                    tab.Url = displayAddress;

                    if (isLockPage && state.HistoryIndex >= 0 && state.History[state.HistoryIndex] == displayAddress)
                        return;

                    if (state.IsNavigatingFromHistory)
                    {
                        state.IsNavigatingFromHistory = false;
                    }
                    else if (state.HistoryIndex >= 0 && state.History[state.HistoryIndex] == displayAddress)
                    {
                    }
                    else if (state.HistoryIndex > 0 && state.History[state.HistoryIndex - 1] == displayAddress)
                    {
                        state.HistoryIndex--;
                    }
                    else
                    {
                        if (state.HistoryIndex < state.History.Count - 1)
                            state.History.RemoveRange(state.HistoryIndex + 1, state.History.Count - (state.HistoryIndex + 1));
                        state.History.Add(displayAddress);
                        state.HistoryIndex++;
                    }
                }
            });
        };

        var grid = this.FindControl<Grid>("MainGrid");
        if (grid != null)
        {
            // Insert at index 0 so it's behind the UI overlay
            grid.Children.Insert(0, browser);
        }
        StartupTimer.MarkOnce("first tab browser attached");
    }

    private void OnTabSwitcherKeyDown(object? sender, KeyEventArgs e)
    {
        if (_tabSwitcherWindow != null)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseTabSwitcher(commit: false);
                return;
            }
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitTabSwitcher();
                return;
            }
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                AdvanceTabSwitcher(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                return;
            }
            if (e.Key is Key.Left or Key.Right)
            {
                e.Handled = true;
                AdvanceTabSwitcher(e.Key == Key.Left ? -1 : 1);
                return;
            }
        }

        if (_viewModel == null || _viewModel.Tabs.Count < 2)
            return;

        if (e.Key != Key.Tab
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Alt)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;

        e.Handled = true;
        CloseSuperMenu();
        var activeIndex = _viewModel.Tabs.IndexOf(_viewModel.ActiveTab!);
        _tabSwitcherIndex = activeIndex >= 0 ? activeIndex : 0;
        AdvanceTabSwitcher(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
    }

    private void OnTabSwitcherKeyUp(object? sender, KeyEventArgs e)
    {
        if (_tabSwitcherWindow == null || e.Key is not (Key.LeftCtrl or Key.RightCtrl))
            return;
        e.Handled = true;
        CommitTabSwitcher();
    }

    private void AdvanceTabSwitcher(int direction)
    {
        if (_viewModel == null || _viewModel.Tabs.Count < 2)
        {
            CloseTabSwitcher(commit: false);
            return;
        }

        _tabSwitcherIndex = (_tabSwitcherIndex + direction + _viewModel.Tabs.Count) % _viewModel.Tabs.Count;
        EnsureTabSwitcherWindow();
        PopulateTabSwitcher();
        _tabSwitcherCommitTimer.Stop();
        _tabSwitcherCommitTimer.Interval = TimeSpan.FromSeconds(5);
        _tabSwitcherCommitTimer.Start();
    }

    private void EnsureTabSwitcherWindow()
    {
        if (_tabSwitcherWindow != null)
            return;

        var root = new Grid();
        var backdrop = new Border { Background = Brushes.Transparent };
        backdrop.PointerPressed += (_, _) => CommitTabSwitcher();
        root.Children.Add(backdrop);

        _tabSwitcherPanel = new Border
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            // Same frame as the app's other floating windows (light grey, rounded, thin border).
            Padding = new Thickness(22, 18, 22, 22),
            Background = new SolidColorBrush(Color.Parse("#F3F3F3")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8D8D8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 18,
                OffsetY = 4,
                Color = Color.FromArgb(48, 0, 0, 0)
            }),
            Opacity = 0
        };
        _tabSwitcherEntranceTransform = new TranslateTransform(0, 24);
        _tabSwitcherPanel.RenderTransform = _tabSwitcherEntranceTransform;
        root.Children.Add(_tabSwitcherPanel);

        _tabSwitcherWindow = new Window
        {
            Content = root,
            Width = Bounds.Width,
            Height = Bounds.Height,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            ShowActivated = true
        };
        _tabSwitcherWindow.AddHandler(KeyDownEvent, OnTabSwitcherKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _tabSwitcherWindow.AddHandler(KeyUpEvent, OnTabSwitcherKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _tabSwitcherWindow.Position = this.PointToScreen(new Point(0, 0));
        _tabSwitcherWindow.Show(this);
        _tabSwitcherWindow.Activate();

        _tabSwitcherPanel.Opacity = 0;
        _tabSwitcherAnimationStarted = DateTime.UtcNow;
        _tabSwitcherAnimationTimer.Start();
    }

    private void PopulateTabSwitcher()
    {
        if (_tabSwitcherPanel == null || _viewModel == null || _viewModel.Tabs.Count == 0)
            return;

        // The frame fits the cards; it is only as wide as the window allows.
        _tabSwitcherPanel.MaxWidth = Math.Max(200, Bounds.Width - 32);
        var count = _viewModel.Tabs.Count;
        var visibleCount = Math.Min(5, count);
        var nominalRowWidth = 340 + Math.Max(0, visibleCount - 1) * 240 + Math.Max(0, visibleCount - 1) * 12;
        var scale = Math.Clamp((Bounds.Width - 48) / nominalRowWidth, 0.55, 1.0);
        var cards = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 14 * scale,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            // Cards stand on one line, so the bigger selected card rises above the others.
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom
        };
        var startOffset = visibleCount switch
        {
            2 => 0,
            4 => -1,
            _ => -(visibleCount / 2)
        };

        for (var item = 0; item < visibleCount; item++)
        {
            var offset = startOffset + item;
            var tabIndex = (_tabSwitcherIndex + offset + count) % count;
            var tab = _viewModel.Tabs[tabIndex];
            var distance = Math.Abs(offset);
            var width = (distance == 0 ? 340 : 240) * scale;
            var height = (distance == 0 ? 228 : 170) * scale;

            var preview = new Grid();
            if (tab.ThumbnailSource is IImage imageSource)
            {
                preview.Children.Add(new Image { Source = imageSource, Stretch = Stretch.UniformToFill });
            }
            else
            {
                preview.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#E9E9E9")) });
                preview.Children.Add(new TextBlock
                {
                    Text = tab.Title,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.Parse("#555555")),
                    FontSize = 16,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var caption = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.Parse("#E4E4E4")),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 7),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = tab.Title,
                    FontSize = distance == 0 ? 13 : 11,
                    FontWeight = distance == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    Foreground = new SolidColorBrush(Color.Parse("#2A2A2A")),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1
                }
            };
            preview.Children.Add(caption);

            var card = new Border
            {
                Width = width,
                Height = height,
                Margin = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(10),
                Background = Brushes.White,
                // Selected: the app's accent (the yellow of its main buttons); others: thin grey.
                BorderBrush = distance == 0
                    ? new SolidColorBrush(Color.Parse("#E2CC6B"))
                    : new SolidColorBrush(Color.Parse("#CFCFCF")),
                BorderThickness = new Thickness(distance == 0 ? 2.5 : 1),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    Blur = distance == 0 ? 14 : 6,
                    OffsetY = distance == 0 ? 4 : 2,
                    Color = Color.FromArgb(distance == 0 ? (byte)60 : (byte)28, 0, 0, 0)
                }),
                ClipToBounds = true,
                Child = preview,
                ZIndex = distance == 0 ? 10 : 5
            };
            card.PointerPressed += (_, e) =>
            {
                _tabSwitcherIndex = tabIndex;
                e.Handled = true;
                CommitTabSwitcher();
            };
            cards.Children.Add(card);
        }

        _tabSwitcherPanel.Child = cards;
        var top = Math.Max(12, Bounds.Height * 0.82 - 125 * scale);
        _tabSwitcherPanel.Margin = new Thickness(0, top, 0, 0);
    }

    private void AnimateTabSwitcherEntrance()
    {
        if (_tabSwitcherPanel == null || _tabSwitcherEntranceTransform == null)
        {
            _tabSwitcherAnimationTimer.Stop();
            return;
        }
        var progress = Math.Clamp((DateTime.UtcNow - _tabSwitcherAnimationStarted).TotalMilliseconds / 190.0, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        _tabSwitcherPanel.Opacity = eased;
        _tabSwitcherEntranceTransform.Y = 24 * (1 - eased);
        if (progress >= 1)
            _tabSwitcherAnimationTimer.Stop();
    }

    private void ResizeTabSwitcherOverlay()
    {
        if (_tabSwitcherWindow == null)
            return;
        _tabSwitcherWindow.Width = Bounds.Width;
        _tabSwitcherWindow.Height = Bounds.Height;
        _tabSwitcherWindow.Position = this.PointToScreen(new Point(0, 0));
        PopulateTabSwitcher();
    }

    private void CommitTabSwitcher() => CloseTabSwitcher(commit: true);

    private void CloseTabSwitcher(bool commit)
    {
        if (_tabSwitcherWindow == null)
            return;
        _tabSwitcherCommitTimer.Stop();
        _tabSwitcherAnimationTimer.Stop();
        var selectedId = commit && _viewModel != null && _tabSwitcherIndex >= 0 && _tabSwitcherIndex < _viewModel.Tabs.Count
            ? _viewModel.Tabs[_tabSwitcherIndex].Id
            : null;

        var window = _tabSwitcherWindow;
        _tabSwitcherWindow = null;
        _tabSwitcherPanel = null;
        _tabSwitcherEntranceTransform = null;
        _tabSwitcherIndex = -1;
        window.Content = null;
        window.Close();

        if (selectedId != null && _viewModel?.Tabs.Any(tab => tab.Id == selectedId) == true)
        {
            _viewModel.SwitchTab(selectedId);
            Dispatcher.UIThread.Post(() => GetActiveTabState()?.Browser?.Focus());
        }
    }

    private DateTime _lastRightClickTime = DateTime.MinValue;

    private void OnBrowserPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Avalonia.Visual);
        if (point.Properties.IsRightButtonPressed)
        {
            bool forceMenu = false;
            
            // 1. Shift + Right Click
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                forceMenu = true;
            }
            else
            {
                // 2. Double Right Click
                var now = DateTime.Now;
                if ((now - _lastRightClickTime).TotalMilliseconds < 500)
                {
                    forceMenu = true;
                    _lastRightClickTime = DateTime.MinValue; // reset
                }
                else
                {
                    _lastRightClickTime = now;
                }
            }

            if (forceMenu && sender is AvaloniaCefBrowser activeBrowser)
            {
                e.Handled = true; // Prevent CEF from getting this
                var position = point.Position;
                var context = new PageCopyContext();
                ShowSuperMenu((int)position.X, (int)position.Y, context);
            }
        }
    }

    private void OnTabClosed(object? sender, string tabId)
    {
        if (_tabs.TryGetValue(tabId, out var state))
        {
            if (state.Browser != null)
            {
                var browser = state.Browser;
                if (browser.IsVisible && _viewModel?.ActiveTab?.Id == tabId)
                    BeginSlideOut(browser);
                var waitForCefClose = browser.IsInitialized;
                Program.StartupLog.Write($"[BrowserLifecycle] tab close started tab={tabId} initialized={waitForCefClose}");
                Program.StartupLog.Write($"[BrowserLifecycle] close developer tools started tab={tabId}");
                RunCloseStep($"close developer tools tab={tabId}", browser.CloseDeveloperTools);
                if (_findUi.Targets(browser))
                    _findUi.ForgetTarget();
                Program.StartupLog.Write($"[BrowserLifecycle] hide started tab={tabId}");
                RunCloseStep($"hide browser tab={tabId}", () => browser.IsVisible = false);
                Program.StartupLog.Write($"[BrowserLifecycle] dispose started tab={tabId}");
                RunCloseStep($"dispose browser tab={tabId}", browser.Dispose);
                state.Browser = null;
                _domainLockSessions.Remove(browser);
                if (!waitForCefClose)
                    DetachBrowserControlAfterClose(tabId, browser);
                Program.StartupLog.Write($"[BrowserLifecycle] tab close requested tab={tabId}");
            }
            _tabs.Remove(tabId);
        }

        SyncContentFullscreenWindow();
    }

    private void OnTabSwitched(object? sender, string tabId)
    {
        if (_slideInNextTab)
        {
            _slideInNextTab = false;
            BeginSlideIn(tabId);
        }
        else
        {
            ApplyTabChrome(tabId);
        }

        _findUi.RetargetOpenMain(GetActiveTabState()?.Browser);
    }

    private void NavigateHistory(bool back)
    {
        var browser = GetActiveTabState()?.Browser;
        if (browser == null)
        {
            CloseSuperMenu();
            return;
        }

        bool canMove = back ? browser.CanGoBack : browser.CanGoForward;
        CloseSuperMenu();
        if (!canMove)
            return;

        browser.UpdateLayout();
        var shot = CaptureBrowser(browser);
        if (back)
            browser.GoBack();
        else
            browser.GoForward();

        StartHistorySlide(browser, back, shot);
    }

    private Bitmap? CaptureBrowser(AvaloniaCefBrowser browser)
    {
        try
        {
            double w = browser.Bounds.Width;
            double h = browser.Bounds.Height;
            if (w < 2 || h < 2)
                return null;

            double scale = Math.Max(1, RenderScaling);
            var bitmap = new RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale))),
                new Vector(96 * scale, 96 * scale));
            bitmap.Render(browser);
            return bitmap;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tab] History capture failed: {ex.Message}");
            return null;
        }
    }

    private void StartHistorySlide(AvaloniaCefBrowser browser, bool back, Bitmap? shot)
    {
        int token = ++_historySlideToken;
        double width = Math.Max(Bounds.Width, 1);
        double height = Math.Max(Bounds.Height, 1);
        double incomingStart = back ? -width : width;
        double outgoingEnd = back ? width : -width;

        var incoming = new TranslateTransform(incomingStart, 0);
        var outgoing = new TranslateTransform(0, 0);
        browser.RenderTransform = incoming;
        browser.ZIndex = 3;

        var grid = this.FindControl<Grid>("MainGrid");
        if (grid == null)
        {
            browser.RenderTransform = null;
            shot?.Dispose();
            return;
        }

        var sheet = new Border
        {
            Background = Brushes.White,
            Width = width,
            Height = height,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            ZIndex = 2,
            RenderTransform = incoming
        };
        grid.Children.Add(sheet);

        Image? overlay = null;
        if (shot != null)
        {
            overlay = new Image
            {
                Source = shot,
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                IsHitTestVisible = false,
                ZIndex = 4,
                RenderTransform = outgoing
            };
            grid.Children.Add(overlay);
        }

        var duration = TimeSpan.FromMilliseconds(460);
        TimeSpan? start = null;

        void OnFrame(TimeSpan time)
        {
            if (token != _historySlideToken)
            {
                RemoveHistorySlide(grid, sheet, overlay, shot, browser, incoming, restoreBrowser: false);
                return;
            }

            start ??= time;
            double t = (time - start.Value).TotalMilliseconds / duration.TotalMilliseconds;
            if (t >= 1)
            {
                incoming.X = 0;
                outgoing.X = outgoingEnd;
                RemoveHistorySlide(grid, sheet, overlay, shot, browser, incoming, restoreBrowser: true);
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);
            incoming.X = incomingStart * (1 - eased);
            outgoing.X = outgoingEnd * eased;
            RequestAnimationFrame(OnFrame);
        }

        RequestAnimationFrame(OnFrame);
    }

    private static void RemoveHistorySlide(
        Grid grid,
        Border sheet,
        Image? overlay,
        Bitmap? shot,
        AvaloniaCefBrowser browser,
        TranslateTransform incoming,
        bool restoreBrowser)
    {
        if (sheet.Parent == grid)
            grid.Children.Remove(sheet);
        if (overlay != null && overlay.Parent == grid)
            grid.Children.Remove(overlay);
        shot?.Dispose();

        if (restoreBrowser && browser.RenderTransform == incoming)
        {
            browser.RenderTransform = null;
            browser.ZIndex = 1;
        }
    }

    private static bool IsUsableQrUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return false;
        return !string.Equals(url.Trim(), "about:blank", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CreateQrFromMenuAsync()
    {
        string? url = _copyContext?.LinkUrl;
        if (!IsUsableQrUrl(url))
            url = _viewModel?.ActiveTab?.Url;

        CloseSuperMenu();

        if (!IsUsableQrUrl(url) || _viewModel == null)
        {
            ShowCopyNotification("Create QR", "No link to encode.", null, NotificationType.Warning);
            return;
        }

        Bitmap? qr = null;
        try
        {
            qr = QrCodeImage.Render(url!);
        }
        catch (Exception ex)
        {
            ShowCopyNotification("Create QR", "Couldn't create the QR code.", null, NotificationType.Warning);
            Console.WriteLine($"[Create QR] {ex}");
            return;
        }

        _viewModel.ConfirmPrompt.QrImage = qr;
        _viewModel.ConfirmPrompt.IsQrVisible = true;
        try
        {
            await _viewModel.ConfirmPrompt.ShowAsync(
                "QR code",
                url!,
                null,
                null,
                "Close");
        }
        finally
        {
            _viewModel.ConfirmPrompt.QrImage = null;
            _viewModel.ConfirmPrompt.IsQrVisible = false;
            qr.Dispose();
        }
    }

    private void OpenLinkInNewTab(string? url)
    {
        CloseSuperMenu();
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            ShowCopyNotification("New tab", "No link to open.", null, NotificationType.Warning);
            return;
        }

        if (_viewModel == null)
            return;

        _slideInNextTab = true;
        _viewModel.AddTab(url);
    }

    private void OpenTabInNewWindow(string tabId)
    {
        var tab = _viewModel?.Tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab == null || _viewModel == null)
            return;

        var targetHost = Uri.TryCreate(tab.Url, UriKind.Absolute, out var targetUri) ? targetUri.Host : "local page";
        Program.StartupLog.Write($"[NewWindow] tab transfer requested tab={tabId} host={targetHost}");
        // Open first so the original tab remains available if the URL cannot be opened.
        bool opened;
        try
        {
            opened = OpenWindowLikeThis(tab.Url);
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[NewWindow] window creation failed type={ex.GetType().Name} message={ex.Message}");
            ShowCopyNotification("Open new window", "Couldn't create a new browser window.", null, NotificationType.Error);
            return;
        }

        if (!opened)
        {
            ShowCopyNotification("Open new window", "This tab can't be opened in a new window.", null, NotificationType.Warning);
            return;
        }
        Program.StartupLog.Write($"[NewWindow] independent window opened tab={tabId}");

        // MainViewModel intentionally keeps at least one tab in the original window.
        if (_viewModel.Tabs.Count == 1)
        {
            Program.StartupLog.Write("[NewWindow] adding replacement blank tab");
            _viewModel.AddTab("about:blank");
        }

        Program.StartupLog.Write($"[NewWindow] closing source tab={tabId}");
        _viewModel.CloseTab(tabId);
        CloseSuperMenu();
        Program.StartupLog.Write($"[NewWindow] transfer completed tab={tabId}");
    }

    // "Open in New tab" only applies when the menu was opened on a link or an image.
    private static string? NewTabTarget(PageCopyContext? copy)
    {
        if (copy == null)
            return null;
        if (Views.PopupBrowserWindow.CanOpen(copy.LinkUrl))
            return copy.LinkUrl;
        return copy.IsImage && Views.PopupBrowserWindow.CanOpen(copy.ImageUrl) ? copy.ImageUrl : null;
    }

    private static bool CanOpenInNewWindow(PageCopyContext copy)
    {
        if (copy.IsImage && Views.PopupBrowserWindow.CanOpen(copy.ImageUrl))
            return true;
        return Views.PopupBrowserWindow.CanOpen(copy.LinkUrl);
    }

    private void OpenInNewWindowFromMenu()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null)
            return;

        if (copy.IsImage && OpenWindowLikeThis(copy.ImageUrl))
            return;
        if (OpenWindowLikeThis(copy.LinkUrl))
            return;

        ShowCopyNotification(
            "Open in New window",
            copy.IsImage ? "This image can't be opened in a new window." : "No link to open.",
            null,
            NotificationType.Warning);
    }

    private void OpenOnPinnedWindowFromMenu()
    {
        var copy = _copyContext;
        CloseSuperMenu();

        string? raw = null;
        if (copy?.IsImage == true && Views.PopupBrowserWindow.CanOpen(copy.ImageUrl))
            raw = copy.ImageUrl;
        else if (copy != null && Views.PopupBrowserWindow.CanOpen(copy.LinkUrl))
            raw = copy.LinkUrl;
        else
            raw = GetActiveTabState()?.Browser?.Address ?? _viewModel?.ActiveTab?.Url;

        if (!Views.PopupBrowserWindow.TryNormalize(raw, out var url))
        {
            ShowCopyNotification(
                "Open on Pinned window",
                "The current page can't be opened in a pinned window.",
                null,
                NotificationType.Warning);
            return;
        }

        if (_pinnedPanes.Count >= MaxPinnedWindows)
        {
            ShowCopyNotification(
                "Open on Pinned window",
                $"You can have up to {MaxPinnedWindows} pinned windows open at once.",
                null,
                NotificationType.Warning);
            return;
        }

        var pane = CreatePinnedPane();
        pane.ShowUrl(url);
    }

    public void OpenPinnedPageFromPopup(string? rawUrl)
    {
        if (!Views.PopupBrowserWindow.TryNormalize(rawUrl, out var url))
        {
            ShowCopyNotification("Open on Pinned window", "This page can't be opened in a pinned window.", null, NotificationType.Warning);
            return;
        }

        if (_pinnedPanes.Count >= MaxPinnedWindows)
        {
            ShowCopyNotification("Open on Pinned window", $"You can have up to {MaxPinnedWindows} pinned windows open at once.", null, NotificationType.Warning);
            return;
        }

        CreatePinnedPane().ShowUrl(url);
    }

    private void OpenLinkFromPointer(string url)
    {
        if (_viewModel == null || string.IsNullOrWhiteSpace(url))
            return;

        _slideInNextTab = true;
        _viewModel.AddTab(url);
    }

    private async Task AskToSavePasswordAsync(PasswordCapture capture)
    {
        if (_viewModel == null || !Uri.TryCreate(capture.Origin, UriKind.Absolute, out var origin))
            return;

        try
        {
            var username = capture.Username?.Trim() ?? string.Empty;
            var match = await _viewModel.MatchSavedPasswordAsync(capture.Origin, username, capture.Password);
            if (match == SavedPasswordMatch.Identical)
            {
                Program.StartupLog.Write($"[PasswordCapture] skip prompt already saved host={origin.Host}");
                return;
            }

            var title = match == SavedPasswordMatch.Different ? "Update password?" : "Save password?";
            var body = match == SavedPasswordMatch.Different
                ? $"{origin.Host}\nUsername: {username}\nA saved password already exists. Update it?"
                : $"{origin.Host}\nUsername: {username}";
            Program.StartupLog.Write($"[PasswordCapture] showing ConfirmPrompt host={origin.Host} match={match}");
            var result = await _viewModel.ConfirmPrompt.ShowAsync(
                title,
                body,
                "Don't save",
                "Not now",
                match == SavedPasswordMatch.Different ? "Update" : "Save");
            Program.StartupLog.Write($"[PasswordCapture] ConfirmPrompt result={result} host={origin.Host}");
            if (result == "Positive")
            {
                var password = capture.Password;
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("The captured username or password was empty.");

                var vaultAuth = App.ServiceProvider?.GetRequiredService<PasswordVaultAuthenticationService>();
                if (vaultAuth == null || !await vaultAuth.EnsureReadyForSaveAsync(this))
                {
                    ShowCopyNotification("Password not saved", "Create a NexZen PIN to save passwords. This login was not stored.", null, NotificationType.Warning);
                    Program.StartupLog.Write($"[PasswordCapture] save canceled because vault PIN setup was skipped host={origin.Host}");
                    return;
                }

                Program.StartupLog.Write($"[PasswordCapture] save requested host={origin.Host} usernameLength={username.Length} passwordLength={password.Length}");
                await _viewModel.SavePasswordAsync(capture.Origin, username, password,
                    stage => Program.StartupLog.Write($"[PasswordCapture] save stage host={origin.Host} stage={stage}"));
                Program.StartupLog.Write($"[PasswordCapture] save completed host={origin.Host} visibleEntries={_viewModel.SavedPasswords.Count}");
                ShowCopyNotification("Password saved", $"Saved login for {origin.Host}.", null, NotificationType.Information);
            }
        }
        catch (Exception ex)
        {
            ShowCopyNotification("Password not saved", "Couldn't save this login. See startup.log for details.", null, NotificationType.Error);
            Program.StartupLog.Write($"[PasswordCapture] save failed type={ex.GetType().Name} message={ex.Message}");
            Console.WriteLine($"[PasswordVault] {ex.GetType().Name}");
        }
    }

    public async Task CheckPasswordVaultSetupReminderAsync()
    {
        if (!IsVisible || Interlocked.CompareExchange(ref _vaultReminderStarted, 1, 0) != 0)
            return;
        try
        {
            var auth = App.ServiceProvider?.GetRequiredService<PasswordVaultAuthenticationService>();
            if (auth != null)
                await auth.ShowSetupReminderAsync(this);
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordVaultPIN] setup reminder failed: {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _vaultReminderStarted, 0);
        }
    }

    private static async Task LogPasswordObserverStateAsync(AvaloniaCefBrowser browser, string url)
    {
        await Task.Delay(300);
        try
        {
            var state = await browser.EvaluateJavaScript<string>(
                "return JSON.stringify({readyState:document.readyState,observer:window.__nexZenPasswordObserverVersion||null,queue:Array.isArray(window.__nexZenPasswordCaptureQueue)});",
                url: url);
            Program.StartupLog.Write($"[PasswordCapture] page state host={new Uri(url).Host} state={state ?? "<null>"}");
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordCapture] page state evaluation failed type={ex.GetType().Name}");
        }
    }

    private async Task PollPasswordCaptureQueuesAsync()
    {
        if (_browsersParked || _passwordCapturePollRunning)
            return;

        _passwordCapturePollRunning = true;
        try
        {
            var browsers = new HashSet<AvaloniaCefBrowser>();
            if (GetActiveTabState()?.Browser is { } activeBrowser)
                browsers.Add(activeBrowser);
            foreach (var pane in _pinnedPanes)
                if (pane.Browser is { } pinnedBrowser)
                    browsers.Add(pinnedBrowser);

            await Task.WhenAll(browsers.Select(PollPasswordCaptureQueueAsync));
        }
        finally
        {
            _passwordCapturePollRunning = false;
        }
    }

    private async Task PollPasswordCaptureQueueAsync(AvaloniaCefBrowser browser)
    {
        try
        {
            var currentUrl = browser.Address;
            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
                || current.Scheme is not ("http" or "https"))
                return;

            var rawResult = await browser.EvaluateJavaScript<string>(PasswordCaptureBridge.DrainQueueScript, url: currentUrl);
            if (string.IsNullOrWhiteSpace(rawResult))
                return;

            var result = JsonSerializer.Deserialize<PasswordCapturePollResult>(rawResult, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (result == null)
                return;

            if (result.Diagnostic is { } diagnostic)
                Program.StartupLog.Write($"[PasswordCapture] observer diagnostic host={diagnostic.Host} stage={diagnostic.Stage} forms={diagnostic.FormCount} passwordFields={diagnostic.PasswordFields} passwordHasValue={diagnostic.PasswordHasValue} documentPasswordFields={diagnostic.DocumentPasswordFields} documentPasswordHasValue={diagnostic.DocumentPasswordHasValue} usernameFields={diagnostic.UsernameFields} inputTypes={string.Join(',', diagnostic.InputTypes ?? Array.Empty<string>())}");

            foreach (var capture in result.Captures ?? Array.Empty<PasswordCapture>())
            {
                if (!PasswordCaptureBridge.IsSameOrigin(browser.Address, capture.Origin))
                    continue;
                Program.StartupLog.Write($"[PasswordCapture] queued credentials received host={current.Host}");
                await AskToSavePasswordAsync(capture);
            }

            foreach (var request in result.AutofillRequests ?? Array.Empty<PasswordAutofillRequest>())
            {
                if (PasswordCaptureBridge.IsSameOrigin(browser.Address, request.Origin))
                    await OfferPasswordAutofillAsync(browser, request.Origin);
            }
        }
        catch
        {
            // The page can navigate or close while the queue is being polled.
        }
    }

    private async Task OfferPasswordAutofillAsync(AvaloniaCefBrowser browser, string requestedOrigin)
    {
        if (_viewModel == null || _passwordAutofillPickerOpen || !IsVisible
            || !PasswordCaptureBridge.IsSameOrigin(browser.Address, requestedOrigin)
            || !Uri.TryCreate(requestedOrigin, UriKind.Absolute, out var requestedUri))
            return;

        _passwordAutofillPickerOpen = true;
        try
        {
            var accounts = await _viewModel.GetSavedPasswordAccountsAsync(requestedOrigin);
            if (accounts.Count == 0 || !PasswordCaptureBridge.IsSameOrigin(browser.Address, requestedOrigin))
                return;

            var accountId = await Views.PasswordAutofillPickerWindow.ShowAsync(this, requestedUri.Host, accounts);
            if (accountId <= 0 || !PasswordCaptureBridge.IsSameOrigin(browser.Address, requestedOrigin))
                return;

            var auth = App.ServiceProvider?.GetRequiredService<PasswordVaultAuthenticationService>();
            if (auth == null || !await auth.AuthorizeRevealAsync(() => this))
                return;

            var credentials = await _viewModel.GetSavedCredentialsAsync(accountId, requestedOrigin);
            if (credentials == null || !PasswordCaptureBridge.IsSameOrigin(browser.Address, credentials.Origin))
                return;

            var fillScript = PasswordCaptureBridge.CreateFillScript(credentials.Username, credentials.Password);
            var filled = await browser.EvaluateJavaScript<bool>(fillScript, url: browser.Address);
            if (filled)
                Program.StartupLog.Write($"[PasswordAutofill] credentials filled host={requestedUri.Host}");
            else
                Program.StartupLog.Write($"[PasswordAutofill] no matching visible form host={requestedUri.Host}");
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordAutofill] failed type={ex.GetType().Name}");
        }
        finally
        {
            _passwordAutofillPickerOpen = false;
        }
    }

    // Mirror of BeginSlideIn: a snapshot of the closing tab slides out to the right
    // (460 ms, ease-out cubic) while the next tab is already shown underneath.
    private void BeginSlideOut(AvaloniaCefBrowser browser)
    {
        int token = ++_slideOutToken;
        RemoveSlideOutOverlay();

        var grid = this.FindControl<Grid>("MainGrid");
        if (grid == null || !IsVisible)
            return;

        Bitmap? shot;
        try
        {
            browser.UpdateLayout();
            shot = CaptureBrowser(browser);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tab] Slide-out capture failed: {ex.Message}");
            return;
        }
        if (shot == null)
            return;

        double width = Math.Max(browser.Bounds.Width, 1);
        double height = Math.Max(browser.Bounds.Height, 1);
        var slide = new TranslateTransform(0, 0);
        var overlay = new Image
        {
            Source = shot,
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            IsHitTestVisible = false,
            ZIndex = 4,
            RenderTransform = slide
        };
        grid.Children.Add(overlay);
        _slideOutOverlay = overlay;

        double distance = Math.Max(Bounds.Width, width);
        var duration = TimeSpan.FromMilliseconds(460);
        TimeSpan? start = null;

        void OnFrame(TimeSpan time)
        {
            if (token != _slideOutToken || !IsVisible || overlay.Parent != grid)
            {
                if (_slideOutOverlay == overlay)
                    RemoveSlideOutOverlay();
                else
                    DisposeOverlay(overlay);
                return;
            }

            start ??= time;
            double t = (time - start.Value).TotalMilliseconds / duration.TotalMilliseconds;
            if (t >= 1)
            {
                slide.X = distance;
                RemoveSlideOutOverlay();
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);
            slide.X = distance * eased;
            RequestAnimationFrame(OnFrame);
        }

        RequestAnimationFrame(OnFrame);
    }

    private void RemoveSlideOutOverlay()
    {
        var overlay = _slideOutOverlay;
        _slideOutOverlay = null;
        if (overlay != null)
            DisposeOverlay(overlay);
    }

    private static void DisposeOverlay(Image overlay)
    {
        if (overlay.Parent is Panel panel)
            panel.Children.Remove(overlay);
        var bitmap = overlay.Source as Bitmap;
        overlay.Source = null;
        bitmap?.Dispose();
    }

    private void BeginSlideIn(string tabId)
    {
        double width = Math.Max(Bounds.Width, 1);
        AvaloniaCefBrowser? incoming = null;
        var slide = new TranslateTransform(width, 0);

        foreach (var kvp in _tabs)
        {
            var browser = kvp.Value.Browser;
            if (browser == null)
                continue;
            if (kvp.Key == tabId)
            {
                browser.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                browser.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                browser.Width = double.NaN;
                browser.Height = double.NaN;
                browser.Margin = new Thickness(0);
                browser.IsVisible = true;
                browser.ZIndex = 3;
                browser.RenderTransform = slide;
                incoming = browser;
            }
            else if (browser.IsVisible)
            {
                browser.ZIndex = 1;
            }
        }

        Border? sheet = null;
        var grid = this.FindControl<Grid>("MainGrid");
        if (incoming != null && grid != null)
        {
            sheet = new Border
            {
                Background = Brushes.White,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                IsHitTestVisible = false,
                ZIndex = 2,
                RenderTransform = slide
            };
            grid.Children.Add(sheet);
            SlideBrowserIn(incoming, sheet, slide, tabId, width);
        }
    }

    private void SlideBrowserIn(AvaloniaCefBrowser browser, Border sheet, TranslateTransform slide, string tabId, double width)
    {
        var duration = TimeSpan.FromMilliseconds(460);
        TimeSpan? start = null;

        void OnFrame(TimeSpan time)
        {
            if (browser.RenderTransform != slide)
            {
                FinishSlide(browser, sheet, slide, tabId);
                return;
            }

            start ??= time;
            double t = (time - start.Value).TotalMilliseconds / duration.TotalMilliseconds;
            if (t >= 1)
            {
                slide.X = 0;
                FinishSlide(browser, sheet, slide, tabId);
                return;
            }

            double eased = 1 - Math.Pow(1 - t, 3);
            slide.X = width * (1 - eased);
            RequestAnimationFrame(OnFrame);
        }

        RequestAnimationFrame(OnFrame);
    }

    private void FinishSlide(AvaloniaCefBrowser browser, Border sheet, TranslateTransform slide, string tabId)
    {
        if (sheet.Parent is Panel panel)
            panel.Children.Remove(sheet);

        if (browser.RenderTransform == slide)
            browser.RenderTransform = null;

        if (_viewModel?.ActiveTab?.Id == tabId)
            ApplyTabChrome(tabId);

        SyncContentFullscreenWindow();
    }

    private void ApplyTabChrome(string tabId)
    {
        foreach (var kvp in _tabs)
        {
            var browser = kvp.Value.Browser;
            if (browser == null)
                continue;
            if (kvp.Key == tabId)
            {
                browser.IsVisible = true;
                browser.ZIndex = 1;
                SyncPageViewBar(kvp.Value);
                bool push = kvp.Value.Viewport != null || kvp.Value.AppliedUserAgent != null;
                LayoutPageView(kvp.Value, pushMetrics: push, reload: false);
            }
            else
            {
                browser.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                browser.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                browser.Width = 1;
                browser.Height = 1;
                browser.Margin = new Thickness(-9999, -9999, 0, 0);
                browser.IsVisible = false;
                browser.ZIndex = 0;
            }
        }

        SyncContentFullscreenWindow();
    }

    private void SetContentFullscreen(string tabId, bool fullscreen)
    {
        if (!_tabs.TryGetValue(tabId, out var state))
            return;

        state.ContentFullscreen = fullscreen;
        Console.WriteLine($"[Fullscreen] {(fullscreen ? "enter" : "exit")} {tabId}");
        if (_viewModel?.ActiveTab?.Id == tabId)
            SyncContentFullscreenWindow();
    }

    private void SyncContentFullscreenWindow()
    {
        var state = GetActiveTabState();
        if (state?.ContentFullscreen == true)
            EnterContentFullscreen(state);
        else if (_osFullscreen)
            ExitContentFullscreen();
    }

    private void EnterContentFullscreen(TabState state)
    {
        CloseSuperMenu();
        foreach (var pane in _pinnedPanes)
            pane.Suppress(true);
        if (!_osFullscreen)
        {
            _restoreWindowState = WindowState;
            _restoreDecorations = WindowDecorations;
            _restorePosition = Position;
            _restoreWidth = Width;
            _restoreHeight = Height;
            _restoreNormalBounds = WindowState == WindowState.Normal;
            var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
            _restorePageViewBar = bar?.IsVisible == true;
            _osFullscreen = true;
            _fullscreenMetricsWidth = 0;
            _fullscreenMetricsHeight = 0;
            if (bar != null)
                bar.IsVisible = false;
            var failsafe = this.FindControl<Button>("FailsafeMenuButton");
            if (failsafe != null)
                failsafe.IsVisible = false;
            // CEF sizes a fullscreen element to the browser view. Cover the monitor
            // so that view is the screen, not the restored window.
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
            WindowState = WindowState.FullScreen;
        }

        StretchForFullscreen(state);
        state.Browser?.Focus();
        ScheduleFullscreenMetrics(state);
    }

    private void ExitContentFullscreen()
    {
        if (!_osFullscreen)
            return;

        RestoreWindowChrome();

        var state = GetActiveTabState();
        if (state != null)
            LayoutPageView(state, pushMetrics: state.Viewport != null || state.AppliedUserAgent != null, reload: false);
        foreach (var pane in _pinnedPanes)
            pane.Suppress(false);
    }

    private void RestoreWindowChrome()
    {
        if (!_osFullscreen)
            return;

        _osFullscreen = false;
        _fullscreenMetricsWidth = 0;
        _fullscreenMetricsHeight = 0;
        var failsafe = this.FindControl<Button>("FailsafeMenuButton");
        if (failsafe != null)
            failsafe.IsVisible = true;
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        if (bar != null)
            bar.IsVisible = _restorePageViewBar;

        WindowState = _restoreWindowState;
        WindowDecorations = _restoreDecorations;
        if (_restoreNormalBounds && _restoreWindowState == WindowState.Normal
            && _restoreWidth > 50 && _restoreHeight > 50)
        {
            Position = _restorePosition;
            Width = _restoreWidth;
            Height = _restoreHeight;
        }
    }

    private void StretchForFullscreen(TabState state)
    {
        var browser = state.Browser;
        if (browser == null)
            return;
        browser.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        browser.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        browser.Width = double.NaN;
        browser.Height = double.NaN;
        browser.Margin = new Thickness(0);
        browser.IsVisible = true;
        this.FindControl<Grid>("MainGrid")?.ClearValue(Panel.BackgroundProperty);
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        if (bar != null)
            bar.IsVisible = false;
    }

    private void ScheduleFullscreenMetrics(TabState state)
    {
        if (state.Viewport == null || !state.ContentFullscreen)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!state.ContentFullscreen || state.Viewport == null || !_osFullscreen)
                return;

            int width = Math.Max(1, (int)Math.Round(state.Browser?.Bounds.Width ?? 0));
            int height = Math.Max(1, (int)Math.Round(state.Browser?.Bounds.Height ?? 0));
            if (width < 50 || height < 50)
                return;
            if (width == _fullscreenMetricsWidth && height == _fullscreenMetricsHeight)
                return;

            _fullscreenMetricsWidth = width;
            _fullscreenMetricsHeight = height;
            var full = state.Viewport.Clone();
            full.Width = width;
            full.Height = height;
            full.Fit = false;
            full.Scale = 1;
            double dsf = RenderScaling;
            if (dsf < 0.5 || double.IsNaN(dsf) || double.IsInfinity(dsf))
                dsf = 1;
            SubmitPageView(state, full, width, height, dsf, reload: false);
        }, DispatcherPriority.Background);
    }

    private void OnContentFullscreenKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_osFullscreen)
            return;

        var state = GetActiveTabState();
        if (state?.ContentFullscreen == true && state.Browser != null)
        {
            var browser = state.Browser;
            CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
            {
                var host = browser.GetBrowserHost();
                try
                {
                    host?.ExitFullscreen(true);
                }
                finally
                {
                    host?.Dispose();
                }
            }));
        }
        else
        {
            ExitContentFullscreen();
        }

        e.Handled = true;
    }

    private void OnBrowserZoomKeyDown(object? sender, KeyEventArgs e)
    {
        var focusedPinned = _pinnedPanes.LastOrDefault(pane => pane.IsKeyboardFocusWithin)?.Browser;
        BrowserZoomShortcuts.TryHandle(e, focusedPinned ?? GetActiveTabState()?.Browser);
    }

    private static void OnTopLevelDragEnter(object? sender, DragEventArgs e)
    {
        Console.WriteLine($"[CEF-DND] MainWindow enter source={e.Source?.GetType().Name ?? "<null>"} effects={e.DragEffects} files={e.DataTransfer.TryGetFiles()?.Length ?? 0} bitmap={e.DataTransfer.TryGetBitmap() != null} text={!string.IsNullOrEmpty(e.DataTransfer.TryGetText())}");
    }

    private static void OnTopLevelDragOver(object? sender, DragEventArgs e)
    {
        if (DateTime.UtcNow - _lastTopLevelDragOverLog < TimeSpan.FromSeconds(1))
            return;
        _lastTopLevelDragOverLog = DateTime.UtcNow;
        Console.WriteLine($"[CEF-DND] MainWindow over source={e.Source?.GetType().Name ?? "<null>"} effects={e.DragEffects}");
    }

    private static void OnTopLevelDragLeave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Console.WriteLine("[CEF-DND] MainWindow leave");
    }

    private static void OnBrowserAreaDragEnter(object? sender, DragEventArgs e)
    {
        Console.WriteLine($"[CEF-DND] MainGrid enter source={e.Source?.GetType().Name ?? "<null>"} effects={e.DragEffects}");
    }

    private static void OnBrowserAreaDragOver(object? sender, DragEventArgs e)
    {
        if (DateTime.UtcNow - _lastTopLevelDragOverLog < TimeSpan.FromSeconds(1))
            return;
        _lastTopLevelDragOverLog = DateTime.UtcNow;
        Console.WriteLine($"[CEF-DND] MainGrid over source={e.Source?.GetType().Name ?? "<null>"} effects={e.DragEffects}");
    }

    private static void OnBrowserAreaDrop(object? sender, DragEventArgs e)
    {
        Console.WriteLine($"[CEF-DND] MainGrid drop source={e.Source?.GetType().Name ?? "<null>"} effects={e.DragEffects}");
    }

    private static void OnTopLevelDrop(object? sender, DragEventArgs e)
    {
        Console.WriteLine($"[CEF-DND] MainWindow received drop effects={e.DragEffects}");
    }

    private void OpenPageView()
    {
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        var state = GetActiveTabState();
        var grid = this.FindControl<Grid>("MainGrid");
        if (bar == null || state == null)
            return;

        bar.IsVisible = true;
        if (state.Viewport == null)
        {
            int width = Math.Max(PageViewMin, (int)Math.Round(grid?.Bounds.Width ?? state.Browser?.Bounds.Width ?? PageViewMin));
            int height = Math.Max(PageViewMin, (int)Math.Round((grid?.Bounds.Height ?? state.Browser?.Bounds.Height ?? PageViewMin) - Controls.PageViewBar.BarHeight));
            bar.Load(null);
            bar.ShowCurrentSize(width, height);
            bar.SetShowingFull();
        }
        else
        {
            bar.Load(state.Viewport);
        }

        LayoutPageView(state, pushMetrics: state.Viewport != null, reload: false);
    }

    private void OnPageViewEdited()
    {
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        var state = GetActiveTabState();
        if (bar == null || state == null)
            return;

        state.Viewport = bar.Read();
        LayoutPageView(state, pushMetrics: true, reload: true);
        if (state.Viewport == null)
        {
            var grid = this.FindControl<Grid>("MainGrid");
            int width = Math.Max(PageViewMin, (int)Math.Round(grid?.Bounds.Width ?? state.Browser?.Bounds.Width ?? PageViewMin));
            int height = Math.Max(PageViewMin, (int)Math.Round((grid?.Bounds.Height ?? state.Browser?.Bounds.Height ?? PageViewMin) - Controls.PageViewBar.BarHeight));
            bar.ShowCurrentSize(width, height);
        }
    }

    private void SyncPageViewBar(TabState state)
    {
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        if (bar == null || !bar.IsVisible)
            return;

        if (state.Viewport == null)
        {
            var grid = this.FindControl<Grid>("MainGrid");
            int width = Math.Max(PageViewMin, (int)Math.Round(grid?.Bounds.Width ?? 1280));
            int height = Math.Max(PageViewMin, (int)Math.Round((grid?.Bounds.Height ?? 800) - Controls.PageViewBar.BarHeight));
            bar.Load(null);
            bar.ShowCurrentSize(width, height);
            bar.SetShowingFull();
            return;
        }

        bar.Load(state.Viewport);
    }

    private void ReflowPageView()
    {
        var state = GetActiveTabState();
        var browser = state?.Browser;
        var viewport = state?.Viewport;
        if (browser == null || viewport is not { Fit: true })
            return;

        var grid = this.FindControl<Grid>("MainGrid");
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        if (grid == null || grid.Bounds.Width < 10 || grid.Bounds.Height < 10)
            return;

        double top = bar?.IsVisible == true ? Controls.PageViewBar.BarHeight : 0;
        double factor = FitFactor(grid.Bounds, viewport, top);
        int viewWidth = ClampView((int)Math.Round(viewport.Width * factor));
        int viewHeight = ClampView((int)Math.Round(viewport.Height * factor));
        if (Math.Abs(browser.Width - viewWidth) < 0.5 && Math.Abs(browser.Height - viewHeight) < 0.5)
            return;
        if (state == null)
            return;

        LayoutPageView(state, pushMetrics: true, reload: false);
    }

    private void LayoutPageView(TabState state, bool pushMetrics, bool reload)
    {
        if (state.Browser == null)
            return;

        if (state.ContentFullscreen)
        {
            StretchForFullscreen(state);
            ScheduleFullscreenMetrics(state);
            return;
        }

        var browser = state.Browser;
        var grid = this.FindControl<Grid>("MainGrid");
        var bar = this.FindControl<Controls.PageViewBar>("PageViewBar");
        bool barOpen = bar?.IsVisible == true;
        double top = barOpen ? Controls.PageViewBar.BarHeight : 0;
        var viewport = state.Viewport;

        if (viewport == null)
        {
            browser.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            browser.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            browser.Width = double.NaN;
            browser.Height = double.NaN;
            browser.Margin = new Thickness(0, top, 0, 0);
            grid?.ClearValue(Panel.BackgroundProperty);
            if (barOpen)
                bar?.SetShowingFull();
            if (pushMetrics)
                SubmitPageView(state, null, 0, 0, 1, reload);
            return;
        }

        if (grid == null || grid.Bounds.Width < 10 || grid.Bounds.Height < 10)
            return;

        double factor = viewport.Fit ? FitFactor(grid.Bounds, viewport, top) : viewport.Scale;
        int viewWidth = ClampView((int)Math.Round(viewport.Width * factor));
        int viewHeight = ClampView((int)Math.Round(viewport.Height * factor));

        browser.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        browser.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        browser.Margin = new Thickness(0, top, 0, 0);
        browser.Width = viewWidth;
        browser.Height = viewHeight;
        grid.Background = new SolidColorBrush(Color.Parse("#D5D9DE"));
        browser.UpdateLayout();
        if (barOpen)
            bar?.SetShowing(viewWidth, viewHeight);

        if (pushMetrics)
        {
            double dsf = RenderScaling;
            if (dsf < 0.5 || double.IsNaN(dsf) || double.IsInfinity(dsf))
                dsf = 1;
            SubmitPageView(state, viewport, viewWidth, viewHeight, dsf, reload);
        }
    }

    private static double FitFactor(Rect bounds, PageViewport viewport, double topInset)
    {
        const double pad = 24;
        double availW = Math.Max(1, bounds.Width - pad * 2);
        double availH = Math.Max(1, bounds.Height - topInset - pad * 2);
        double fit = Math.Min(availW / Math.Max(1, viewport.Width), availH / Math.Max(1, viewport.Height));
        return Math.Clamp(fit, 0.1, 1);
    }

    private static int ClampView(int value)
    {
        return Math.Clamp(value, PageViewMin, PageViewMax);
    }

    private void SubmitPageView(
        TabState state,
        PageViewport? viewport,
        int viewWidth,
        int viewHeight,
        double deviceScaleFactor,
        bool reload)
    {
        string? nextAgent = viewport?.UserAgent;
        bool agentChanged = reload && state.AppliedUserAgent != nextAgent;
        state.AppliedUserAgent = nextAgent;
        int token = ++_pageViewSubmit;
        var browser = state.Browser;
        if (browser == null)
            return;

        _ = SubmitCore();

        async Task SubmitCore()
        {
            await PageViewportClient.SubmitAsync(
                () => browser.GetBrowserHost(),
                viewport,
                viewWidth,
                viewHeight,
                deviceScaleFactor);
            if (!agentChanged || token != _pageViewSubmit)
                return;

            await Task.Delay(150);
            if (token != _pageViewSubmit)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token != _pageViewSubmit)
                    return;
                browser.Reload();
            });
        }
    }

    private const string OpenOnDeviceTitle = "Open on another device";

    private Task OpenOnAnotherDeviceAsync()
    {
        // Only a NEWER request supersedes this one. Closing the menu (CloseSuperMenu bumps
        // _openOnDeviceGeneration, and the menu closes as soon as the toast steals focus) used to
        // make the scan return silently after "Looking for devices…".
        var request = Interlocked.Increment(ref _openOnRequestGeneration);
        string? url = _copyContext?.LinkUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = _viewModel?.ActiveTab?.Url;
        var title = string.IsNullOrWhiteSpace(_viewModel?.ActiveTab?.Title)
            ? "Shared Link"
            : _viewModel!.ActiveTab!.Title;
        CloseSuperMenu();
        return RunOpenOnDeviceFlowAsync(url, title,
            () => request != Volatile.Read(ref _openOnRequestGeneration),
            tryShowInline: null,
            toast: true);
    }

    private Task OpenPinnedOnAnotherDeviceAsync(PageCopyContext copy)
    {
        var generation = Interlocked.Increment(ref _pinShareGeneration);
        var url = HttpShareUrl(copy.LinkUrl)
            ?? (copy.IsImage ? HttpShareUrl(copy.ImageUrl) : null)
            ?? HttpShareUrl(_pinnedPane?.Address);
        _pinShareUrl = url;
        _pinShareTitle = "Shared Link";
        return RunOpenOnDeviceFlowAsync(url, _pinShareTitle,
            () => generation != Volatile.Read(ref _pinShareGeneration),
            tryShowInline: devices =>
            {
                if (_pinnedPane?.IsMenuOpen != true) return false;
                _pinShareDevices = devices.ToList();
                _pinShareUrl = url;
                var names = devices
                    .Select(device => string.IsNullOrWhiteSpace(device.DeviceName) ? device.Host : device.DeviceName)
                    .ToList();
                _pinnedPane?.ShowChoices(names);
                return true;
            },
            toast: false);
    }

    private NexZen.Application.Services.OpenOnDeviceFlow? CreateOpenOnDeviceFlow(
        Func<bool> superseded,
        Func<IReadOnlyList<DiscoveryMessage>, bool>? tryShowInline,
        bool toast)
    {
        var share = _viewModel?.NexShare;
        if (share == null)
            return null;
        return new NexZen.Application.Services.OpenOnDeviceFlow
        {
            GetReachable = async () => await Dispatcher.UIThread.InvokeAsync(() => (IReadOnlyList<DiscoveryMessage>)share.ReachableDevices()),
            Send = (d, u, t) => share.SendUrlToDeviceAsync(d, u, t),
            Notify = (msg, isError) => Dispatcher.UIThread.Post(() =>
            {
                if (!toast && !isError && _pinnedPane?.IsMenuOpen == true)
                {
                    _pinnedPane.ShowShareStatus(msg);
                    return;
                }
                if (!toast && _pinnedPane?.IsMenuOpen == true)
                    _pinnedPane.CloseMenu();
                ShowNotification(OpenOnDeviceTitle, msg, isError ? NotificationType.Warning : NotificationType.Information);
            }),
            Pick = devices => share.PickDeviceAsync(devices),
            TryShowInline = tryShowInline == null ? null : devices => Dispatcher.UIThread.Invoke(() => tryShowInline(devices)),
            PortsClosed = async () => await Dispatcher.UIThread.InvokeAsync(() =>
                !share.IsActive && share.ConnectionStatus?.Contains("Ports are closed", StringComparison.Ordinal) == true),
            EnsureStarted = async () => await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!share.IsActive)
                {
                    NexZen.Application.Services.OpenOnLog.Write("NexShare was off -> turning on");
                    share.IsActive = true;
                }
            }),
            IsSuperseded = superseded,
        };
    }

    private async Task RunOpenOnDeviceFlowAsync(
        string? url,
        string title,
        Func<bool> superseded,
        Func<IReadOnlyList<DiscoveryMessage>, bool>? tryShowInline,
        bool toast)
    {
        var flow = CreateOpenOnDeviceFlow(superseded, tryShowInline, toast);
        if (flow == null)
        {
            NexZen.Application.Services.OpenOnLog.Write("NexShare view model missing");
            ShowNotification(OpenOnDeviceTitle, "NexShare is not ready.", NotificationType.Warning);
            return;
        }
        var outcome = await flow.RunAsync(url, title);
        NexZen.Application.Services.OpenOnLog.Write($"outcome={outcome}");
    }

    private async Task SendSharedUrlAsync(DiscoveryMessage device, string url, string title)
    {
        var flow = CreateOpenOnDeviceFlow(() => false, null, toast: true);
        if (flow == null)
            return;
        var outcome = await flow.SendToAsync(device, url, title);
        NexZen.Application.Services.OpenOnLog.Write($"outcome={outcome} (chosen in pinned pane)");
    }

    private static string? HttpShareUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var text = url.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal))
            text = "https:" + text;
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return text;
        return null;
    }

    private async Task RunPinEditAsync(PageCopyContext copy, Func<Task> action, Controls.PinnedPane? pane = null)
    {
        var previousBrowser = _actionBrowser;
        var previousCopy = _copyContext;
        _actionBrowser = (pane ?? _pinnedPane)?.Browser;
        _copyContext = copy;
        try
        {
            await action();
        }
        finally
        {
            _actionBrowser = previousBrowser;
            _copyContext = previousCopy;
        }
    }

    private async Task RunOnBrowserAsync(AvaloniaCefBrowser? browser, Func<Task> action)
    {
        var previous = _actionBrowser;
        _actionBrowser = browser;
        try
        {
            await action();
        }
        finally
        {
            _actionBrowser = previous;
        }
    }

    private AvaloniaCefBrowser? ActionBrowser()
    {
        return _actionBrowser ?? GetActiveTabState()?.Browser;
    }

    private void CloseSuperMenu()
    {
        Interlocked.Increment(ref _openOnDeviceGeneration);
        _superMenuControl?.HideRevealedPasswords();
        _superMenuControl?.CollapseImageSettings();
        _superMenuControl?.CollapseTools();
        App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>()?.Lock();
        if (_closingMenu || _menuWindow == null) return;
        _closingMenu = true;

        try
        {
            var win = _menuWindow;
            _menuWindow = null;

            // Explicitly remove _superMenuControl from its parent before clearing,
            // to avoid Avalonia leaving it in a broken "orphaned" visual state
            // that prevents it from being re-added to a new grid on next open.
            if (win.Content is Grid overlayGrid)
            {
                if (_superMenuControl is { } superMenuControl
                    && overlayGrid.Children.Contains(superMenuControl))
                    overlayGrid.Children.Remove(superMenuControl);
                overlayGrid.Children.Clear();
            }

            win.Content = null;
            win.Close();
        }
        finally
        {
            _closingMenu = false;
        }
    }

    private void ShowSuperMenu(int x, int y, PageCopyContext copy)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CloseSuperMenu();
            _copyContext = copy;
            _superMenuControl.ResetToMainMenu();
            _superMenuControl.SetSaveImageVisible(copy.IsImage);
            _superMenuControl.SetOpenInNewTabVisible(NewTabTarget(copy) != null);
            _superMenuControl.SetPasteVisible(false);
            _ = UpdatePasteVisibilityAsync();
            _superMenuControl.SetOpenInNewWindowVisible(CanOpenInNewWindow(copy));
            _superMenuControl.SetOpenOnPinnedWindowVisible(true);
            _superMenuControl.SetMuteTabLabel(GetActiveTabState()?.AudioMuted == true);
            UpdateReadAloudLabel(copy);
            _superMenuControl.SetSearchAvailable(copy.IsImage || !string.IsNullOrWhiteSpace(copy.SelectionText));
            var currentUrl = GetCurrentPageAddress();
            _superMenuControl.SetDomainLockedState(App.ServiceProvider?.GetService<DomainLockStore>()?.IsLocked(currentUrl) == true);

            var overlayGrid = new Grid();
            
            // Transparent background to capture outside clicks
            var backgroundBorder = new Border { Background = Avalonia.Media.Brushes.Transparent };
            backgroundBorder.PointerPressed += (s, e) => CloseSuperMenu();
            overlayGrid.Children.Add(backgroundBorder);

            _superMenuControl.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            _superMenuControl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

            // Safety: ensure the control has no stale parent before adding to the new grid
            if (_superMenuControl.Parent is Panel staleParent)
                staleParent.Children.Remove(_superMenuControl);

            overlayGrid.Children.Add(_superMenuControl);
            overlayGrid.Children.Add(BuildSuperMenuTabStrip());

            _menuAnchorX = x;
            _menuAnchorY = y;

            _menuWindow = new Window
            {
                Content = overlayGrid,
                Width = this.Bounds.Width,
                Height = this.Bounds.Height,
                WindowDecorations = global::Avalonia.Controls.WindowDecorations.None,
                ShowInTaskbar = false,
                CanResize = false,
                Background = Avalonia.Media.Brushes.Transparent,
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
                ShowActivated = true,
            };
            _menuWindow.AddHandler(KeyDownEvent, OnEditKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnBrowserZoomKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnReopenClosedTabKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnNewTabKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnDevToolsKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnNewIncognitoKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnPrintKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnAiFunctionKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnWorkspaceKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnFindKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyDownEvent, OnTabSwitcherKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _menuWindow.AddHandler(KeyUpEvent, OnTabSwitcherKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            PositionSuperMenu();
            _superMenuControl.SetTabCount(_viewModel?.Tabs.Count ?? 0);

            // Position the overlay exactly over the main window
            var mainPos = this.PointToScreen(new Point(0, 0));
            _menuWindow.Position = mainPos;
            _menuWindow.Show(this);
            _superMenuControl.FocusAddressBar();
            if (!copy.IsImage && string.IsNullOrWhiteSpace(copy.SelectionText))
                _ = FillSelectionForSearchAsync(copy);
        });
    }

    private const double SuperMenuTabStripHeight = 64;

    /// <summary>
    /// A row of the open tabs along the bottom of the window while the SuperMenu is open: favicon and
    /// title on each card, the current tab highlighted, scrolled sideways (also with the mouse wheel)
    /// when there are many. Clicking a card switches to that tab.
    /// </summary>
    private Control BuildSuperMenuTabStrip()
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Control? activeCard = null;
        foreach (var tab in _viewModel?.Tabs.ToList() ?? [])
        {
            var active = tab.Id == _viewModel?.ActiveTab?.Id;
            Control icon = tab.FaviconSource is IImage favicon
                ? new Image { Source = favicon, Width = 16, Height = 16 }
                : new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.Parse("#D9D9D9")),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(tab.Title) ? "·" : tab.Title[..1].ToUpperInvariant(),
                        FontSize = 10,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.Parse("#555555")),
                    },
                };
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title,
                FontSize = 12.5,
                FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = new SolidColorBrush(Color.Parse("#2A2A2A")),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(title, 1);
            var card = new Border
            {
                Width = 210,
                Height = 40,
                Padding = new Thickness(10, 0),
                CornerRadius = new CornerRadius(10),
                // The current tab in the app's accent yellow, the others white.
                Background = new SolidColorBrush(Color.Parse(active ? "#FFF3C4" : "#FFFFFF")),
                BorderBrush = new SolidColorBrush(Color.Parse(active ? "#E2CC6B" : "#D6D6D6")),
                BorderThickness = new Thickness(active ? 2 : 1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    ColumnSpacing = 8,
                    Children = { icon, title },
                },
            };
            ToolTip.SetTip(card, string.IsNullOrWhiteSpace(tab.Title) ? tab.Url : tab.Title + "\n" + tab.Url);
            var id = tab.Id;
            card.PointerEntered += (_, _) => { if (!active) card.Background = new SolidColorBrush(Color.Parse("#F2F2F2")); };
            card.PointerExited += (_, _) => { if (!active) card.Background = Brushes.White; };
            card.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                CloseSuperMenu();
                if (_viewModel?.Tabs.Any(t => t.Id == id) == true)
                {
                    _viewModel.SwitchTab(id);
                    Dispatcher.UIThread.Post(() => GetActiveTabState()?.Browser?.Focus());
                }
            };
            if (active)
                activeCard = card;
            row.Children.Add(card);
        }

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = row,
        };
        // The mouse wheel scrolls the row sideways.
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            scroller.Offset = new Vector(Math.Max(0, scroller.Offset.X - delta * 80), 0);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var strip = new Border
        {
            Height = SuperMenuTabStripHeight,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xF4, 0xF4, 0xF4)),
            BorderBrush = new SolidColorBrush(Color.Parse("#DDDDDD")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = scroller,
        };
        // Clicks between cards keep the menu open (the area outside both closes it).
        strip.PointerPressed += (_, e) => e.Handled = true;
        if (activeCard != null)
            Dispatcher.UIThread.Post(() => activeCard.BringIntoView(), DispatcherPriority.Background);
        return strip;
    }

    private void PositionSuperMenu()
    {
        if (_menuWindow == null) return;

        double windowW = Bounds.Width;
        double windowH = Bounds.Height;
        if (windowW <= 1 || windowH <= 1) return;

        double availableW = windowW - MenuEdgePadding * 2;
        // The open-tabs strip runs along the bottom while the menu is open; keep the menu above it.
        windowH -= SuperMenuTabStripHeight;
        double availableH = windowH - MenuEdgePadding * 2;
        var size = _superMenuControl.FitToViewport(availableW, availableH);

        double rightLimit = windowW - MenuEdgePadding;
        double bottomLimit = windowH - MenuEdgePadding;
        double left = _menuAnchorX;
        double top = _menuAnchorY;

        if (left + size.Width > rightLimit)
            left = rightLimit - size.Width;
        if (top + size.Height > bottomLimit)
            top = bottomLimit - size.Height;
        if (left < MenuEdgePadding)
            left = MenuEdgePadding;
        if (top < MenuEdgePadding)
            top = MenuEdgePadding;

        _superMenuControl.Margin = new Thickness(left, top, 0, 0);
    }

    private void ReopenClosedTabFromUi()
    {
        if (_viewModel?.CanReopenClosedTab != true)
            return;
        CloseSuperMenu();
        _viewModel.ReopenClosedTab();
    }

    private void OpenIncognitoWindow()
    {
        CloseSuperMenu();
        Views.PopupBrowserWindow.OpenIncognito(this, null);
    }

    /// <summary>"Open in new window": incognito windows open incognito windows.</summary>
    private bool OpenWindowLikeThis(string? url) =>
        _incognito ? Views.PopupBrowserWindow.OpenIncognito(this, url) : Views.PopupBrowserWindow.Open(this, url, _viewModel);

    /// <summary>
    /// Site privacy "Sound": mutes the tab on a site set to Mute, and unmutes it again when it
    /// leaves that site (only if it was muted by this rule, not by the user).
    /// </summary>
    private static void ApplySiteSound(TabState state, AvaloniaCefBrowser browser, string url)
    {
        var store = App.ServiceProvider?.GetService<SitePermissionStore>();
        var mute = store?.Get(SiteDomains.SiteOf(url), SitePermissionKeys.Sound) == SitePermissionValue.Block;
        if (mute && !state.AudioMuted)
        {
            state.AudioMuted = true;
            state.MutedBySite = true;
            TabAudio.Apply(browser, true);
        }
        else if (!mute && state.MutedBySite)
        {
            state.AudioMuted = false;
            state.MutedBySite = false;
            TabAudio.Apply(browser, false);
        }
    }

    private void ToggleMuteActiveTab()
    {
        var state = GetActiveTabState();
        var browser = state?.Browser;
        CloseSuperMenu();
        if (state == null || browser == null)
            return;
        state.AudioMuted = !state.AudioMuted;
        state.MutedBySite = false;
        TabAudio.Apply(browser, state.AudioMuted);
    }

    private void OnNewIncognitoKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.N)
            return;
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(KeyModifiers.Control) || !mods.HasFlag(KeyModifiers.Shift))
            return;
        if (mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;
        e.Handled = true;
        OpenIncognitoWindow();
    }

    private void OnPrintKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.P || e.KeyModifiers != KeyModifiers.Control)
            return;
        if (sender is TopLevel top && top.FocusManager?.GetFocusedElement() is TextBox)
            return;
        e.Handled = true;
        var menuOpen = _menuWindow != null;
        var copy = _copyContext;
        CloseSuperMenu();
        // From the open SuperMenu, Ctrl+P does what its Print item does.
        StartPrint(menuOpen ? copy : null);
    }

    private void PrintFromMenu()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        StartPrint(copy);
    }

    /// <summary>
    /// Opens the print dialog for the active page, or for the image the menu was opened on.
    /// The page goes through CEF's PDF output, an image through Skia; then lp or a saved file.
    /// </summary>
    private void StartPrint(PageCopyContext? copy)
    {
        var browser = ActionBrowser();
        var host = browser?.GetBrowserHost();
        var tab = _viewModel?.ActiveTab;
        var pageUrl = browser?.Address ?? tab?.Url;
        var image = copy is { IsImage: true } && !string.IsNullOrWhiteSpace(copy.ImageUrl) ? copy : null;
        if (host == null || (image == null && !IsPrintablePage(pageUrl)))
        {
            ShowNotification("Print", "Open a web page first.", NotificationType.Warning);
            return;
        }
        if (Avalonia.Application.Current is not App app)
            return;

        var title = tab?.Title;
        var subject = image != null ? PageCopy.Preview(image.ImageUrl!) : (string.IsNullOrWhiteSpace(title) ? pageUrl ?? "" : title);
        var fileName = PrintLayout.SuggestedFileName(image != null ? (title is { Length: > 0 } ? title + " - image" : null) : title,
            image?.ImageUrl ?? pageUrl);

        // The image is fetched once per dialog, for the preview and the print alike.
        Task<byte[]?>? imageBytes = null;
        // One PDF at a time: CEF's PrintToPdf can't run twice at once on a browser.
        var pdfGate = new SemaphoreSlim(1, 1);
        async Task<string?> MakePdfAsync(PrintOptions options, string target)
        {
            await pdfGate.WaitAsync();
            try
            {
                if (image == null)
                    return await PrintService.SavePagePdfAsync(host, target, options);
                imageBytes ??= PrintImageBytesAsync(browser!, image);
                var bytes = await imageBytes;
                return bytes == null
                    ? "Couldn't download this image from the page."
                    : await PrintService.SaveImagePdfAsync(bytes, target, options);
            }
            finally
            {
                pdfGate.Release();
            }
        }

        var previewFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexzen-print-preview-" + Guid.NewGuid().ToString("N"));
        async Task<PrintPreview> PreviewAsync(PrintOptions options, CancellationToken token)
        {
            var pdf = System.IO.Path.Combine(previewFolder, Guid.NewGuid().ToString("N") + ".pdf");
            System.IO.Directory.CreateDirectory(previewFolder);
            try
            {
                var error = await MakePdfAsync(options, pdf);
                if (error != null)
                    return new PrintPreview(Array.Empty<string>(), 0, error);
                token.ThrowIfCancellationRequested();
                // Color is done by the printer; show it grey only when that is what will come out.
                return await PrintService.RenderPreviewAsync(pdf, previewFolder, options.Printer != null && !options.Color, token);
            }
            finally
            {
                try { System.IO.File.Delete(pdf); } catch { }
            }
        }

        app.ShowPrintDialog(this, image != null, subject, PreviewAsync, previewFolder, async (options, dialog) =>
        {
            string? target;
            if (options.Printer == null)
            {
                var file = await dialog.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save as PDF",
                    SuggestedFileName = fileName,
                    DefaultExtension = "pdf",
                    FileTypeChoices = new[] { new FilePickerFileType("PDF document") { Patterns = new[] { "*.pdf" } } }
                });
                if (file == null)
                    return "";
                target = file.TryGetLocalPath();
                if (target == null)
                    return "Choose a folder on this computer.";
            }
            else
            {
                target = PrintService.TempPdfPath();
            }

            var error = await MakePdfAsync(options, target);
            if (error != null)
                return error;

            if (options.Printer == null)
            {
                ShowNotification("Print", "Saved " + System.IO.Path.GetFileName(target));
                return null;
            }
            try
            {
                error = await PrintService.SendToPrinterAsync(target, options);
            }
            finally
            {
                try { System.IO.File.Delete(target); } catch { }
            }
            if (error == null)
                ShowNotification("Print", "Sent to " + options.Printer);
            return error;
        });
    }

    private static bool IsPrintablePage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https" or "file" or "data";

    private async Task<byte[]?> PrintImageBytesAsync(AvaloniaCefBrowser browser, PageCopyContext image)
    {
        var bytes = PageCopy.DecodeDataUrl(image.ImageUrl!);
        if (bytes != null)
            return bytes;
        byte[]? result = null;
        await RunOnBrowserAsync(browser, async () =>
        {
            var url = await FindOriginalImageUrlAsync(image);
            if (!string.IsNullOrWhiteSpace(url))
                result = PageCopy.DecodeDataUrl(url) ?? await DownloadPageImageAsync(url);
            if (result == null || result.Length == 0)
                result = await DownloadPageImageAsync(image.ImageUrl!);
        });
        return result is { Length: > 0 } ? result : null;
    }

    /// <summary>Ctrl+N or Cmd+N (exactly, so Ctrl+Shift+N stays the incognito window) opens a new tab.</summary>
    internal static bool IsNewTabChord(KeyEventArgs e)
        => e.Key == Key.N && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta);

    private void OnNewTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsNewTabChord(e))
            return;
        e.Handled = true;
        CloseSuperMenu();
        _viewModel?.AddTab("about:blank");
    }

    private void OnReopenClosedTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.T)
            return;
        var mods = e.KeyModifiers;
        if (!mods.HasFlag(KeyModifiers.Control) || !mods.HasFlag(KeyModifiers.Shift))
            return;
        if (mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;
        e.Handled = true;
        ReopenClosedTabFromUi();
    }

    private void OnAiFunctionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.I || !IsCtrlOrCmdShiftChord(e.KeyModifiers))
            return;
        e.Handled = true;
        CloseSuperMenu();
        if (Avalonia.Application.Current is App app)
            app.ShowAiFunctionWindow();
    }

    // Ctrl+Shift+W, or Cmd+Shift+W on macOS. Chrome uses this chord to close the window;
    // NexZen has no close-window shortcut, so it opens Workspace instead.
    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.W || !IsCtrlOrCmdShiftChord(e.KeyModifiers))
            return;
        e.Handled = true;
        OpenWorkspace();
    }

    private void OpenWorkspace()
    {
        CloseSuperMenu();
        if (_viewModel != null && Avalonia.Application.Current is App app)
            app.ShowWorkspaceWindow(_viewModel, this);
    }

    // Shift plus exactly one of Ctrl or Meta (Cmd on macOS), no Alt.
    internal static bool IsCtrlOrCmdShiftChord(KeyModifiers mods)
    {
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        if (!shift || alt)
            return false;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var meta = mods.HasFlag(KeyModifiers.Meta);
        return (ctrl && !meta) || (meta && !ctrl);
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        var chord = PageFindUi.IsFindChord(e);
        if (!chord && e.Key != Key.Escape)
            return;

        var top = sender as TopLevel ?? this;
        var focused = top.FocusManager?.GetFocusedElement() as InputElement;
        var searchPin = _pinnedPane?.Browser != null
                        && (_pinnedPane.IsMenuOpen || _pinnedPane.IsKeyboardFocusWithin);
        if (chord && !_findUi.ContainsFocus(focused))
        {
            if (_menuWindow != null)
            {
                CloseSuperMenu();
                searchPin = false;
            }
            else if (searchPin)
            {
                _pinnedPane?.CloseMenu();
            }
        }

        var page = searchPin ? _pinnedPane?.Browser : GetActiveTabState()?.Browser;
        _findUi.HandleChromeKey(e, page, focused, searchPin);
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (!EditShortcuts.IsPrimary(e.KeyModifiers))
            return;

        if (sender is TopLevel top && top.FocusManager?.GetFocusedElement() is TextBox)
            return;

        AvaloniaCefBrowser? browser = null;
        if (_pinnedPane?.Browser != null && (_pinnedPane.IsMenuOpen || _pinnedPane.IsKeyboardFocusWithin))
            browser = _pinnedPane.Browser;

        switch (e.Key)
        {
            case Key.C:
                e.Handled = true;
                _ = RunOnBrowserAsync(browser, CopyFromKeyboardAsync);
                break;
            case Key.X:
                e.Handled = true;
                _ = RunOnBrowserAsync(browser, CutFromKeyboardAsync);
                break;
            case Key.V:
                e.Handled = true;
                _ = RunOnBrowserAsync(browser, PasteFromKeyboardAsync);
                break;
        }
    }

    private async Task CopyFromKeyboardAsync()
    {
        try
        {
            var text = await ReadPageSelectionAsync();
            await OnUiAsync(async () =>
            {
                if (string.IsNullOrEmpty(text))
                {
                    ShowCopyNotification("Copy", "Nothing to copy.", null, NotificationType.Warning);
                    return;
                }

                await CopyTextAsync(text);
            });
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                ShowCopyNotification("Copy", "Couldn't copy.", null, NotificationType.Error);
                Console.WriteLine($"[Copy] {ex}");
                return Task.CompletedTask;
            });
        }
    }

    private async Task CutFromMenuAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null || !copy.CanCut)
        {
            ShowCopyNotification("Cut", "Can't cut this content.", null, NotificationType.Warning);
            return;
        }

        FocusedFrame()?.Cut();
        if (!string.IsNullOrEmpty(copy.SelectionText))
            ShowCopyNotification("Cut", PageCopy.Preview(copy.SelectionText), null, NotificationType.Information);
        else
            ShowCopyNotification("Cut", null, null, NotificationType.Information);
    }

    private async Task CutFromKeyboardAsync()
    {
        try
        {
            if (!await IsPageEditableAsync())
            {
                await OnUiAsync(() =>
                {
                    ShowCopyNotification("Cut", "Can't cut this content.", null, NotificationType.Warning);
                    return Task.CompletedTask;
                });
                return;
            }

            var text = await ReadPageSelectionAsync();
            if (string.IsNullOrEmpty(text))
            {
                await OnUiAsync(() =>
                {
                    ShowCopyNotification("Cut", "Nothing to cut.", null, NotificationType.Warning);
                    return Task.CompletedTask;
                });
                return;
            }

            await OnUiAsync(() => CopyTextAsync(text));
            await DeletePageSelectionAsync();
            await OnUiAsync(() =>
            {
                ShowCopyNotification("Cut", PageCopy.Preview(text), null, NotificationType.Information);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                ShowCopyNotification("Cut", "Couldn't cut.", null, NotificationType.Error);
                Console.WriteLine($"[Cut] {ex}");
                return Task.CompletedTask;
            });
        }
    }

    private async Task PasteFromMenuAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null || !copy.IsEditable)
        {
            ShowCopyNotification("Paste", "Nowhere to paste.", null, NotificationType.Warning);
            return;
        }

        if (!copy.CanPaste)
        {
            ShowCopyNotification("Paste", "Clipboard is empty.", null, NotificationType.Warning);
            return;
        }

        await PasteClipboardAsync();
    }

    private async Task PasteFromKeyboardAsync()
    {
        try
        {
            if (!await IsPageEditableAsync())
            {
                await OnUiAsync(() =>
                {
                    ShowCopyNotification("Paste", "Nowhere to paste.", null, NotificationType.Warning);
                    return Task.CompletedTask;
                });
                return;
            }

            await PasteClipboardAsync();
        }
        catch (Exception ex)
        {
            await OnUiAsync(() =>
            {
                ShowCopyNotification("Paste", "Couldn't paste.", null, NotificationType.Error);
                Console.WriteLine($"[Paste] {ex}");
                return Task.CompletedTask;
            });
        }
    }

    private async Task PasteClipboardAsync()
    {
        (string? Text, Bitmap? Image) clip = default;
        await OnUiAsync(async () => clip = await ReadClipboardAsync());
        await OnUiAsync(() =>
        {
            if (clip.Image == null && string.IsNullOrWhiteSpace(clip.Text))
            {
                ShowCopyNotification("Paste", "Clipboard is empty.", null, NotificationType.Warning);
                return Task.CompletedTask;
            }

            FocusedFrame()?.Paste();
            if (clip.Image != null && string.IsNullOrWhiteSpace(clip.Text))
                ShowCopyNotification("Pasted image", null, clip.Image, NotificationType.Information);
            else
                ShowCopyNotification("Pasted", PageCopy.Preview(clip.Text ?? ""), null, NotificationType.Information);
            return Task.CompletedTask;
        });
    }

    private async Task OnUiAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task<string?> ReadPageSelectionAsync()
    {
        var browser = ActionBrowser();
        if (browser == null)
            return null;
        return await browser.EvaluateJavaScript<string>(PageCopy.SelectionScript);
    }

    private async Task<bool> IsPageEditableAsync()
    {
        var browser = ActionBrowser();
        if (browser == null)
            return false;
        return await browser.EvaluateJavaScript<bool>(PageCopy.EditableScript);
    }

    private async Task DeletePageSelectionAsync()
    {
        var browser = ActionBrowser();
        if (browser == null)
            return;
        await browser.EvaluateJavaScript<bool>(PageCopy.DeleteSelectionScript);
    }

    private CefFrame? FocusedFrame()
    {
        var cefBrowser = ActionBrowser()?.GetBrowserHost()?.GetBrowser();
        if (cefBrowser == null)
            return null;
        return cefBrowser.GetFocusedFrame() ?? cefBrowser.GetMainFrame();
    }

    // The clipboard is read after the menu opens, so a slow clipboard owner doesn't delay it.
    private async Task UpdatePasteVisibilityAsync()
    {
        var hasContent = false;
        try
        {
            if (Clipboard != null)
            {
                var formats = await Clipboard.GetDataFormatsAsync();
                if (formats.Any(f => f.Identifier.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                     || Equals(f, DataFormat.Bitmap)))
                    hasContent = true;
                else if (formats.Any(f => Equals(f, DataFormat.Text)))
                    hasContent = !string.IsNullOrEmpty(await Clipboard.TryGetTextAsync());
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Paste] clipboard check: {ex.Message}");
        }
        if (_menuWindow != null)
            _superMenuControl.SetPasteVisible(hasContent);
    }

    private async Task<(string? Text, Bitmap? Image)> ReadClipboardAsync()
    {
        if (Clipboard == null)
            return (null, null);

        string? text = null;
        Bitmap? image = null;
        try
        {
            var pngFormat = DataFormat.CreateBytesPlatformFormat("image/png");
            var png = await Clipboard.TryGetValueAsync(pngFormat);
            if (png != null && png.Length > 0)
            {
                image = PageCopy.DecodeBitmap(png);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Paste] Image clipboard: {ex.Message}");
        }

        try
        {
            text = await Clipboard.TryGetTextAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Paste] Text clipboard: {ex.Message}");
        }

        return (text, image);
    }

    private void InstallStoreExtension(string extensionId)
    {
        _ = InstallFromWebStoreAsync(extensionId);
    }

    private async Task InstallFromWebStoreAsync(string extensionId)
    {
        try
        {
            ShowCopyNotification("Extension", "Downloading the extension from the store...", null, NotificationType.Information);
            var unpacked = await WebStoreInstaller.DownloadUnpackedAsync(extensionId);
            if (_viewModel == null)
                return;
            var item = _extensionCatalog.InstallFromFolder(unpacked);
            _viewModel.Extensions.Add(item);
            _extensionCatalog.Save(_viewModel.Extensions);
            
            var tutorialImage = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://NexZen.Desktop/Assets/RestartTutorial.png")));
            _viewModel.ConfirmPrompt.QrImage = tutorialImage;
            _viewModel.ConfirmPrompt.IsQrVisible = true;
            await _viewModel.ConfirmPrompt.ShowAsync(
                "Extension installed",
                $"Extension \"{item.Name}\" was installed.\n\nRestart NexZen so Chromium can load it.\n\n1. Close this message.\n2. Right-click the NexZen icon in the system tray.\n3. Choose \"Exit\".\n4. Open NexZen again.",
                null,
                null,
                "Got it"
            );
            _viewModel.ConfirmPrompt.QrImage = null;
            _viewModel.ConfirmPrompt.IsQrVisible = false;
        }
        catch (Exception ex)
        {
            ShowCopyNotification("Extension", ex.Message, null, NotificationType.Warning);
            Console.WriteLine($"[Extension] {ex}");
        }
    }

    private async Task InstallExtensionAsync()
    {
        var owner = _menuWindow ?? this;
        if (!owner.StorageProvider.CanPickFolder)
        {
            ShowCopyNotification("Extension", "Couldn't choose a folder.", null, NotificationType.Warning);
            return;
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose extension folder",
            AllowMultiple = false
        });
        if (folders.Count == 0 || _viewModel == null)
            return;

        var folder = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowCopyNotification("Extension", "Couldn't read the selected folder.", null, NotificationType.Warning);
            return;
        }

        var item = _extensionCatalog.InstallFromFolder(folder);
        _viewModel.Extensions.Add(item);
        _extensionCatalog.Save(_viewModel.Extensions);
        
        var tutorialImage = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://NexZen.Desktop/Assets/RestartTutorial.png")));
        _viewModel.ConfirmPrompt.QrImage = tutorialImage;
        _viewModel.ConfirmPrompt.IsQrVisible = true;
        await _viewModel.ConfirmPrompt.ShowAsync(
            "Extension installed",
            $"Extension \"{item.Name}\" was installed.\n\nRestart NexZen so Chromium can load it.\n\n1. Close this message.\n2. Right-click the NexZen icon in the system tray.\n3. Choose \"Exit\".\n4. Open NexZen again.",
            null,
            null,
            "Got it"
        );
        _viewModel.ConfirmPrompt.QrImage = null;
        _viewModel.ConfirmPrompt.IsQrVisible = false;
    }

    private void RunExtension(string id)
    {
        var item = _viewModel?.Extensions.FirstOrDefault(entry => entry.Id == id);
        if (item == null || string.IsNullOrEmpty(item.CefId))
        {
            ShowCopyNotification("Extension", "This extension isn't set up correctly.", null, NotificationType.Warning);
            return;
        }

        item.IsRunning = true;
        _extensionCatalog.Save(_viewModel!.Extensions);

        if (string.IsNullOrEmpty(item.Popup))
        {
            ShowCopyNotification("Extension", $"{item.Name} is enabled. Restart NexZen for the change to take effect.", null, NotificationType.Information);
            return;
        }

        CloseSuperMenu();

        var popupUrl = $"chrome-extension://{item.CefId}/{item.Popup}";
        if (ExtensionPopupContent.Content is AvaloniaCefBrowser oldBrowser)
        {
            ExtensionPopupContent.Content = null;
            oldBrowser.Dispose();
        }

        DetachExtensionPopupMount();
        ExtensionPopupContent.Tag = id;
        _extensionPopupMount = (_, _) => MountExtensionPopup(id, popupUrl);
        ExtensionPopupContent.LayoutUpdated += _extensionPopupMount;
        ExtensionPopup.Opened += _extensionPopupMount;
        ExtensionPopup.IsOpen = true;
        Dispatcher.UIThread.Post(() => MountExtensionPopup(id, popupUrl), DispatcherPriority.Loaded);
    }

    private EventHandler? _extensionPopupMount;

    private void DetachExtensionPopupMount()
    {
        if (_extensionPopupMount == null)
            return;
        ExtensionPopupContent.LayoutUpdated -= _extensionPopupMount;
        ExtensionPopup.Opened -= _extensionPopupMount;
        _extensionPopupMount = null;
    }

    private void MountExtensionPopup(string id, string popupUrl)
    {
        if (!ExtensionPopup.IsOpen || ExtensionPopupContent.Tag as string != id)
        {
            DetachExtensionPopupMount();
            return;
        }

        // The page is local. Wait until the popup window exists, or the first paint is dropped.
        if (TopLevel.GetTopLevel(ExtensionPopupContent) == null)
            return;
        if (ExtensionPopupContent.Bounds.Width < 1 || ExtensionPopupContent.Bounds.Height < 1)
            return;
        if (ExtensionPopupContent.Content is AvaloniaCefBrowser)
        {
            DetachExtensionPopupMount();
            return;
        }

        DetachExtensionPopupMount();
        var browser = new AvaloniaCefBrowser { Address = popupUrl };
        ExtensionPopupContent.Content = browser;
    }

    public void StopExtension(string id)
    {
        var item = _viewModel?.Extensions.FirstOrDefault(entry => entry.Id == id);
        if (item == null) return;
        
        item.IsRunning = false;
        _extensionCatalog.Save(_viewModel!.Extensions);
        
        if (ExtensionPopup.IsOpen && ExtensionPopupContent.Tag is string currentId && currentId == id)
        {
            ExtensionPopup.IsOpen = false;
        }
    }

    private void ExtensionPopup_Closed(object? sender, EventArgs e)
    {
        DetachExtensionPopupMount();
        if (ExtensionPopupContent.Content is Xilium.CefGlue.Avalonia.AvaloniaCefBrowser browser)
        {
            browser.Dispose();
            ExtensionPopupContent.Content = null;
        }
    }

    private void OnExtensionLoaded(CefExtension extension)
    {
        Console.WriteLine($"[Bước 0] CEF đã gọi OnExtensionLoaded cho extension: {extension.Identifier}");
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel == null)
                return;

            var loadedPath = extension.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var item = _viewModel.Extensions.FirstOrDefault(entry =>
                string.Equals(entry.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), loadedPath, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                Console.WriteLine($"[Bước 0] Không tìm thấy extension trong _viewModel.Extensions cho path {loadedPath}");
                return;
            }

            item.CefId = extension.Identifier;
            item.IsRunning = true;
            _extensionCatalog.Save(_viewModel.Extensions);

            if (!string.IsNullOrWhiteSpace(item.Popup))
            {
                _viewModel.AddTab("chrome-extension://" + extension.Identifier + "/" + item.Popup.TrimStart('/'));
                CloseSuperMenu();
            }
            else
            {
                ShowCopyNotification("Extension", item.Name + " is running.", null, NotificationType.Information);
            }
        });
    }

    private void MarkExtensionStopped(CefExtension extension)
    {
        var item = _viewModel?.Extensions.FirstOrDefault(entry => entry.CefId == extension.Identifier);
        if (item != null)
            item.IsRunning = false;
    }

    private void DeleteExtension(string id)
    {
        var item = _viewModel?.Extensions.FirstOrDefault(entry => entry.Id == id);
        if (item == null)
            return;

        if (item.IsRunning && !string.IsNullOrWhiteSpace(item.CefId))
        {
            var context = GetActiveTabState()?.Browser?.RequestContext;
            var cefId = item.CefId;
            void Start()
            {
                try
                {
                    var target = context ?? CefRequestContext.GetGlobalContext();
                    target.GetExtension(cefId)?.Unload();
                }
                catch (Exception ex) { Console.WriteLine($"[Extension] {ex.Message}"); }
            }

            if (CefRuntime.CurrentlyOn(CefThreadId.UI))
                Start();
            else
                CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(Start));
        }

        try
        {
            _extensionCatalog.DeleteFiles(item);
        }
        catch (Exception ex)
        {
            ShowCopyNotification("Extension", "Couldn't delete the extension folder.", null, NotificationType.Warning);
            Console.WriteLine($"[Extension] {ex.Message}");
            return;
        }

        _viewModel?.Extensions.Remove(item);
        if (_viewModel != null)
            _extensionCatalog.Save(_viewModel.Extensions);
    }

    private async Task LockCurrentDomainAsync()
    {
        var activeState = GetActiveTabState();
        var browser = activeState?.Browser;
        var currentUrl = GetCurrentPageAddress();
        var locks = App.ServiceProvider?.GetService<DomainLockStore>();
        var auth = App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>();
        if (browser == null || locks == null || auth == null
            || !DomainLockStore.TryGetRootDomain(currentUrl, out var rootDomain))
        {
            ShowCopyNotification("Lock domain", "Open a website before locking its domain.", null, NotificationType.Warning);
            CloseSuperMenu();
            return;
        }

        CloseSuperMenu();
        try
        {
            var hasSession = _domainLockSessions.TryGetValue(browser, out var session);
            var alreadyLocked = locks.IsLocked(currentUrl);
            if (alreadyLocked && hasSession && session!.IsUnlocked(rootDomain))
            {
                if (locks.RemoveLock(currentUrl))
                {
                    session.Lock(rootDomain);
                    ShowCopyNotification("Domain unlocked", $"The lock for {rootDomain} was removed.", null, NotificationType.Information);
                }
                return;
            }

            var needsPinSetup = !locks.IsConfigured;
            if (needsPinSetup && !await auth.CreatePinAsync(this))
            {
                ShowCopyNotification("Domain not locked", "Create a NexZen PIN before locking a domain.", null, NotificationType.Warning);
                return;
            }
            if (!locks.IsConfigured)
                return;
            if (needsPinSetup)
                auth.Lock();

            var lockedRoot = locks.AddLock(currentUrl);
            if (lockedRoot == null)
                return;
            session?.Lock(lockedRoot);

            if (alreadyLocked)
                browser.Reload();
            Program.StartupLog.Write($"[DomainLock] domain lock enabled root={lockedRoot}");
            ShowCopyNotification("Domain locked", $"{lockedRoot} now requires your NexZen PIN in every browser mode.", null, NotificationType.Information);
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[DomainLock] could not update lock: {ex.GetType().Name}");
            ShowCopyNotification("Domain lock failed", "NexZen couldn't update the domain lock.", null, NotificationType.Error);
        }
    }

    private void NavigateFromMenu(string url)
    {
        ShowProgressBar(true);
        var state = GetActiveTabState();
        if (state?.Browser != null)
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("view-source://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("nexzen://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            state.Browser.Address = BlankPage.ForBrowser(url);
        }
        CloseSuperMenu();
    }

    private async Task SearchFromMenuAsync()
    {
        var copy = _copyContext;
        var engine = _viewModel?.SelectedSearchEngine;
        if (copy == null)
        {
            CloseSuperMenu();
            return;
        }

        if (copy.IsImage)
        {
            CloseSuperMenu();
            var resolved = await FindOriginalImageUrlAsync(copy);
            var raw = string.IsNullOrWhiteSpace(resolved) ? copy.ImageUrl : resolved;
            var publicUrl = SearchTargets.PublicImageUrl(raw);
            if (publicUrl == null)
            {
                ShowCopyNotification("Search", "This image has no public address to search.", null, NotificationType.Warning);
                return;
            }

            var target = SearchTargets.ImageUrl(engine, publicUrl);
            if (target == null)
            {
                ShowCopyNotification("Search", SearchTargets.DisplayName(engine) + " has no reverse image search.", null, NotificationType.Warning);
                return;
            }

            NavigateFromMenu(target);
            return;
        }

        var query = copy.SelectionText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            CloseSuperMenu();
            return;
        }

        NavigateFromMenu(SearchTargets.TextUrl(engine, query));
    }

    private async Task FillSelectionForSearchAsync(PageCopyContext copy)
    {
        string? text;
        try
        {
            text = await ReadPageSelectionAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Search] {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_menuWindow == null || !ReferenceEquals(_copyContext, copy) || copy.IsImage)
                return;
            copy.SelectionText = text.Trim();
            _superMenuControl.SetSearchAvailable(!string.IsNullOrWhiteSpace(copy.SelectionText));
            UpdateReadAloudLabel(copy);
        });
    }

    private void UpdateReadAloudLabel(PageCopyContext copy)
    {
        _superMenuControl.SetReadAloudLabel(
            Speech.ReadAloud.State is Speech.ReadAloudState.Preparing or Speech.ReadAloudState.Speaking
                or Speech.ReadAloudState.Paused ? "Stop reading aloud"
            : !copy.IsImage && !string.IsNullOrWhiteSpace(copy.SelectionText) ? "Read selection aloud"
            : "Read page aloud");
    }

    /// <summary>
    /// The read-aloud bar floats at the bottom of the page in a popup (the page's bitmap covers
    /// controls placed inside the page host), and the sentence being read is highlighted on the page.
    /// </summary>
    private void MountReadAloudBar()
    {
        if (this.FindControl<Grid>("MainGrid") is not { } grid)
            return;
        var anchor = new Control
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Width = 0,
            Height = 0,
            Margin = new Thickness(0, 0, 0, 24),
            IsHitTestVisible = false,
        };
        var bar = new Speech.ReadAloudBar(this);
        var popup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = Avalonia.Controls.PlacementMode.Top,
            IsLightDismissEnabled = false,
            Child = bar,
        };
        grid.Children.Add(anchor);
        grid.Children.Add(popup);
        bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
                popup.IsOpen = bar.IsVisible && IsVisible;
        };
        AvaloniaCefBrowser? highlighted = null;
        void OnSentence(object? origin, string sentence)
        {
            if (!ReferenceEquals(origin, this))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                var browser = GetActiveTabState()?.Browser;
                var snippet = HighlightSnippet(sentence);
                if (browser == null || snippet.Length < 4)
                    return;
                highlighted = browser;
                browser.GetBrowserHost()?.Find(snippet, true, false, false);
            });
        }
        void OnState()
        {
            if (Speech.ReadAloud.State != Speech.ReadAloudState.Idle || highlighted == null)
                return;
            var browser = highlighted;
            highlighted = null;
            Dispatcher.UIThread.Post(() => browser.GetBrowserHost()?.StopFinding(true));
        }
        Speech.ReadAloud.SentenceStarted += OnSentence;
        Speech.ReadAloud.StateChanged += OnState;
        Closed += (_, _) =>
        {
            Speech.ReadAloud.SentenceStarted -= OnSentence;
            Speech.ReadAloud.StateChanged -= OnState;
            if (ReferenceEquals(Speech.ReadAloud.Origin, this))
                Speech.ReadAloud.Stop();
        };
    }

    /// <summary>The first words of a sentence, enough to find it on the page (find stops at line breaks).</summary>
    private static string HighlightSnippet(string sentence)
    {
        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var snippet = string.Join(' ', words.Take(8));
        return snippet.TrimEnd('.', ',', ';', ':', '!', '?', '…', '"', '\'', ')');
    }

    /// <summary>SuperMenu "Read aloud": the selection if there is one, else the page's main text.</summary>
    private async Task ReadAloudFromMenuAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (Speech.ReadAloud.State is Speech.ReadAloudState.Preparing or Speech.ReadAloudState.Speaking
            or Speech.ReadAloudState.Paused)
        {
            Speech.ReadAloud.Stop();
            return;
        }
        var selection = copy is { IsImage: false } ? copy.SelectionText?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(selection))
        {
            Speech.ReadAloud.Speak(selection, null, this);
            return;
        }
        var tabId = _viewModel?.ActiveTab?.Id;
        var json = tabId == null ? null : await TryExtractPageJsonAsync(tabId);
        var text = json == null ? "" : Speech.ReadAloud.TextFromPageJson(json, out var title);
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowCopyNotification("Read aloud", "Couldn't find text to read on this page.", null, NotificationType.Information);
            return;
        }
        Speech.ReadAloud.Speak(text, _viewModel?.ActiveTab?.Title, this);
    }

    private async Task CopyImageAddressFromMenuAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null || !copy.IsImage || string.IsNullOrWhiteSpace(copy.ImageUrl))
        {
            ShowCopyNotification("Copy image address", "No image address to copy.", null, NotificationType.Warning);
            return;
        }

        if (Clipboard == null)
        {
            ShowCopyNotification("Copy image address", "Clipboard isn't available.", null, NotificationType.Error);
            return;
        }

        var url = copy.ImageUrl.Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
            url = "https:" + url;

        await Clipboard.SetTextAsync(url);
        ShowCopyNotification("Copied image address", PageCopy.Preview(url), null, NotificationType.Information);
    }

    private async Task SaveImageFromMenuAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null || !copy.IsImage)
        {
            ShowCopyNotification("Save Image", "No image to save.", null, NotificationType.Warning);
            return;
        }

        var url = await FindOriginalImageUrlAsync(copy);
        if (string.IsNullOrWhiteSpace(url))
            url = copy.ImageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowCopyNotification("Save Image", "Couldn't get the original image.", null, NotificationType.Warning);
            return;
        }

        var bytes = PageCopy.DecodeDataUrl(url);
        var extension = bytes == null ? ".png" : DataImageExtension(url);
        var fileType = new FilePickerFileType(extension.TrimStart('.').ToUpperInvariant() + " image")
        {
            Patterns = new[] { "*" + extension }
        };
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save image",
            SuggestedFileName = "image_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = new[] { fileType }
        });
        if (file == null)
            return;

        if (bytes == null)
        {
            // Use CEF's browser request context so cookies, cache and page
            // authentication are available for protected image URLs.
            bytes = await DownloadPageImageAsync(url);
        }

        if (bytes == null || bytes.Length == 0)
        {
            var scheme = Uri.TryCreate(url, UriKind.Absolute, out var imageUri) ? imageUri!.Scheme : "invalid";
            Program.StartupLog.Write($"[SaveImage] image retrieval failed scheme={scheme}");
            ShowCopyNotification("Save Image", "Couldn't download this image from the page.", null, NotificationType.Error);
            return;
        }

        await using (var stream = await file.OpenWriteAsync())
            await stream.WriteAsync(bytes, 0, bytes.Length);
        ShowCopyNotification("Save Image", "Saved " + file.Name, null, NotificationType.Information);
    }

    private static string DataImageExtension(string dataUrl)
    {
        var headerEnd = dataUrl.IndexOfAny(new[] { ';', ',' });
        var mimeType = headerEnd > 5 ? dataUrl.Substring(5, headerEnd - 5) : string.Empty;
        return mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            _ => ".png"
        };
    }

    private async Task<string?> FindOriginalImageUrlAsync(PageCopyContext copy)
    {
        var browser = ActionBrowser();
        if (browser == null)
            return null;

        try
        {
            var found = await browser.EvaluateJavaScript<string>(PageCopy.OriginalImageScript(copy.X, copy.Y));
            if (!string.IsNullOrWhiteSpace(found))
                return found;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SaveImage] {ex.Message}");
        }

        return null;
    }

    private async Task CopyFromPageAsync()
    {
        var copy = _copyContext;
        CloseSuperMenu();
        if (copy == null)
            return;

        if (copy.IsImage)
            await CopyImageAsync(copy.ImageUrl);
        else if (!string.IsNullOrWhiteSpace(copy.SelectionText))
            await CopyTextAsync(copy.SelectionText);
        else
            ShowCopyNotification("Copy", "Nothing to copy.", null, NotificationType.Warning);
    }

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard == null)
        {
            ShowCopyNotification("Copy", "Clipboard isn't available.", null, NotificationType.Error);
            return;
        }

        await Clipboard.SetTextAsync(text);
        ShowCopyNotification("Copied", PageCopy.Preview(text), null, NotificationType.Information);
    }

    private async Task CopyImageAsync(string? imageUrl)
    {
        if (Clipboard == null)
        {
            ShowCopyNotification("Copy", "Clipboard isn't available.", null, NotificationType.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            ShowCopyNotification("Copy", "Couldn't get the image.", null, NotificationType.Warning);
            return;
        }

        byte[]? png = PageCopy.DecodeDataUrl(imageUrl);
        if (png == null)
            png = await DownloadPageImageAsync(imageUrl);

        if (png == null || png.Length == 0)
        {
            ShowCopyNotification("Copy", "Couldn't get the image.", null, NotificationType.Warning);
            return;
        }

        var dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("image/png"), png));
        await Clipboard.SetDataAsync(dataTransfer);
        ShowCopyNotification("Copied image", null, PageCopy.DecodeBitmap(png), NotificationType.Information);
    }

    private Task<byte[]?> DownloadPageImageAsync(string imageUrl)
    {
        var host = ActionBrowser()?.GetBrowserHost();
        if (host == null)
            return Task.FromResult<byte[]?>(null);

        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new ImagePngCallback(png => done.TrySetResult(png));

        void Start()
        {
            try
            {
                host.DownloadImage(imageUrl, false, 0, false, callback);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Copy] DownloadImage: {ex.Message}");
                done.TrySetResult(null);
            }
        }

        if (CefRuntime.CurrentlyOn(CefThreadId.UI))
            Start();
        else
            CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(Start));

        return done.Task;
    }

    public void ShowNotification(string title, string? message, NotificationType type = NotificationType.Information)
    {
        ShowCopyNotification(title, message, null, type);
    }

    private void ShowCopyNotification(string title, string? message, Bitmap? image, NotificationType type)
    {
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            FontSize = 16,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2A2A")),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var isProblem = type is NotificationType.Error or NotificationType.Warning;
        if (isProblem)
            NexZen.Application.Services.OpenOnLog.Write($"toast {type}: {title}: {message}");
        if (!string.IsNullOrWhiteSpace(message))
        {
            panel.Children.Add(new SelectableTextBlock
            {
                Text = message,
                FontSize = 14,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4A4A4A")),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
        }
        if (image != null)
        {
            panel.Children.Add(new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFFFFF")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E4E4E4")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
                Child = new Image
                {
                    Source = image,
                    MaxWidth = 220,
                    MaxHeight = 220,
                    Stretch = Avalonia.Media.Stretch.Uniform
                }
            });
        }

        if (isProblem)
        {
            var copyText = string.IsNullOrWhiteSpace(message) ? title : title + ": " + message;
            var copy = new Button { Content = "Copy" };
            copy.Classes.Add("CopyToast");
            copy.Click += async (_, e) =>
            {
                e.Handled = true;
                try
                {
                    if (Clipboard != null) await Clipboard.SetTextAsync(copyText);
                    copy.Content = "Copied";
                }
                catch { }
            };
            panel.Children.Add(copy);
        }

        _notificationManager?.Show(panel, type, TimeSpan.FromSeconds(isProblem ? 8 : 4));
    }

    private void SyncMenuOverlay()
    {
        if (_menuWindow == null) return;
        _menuWindow.Width = Bounds.Width;
        _menuWindow.Height = Bounds.Height;
        _menuWindow.Position = this.PointToScreen(new Point(0, 0));
    }

    async Task ClearBrowsingDataFromMenuAsync(DataClearRequest request)
    {
        if (_viewModel == null)
            return;

        _superMenuControl.SetDataManagerBusy(true);
        _superMenuControl.SetDataManagerStatus("Deleting…");
        try
        {
            var status = await BrowsingDataCleaner.ClearAsync(
                _viewModel,
                GetActiveTabState()?.Browser,
                request);
            ShowDataManagerStatus(status);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[DataManager] " + ex);
            ShowDataManagerStatus("Could not delete data.");
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess())
                _superMenuControl.SetDataManagerBusy(false);
            else
                Dispatcher.UIThread.Post(() => _superMenuControl.SetDataManagerBusy(false));
        }
    }

    void ShowDataManagerStatus(string text)
    {
        if (Dispatcher.UIThread.CheckAccess())
            _superMenuControl.SetDataManagerStatus(text);
        else
            Dispatcher.UIThread.Post(() => _superMenuControl.SetDataManagerStatus(text));
    }

}

public class CustomContextMenuHandler : Xilium.CefGlue.Common.Handlers.ContextMenuHandler
{
    private readonly Action<int, int, PageCopyContext> _onShowMenu;

    public CustomContextMenuHandler(Action<int, int, PageCopyContext> onShowMenu)
    {
        _onShowMenu = onShowMenu;
    }

    protected override void OnBeforeContextMenu(CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
    {
        // DevTools is a second browser on the same client. Leave its own menu intact.
        if (IsDevTools(browser, frame))
            return;

        model.Clear();

        var copy = PageCopyContext.FromMenu(state);
        _onShowMenu(state.X, state.Y, copy);
    }

    static bool IsDevTools(CefBrowser browser, CefFrame frame)
    {
        if (browser.IsPopup)
            return true;

        if (IsDevToolsUrl(frame.Url))
            return true;

        return IsDevToolsUrl(browser.GetMainFrame()?.Url);
    }

    static bool IsDevToolsUrl(string? url)
    {
        return !string.IsNullOrEmpty(url)
            && url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class UiActionTask : CefTask
{
    private Action? _action;

    public UiActionTask(Action action)
    {
        _action = action;
    }

    protected override void Execute()
    {
        var action = _action;
        _action = null;
        action?.Invoke();
    }
}

internal sealed class FaviconImageCallback : CefDownloadImageCallback
{
    private readonly Action<byte[]?> _onPng;

    public FaviconImageCallback(Action<byte[]?> onPng)
    {
        _onPng = onPng;
    }

    protected override void OnDownloadImageFinished(string imageUrl, int httpStatusCode, CefImage image)
    {
        byte[]? png = null;
        try
        {
            if (httpStatusCode is >= 200 and < 300 && image != null && !image.IsEmpty)
            {
                using var binary = image.GetAsPng(1f, true, out _, out _);
                png = binary?.ToArray();
            }
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[Favicon] image decode failed url={imageUrl} error={ex.Message}");
        }

        _onPng(png);
    }
}
