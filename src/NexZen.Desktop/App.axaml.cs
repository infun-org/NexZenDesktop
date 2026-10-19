using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Application.ViewModels;
using NexZen.Core.Interfaces;
using NexZen.Infrastructure.Data;
using NexZen.Infrastructure.Repositories;
using NexZen.Core.AI;
using NexZen.Infrastructure.AI;
using NexZen.Infrastructure.Services;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Infrastructure.NexShare.Crypto;
using NexZen.Infrastructure.NexShare.Discovery;
using NexZen.Infrastructure.NexShare.Transport;
using NexZen.Application.Services;
using NexZen.Desktop.Services;
using NexZen.Desktop.Views;

namespace NexZen.Desktop;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }

    public override void Initialize()
    {
        StartupTimer.Mark("App XAML load");
        AvaloniaXamlLoader.Load(this);
    }

    private void OnShowMainWindow(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow != null)
            {
                if (desktop.MainWindow is MainWindow mw)
                    mw.ResumeBrowsers();
                desktop.MainWindow.Show();
                if (desktop.MainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                {
                    desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                }
                desktop.MainWindow.Activate();
                if (desktop.MainWindow is MainWindow openedWindow)
                    _ = openedWindow.CheckPasswordVaultSetupReminderAsync();
            }
        }
    }

    private NexShareWindow? _nexShareWindow;
    private CompanionStudioWindow? _companionStudio;
    private AiFunctionWindow? _aiFunctionWindow;

    public void ShowCompanionStudio()
    {
        if (_companionStudio == null)
        {
            _companionStudio = new CompanionStudioWindow();
            _companionStudio.StudioClosed += (_, _) =>
            {
                OnShowNexShareWindow(null, EventArgs.Empty);
                Dispatcher.UIThread.Post(() =>
                {
                    var view = _nexShareWindow?.Content as NexShareView
                        ?? _nexShareWindow?.FindDescendantOfType<NexShareView>();
                    view?.ReloadCompanionLighting();
                    var ai = _aiFunctionWindow?.Content as AiFunctionView
                        ?? _aiFunctionWindow?.FindDescendantOfType<AiFunctionView>();
                    ai?.ReloadCompanionLighting();
                }, DispatcherPriority.Loaded);
            };
        }
        _companionStudio.Show();
        if (_companionStudio.WindowState == Avalonia.Controls.WindowState.Minimized)
            _companionStudio.WindowState = Avalonia.Controls.WindowState.Normal;
        _companionStudio.Activate();
    }

    private void OnShowCompanionStudio(object? sender, EventArgs e) => ShowCompanionStudio();

    private void OnShowNexShareWindow(object? sender, EventArgs e)
    {
        if (_nexShareWindow == null)
        {
            _nexShareWindow = new NexShareWindow();
            var mainVm = ServiceProvider?.GetRequiredService<MainViewModel>();
            if (mainVm != null)
            {
                _nexShareWindow.DataContext = mainVm.NexShare;
            }
        }
        
        _nexShareWindow.Show();
        if (_nexShareWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            _nexShareWindow.WindowState = Avalonia.Controls.WindowState.Normal;
        }
        _nexShareWindow.Activate();
    }

    public void ShowAiFunctionWindow()
    {
        if (_aiFunctionWindow == null)
        {
            _aiFunctionWindow = new AiFunctionWindow();
            var mainVm = ServiceProvider?.GetRequiredService<MainViewModel>();
            if (mainVm != null)
                _aiFunctionWindow.DataContext = mainVm.AiFunction;
        }

        if (_aiFunctionWindow.WindowState == WindowState.Minimized)
            _aiFunctionWindow.WindowState = WindowState.Normal;
        PlaceWidgetInCorner(_aiFunctionWindow);
        _aiFunctionWindow.Show();
        PlaceWidgetInCorner(_aiFunctionWindow);
        _aiFunctionWindow.Activate();
        Dispatcher.UIThread.Post(() =>
        {
            PlaceWidgetInCorner(_aiFunctionWindow);
            var view = _aiFunctionWindow?.Content as AiFunctionView
                ?? _aiFunctionWindow?.FindDescendantOfType<AiFunctionView>();
            view?.ReloadCompanionLighting();
        }, DispatcherPriority.Loaded);
    }

    private NexZen.Desktop.Views.WorkspaceWindow? _workspaceWindow;
    /// <summary>Browser windows opened from a workspace, by workspace id, until they close.</summary>
    private readonly System.Collections.Generic.Dictionary<int, MainWindow> _openWorkspaceWindows = new();

    /// <summary>
    /// Shows the workspace window where AI Function opens. "Add this tab" and "Update"
    /// use the tabs of <paramref name="source"/>, the browser window it was opened from.
    /// </summary>
    public async void ShowWorkspaceWindow(MainViewModel source, Window owner)
    {
        if (ServiceProvider?.GetService<WorkspaceViewModel>() is not { } workspaces)
            return;
        // Resolved at click time: the workspace windows float over whichever browser window
        // the user went back to, and that is the one "this tab" means.
        workspaces.GetActiveTab = () => WorkspaceViewModel.FromOpenTab(MainWindow.LastActiveTabs(source)?.ActiveTab);
        workspaces.OpenWorkspace = request =>
        {
            _workspaceWindow?.Hide();
            // One window per workspace: bring back the one already open instead of a second copy.
            if (_openWorkspaceWindows.TryGetValue(request.WorkspaceId, out var open))
            {
                open.WorkspaceName = request.Name;
                open.WorkspaceColor = request.Color;
                open.ShowTab(request.FocusUrl);
                return;
            }
            var window = NexZen.Desktop.Views.PopupBrowserWindow.OpenMany(owner, request.Urls, request.Name);
            if (window == null)
                return;
            window.WorkspaceColor = request.Color;
            _openWorkspaceWindows[request.WorkspaceId] = window;
            window.Closed += (_, _) =>
            {
                if (_openWorkspaceWindows.TryGetValue(request.WorkspaceId, out var current) && ReferenceEquals(current, window))
                    _openWorkspaceWindows.Remove(request.WorkspaceId);
            };
            if (request.FocusUrl != null)
                window.ShowTab(request.FocusUrl);
        };
        workspaces.OpenEditor = id => ShowWorkspaceEditor(source, owner, id);

        _workspaceWindow ??= new NexZen.Desktop.Views.WorkspaceWindow { DataContext = workspaces };
        if (_workspaceWindow.WindowState == WindowState.Minimized)
            _workspaceWindow.WindowState = WindowState.Normal;
        PlaceWidgetInCorner(_workspaceWindow);
        _workspaceWindow.Show();
        PlaceWidgetInCorner(_workspaceWindow);
        _workspaceWindow.Activate();
        try
        {
            await workspaces.LoadAsync();
        }
        catch (Exception ex)
        {
            workspaces.StatusText = "Could not load workspaces.";
            Program.StartupLog.Write("workspace load failed: " + ex.Message);
        }
    }

    private NexZen.Desktop.Views.WorkspaceEditorWindow? _workspaceEditorWindow;
    private NexZen.Desktop.Views.CustomCssWindow? _customCssWindow;

    /// <summary>Opens the pinned Custom CSS window for a page's site; Save reloads that tab.</summary>
    private NexZen.Desktop.Views.SitePrivacyWindow? _sitePrivacyWindow;

    /// <summary>"Site privacy" for the page; one window, refreshed for the current page.</summary>
    public void ShowSitePrivacyWindow(Window owner, string? pageUrl, byte[]? faviconPng)
    {
        if (ServiceProvider?.GetService<SitePrivacyViewModel>() is not { } privacy)
            return;
        privacy.Load(pageUrl, faviconPng);
        if (_sitePrivacyWindow == null)
        {
            _sitePrivacyWindow = new NexZen.Desktop.Views.SitePrivacyWindow { DataContext = privacy };
            _sitePrivacyWindow.Closed += (_, _) => _sitePrivacyWindow = null;
            privacy.CloseRequested = () => _sitePrivacyWindow?.Close();
            _sitePrivacyWindow.Show(owner);
        }
        _sitePrivacyWindow.Activate();
    }

    public void ShowCustomCssWindow(string pageUrl, Action reloadTab)
    {
        if (ServiceProvider?.GetService<CustomCssViewModel>() is not { } editor || !editor.Load(pageUrl))
            return;
        editor.ReloadPage = reloadTab;
        editor.CloseRequested = () => _customCssWindow?.Hide();
        _customCssWindow ??= new NexZen.Desktop.Views.CustomCssWindow { DataContext = editor };
        PlaceWidgetInCorner(_customCssWindow);
        _customCssWindow.Show();
        PlaceWidgetInCorner(_customCssWindow);
        _customCssWindow.Activate();
    }

    /// <summary>
    /// The print dialog over <paramref name="owner"/>. <paramref name="run"/> gets the chosen
    /// options and the dialog window (for the save picker) and returns an error or null.
    /// </summary>
    public async void ShowPrintDialog(Window owner, bool image, string subject,
        Func<PrintOptions, System.Threading.CancellationToken, System.Threading.Tasks.Task<PrintPreview>> preview,
        string previewFolder,
        Func<PrintOptions, Window, System.Threading.Tasks.Task<string?>> run)
    {
        if (ServiceProvider?.GetService<PrintViewModel>() is not { } print)
            return;
        print.MakePreview = null;
        print.Begin(image, subject);
        var window = new NexZen.Desktop.Views.PrintWindow { DataContext = print };
        print.Run = options => run(options, window);
        print.CloseRequested = () => window.Close();
        print.MakePreview = preview;
        var printers = NexZen.Desktop.Services.PrintService.GetPrintersAsync();
        var shown = window.ShowDialog(owner);
        try
        {
            var (list, defaultPrinter) = await printers;
            print.SetPrinters(list, defaultPrinter);
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write("printer list failed: " + ex.Message);
        }
        // After the printer list, so the first preview already knows the destination.
        print.RefreshPreview();
        await shown;
        print.StopPreview();
        print.MakePreview = null;
        print.Run = null;
        print.CloseRequested = null;
        print.PreviewPages.Clear();
        try { System.IO.Directory.Delete(previewFolder, recursive: true); } catch { }
    }

    /// <summary>
    /// The pinned editor window, for "Add new" (<paramref name="workspaceId"/> null) and for
    /// "Update" (name and tabs of that workspace). A new draft is kept when the window is
    /// closed; after a save the workspace window comes back with the changes.
    /// </summary>
    private async void ShowWorkspaceEditor(MainViewModel source, Window owner, int? workspaceId)
    {
        if (ServiceProvider?.GetService<WorkspaceEditorViewModel>() is not { } editor)
            return;
        if (workspaceId is int id)
        {
            var workspace = await ServiceProvider.GetRequiredService<IWorkspaceRepository>().GetByIdAsync(id);
            if (workspace == null)
                return;
            editor.BeginEdit(workspace);
        }
        else if (editor.EditingId != null)
            editor.Reset();
        editor.GetCurrentTabs = () => (MainWindow.LastActiveTabs(source) ?? source).Tabs
            .Select(WorkspaceViewModel.FromOpenTab)
            .Where(tab => tab != null)
            .Select(tab => tab!)
            .ToList();
        editor.CloseRequested = () => _workspaceEditorWindow?.Hide();
        editor.Saved = _ => Dispatcher.UIThread.Post(() => ShowWorkspaceWindow(source, owner));

        _workspaceEditorWindow ??= new NexZen.Desktop.Views.WorkspaceEditorWindow { DataContext = editor };
        _workspaceWindow?.Hide();
        PlaceWidgetInCorner(_workspaceEditorWindow);
        _workspaceEditorWindow.Show();
        PlaceWidgetInCorner(_workspaceEditorWindow);
        _workspaceEditorWindow.Activate();
    }

    private static void PlaceWidgetInCorner(Window? window)
    {
        if (window == null)
            return;
        var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
        if (screen == null)
            return;
        var workArea = screen.WorkingArea;
        int x = workArea.Right - (int)window.Width - 10;
        int y = workArea.Bottom - (int)window.Height - 10;
        if (OperatingSystem.IsMacOS())
            y = workArea.Y + 10;
        window.Position = new PixelPoint(x, y);
    }

    private NexZen.Desktop.Views.BenchmarkWindow? _benchmarkWindow;

    private void OnShowBenchmarkWindow(object? sender, EventArgs e)
    {
        if (_benchmarkWindow == null || !_benchmarkWindow.IsVisible)
        {
            _benchmarkWindow = new NexZen.Desktop.Views.BenchmarkWindow();
        }
        _benchmarkWindow.Show();
        if (_benchmarkWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            _benchmarkWindow.WindowState = Avalonia.Controls.WindowState.Normal;
        _benchmarkWindow.Activate();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        OnShowNexShareWindow(sender, e);
        
        if (_nexShareWindow != null && _nexShareWindow.Screens.Primary != null)
        {
            var workArea = _nexShareWindow.Screens.Primary.WorkingArea;
            int x = workArea.Right - (int)_nexShareWindow.Width - 10;
            int y = workArea.Bottom - (int)_nexShareWindow.Height - 10;
            
            // Adjust for macOS where menubar is at the top
            if (OperatingSystem.IsMacOS())
            {
                y = workArea.Y + 10;
            }
            
            _nexShareWindow.Position = new PixelPoint(x, y);
        }
    }

    private void OnExitApplication(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mw)
            {
                mw.ForceClose = true;
            }
            if (_nexShareWindow != null)
            {
                _nexShareWindow.ForceClose = true;
            }
            if (_companionStudio != null)
            {
                _companionStudio.ForceClose = true;
            }
            if (_aiFunctionWindow != null)
            {
                _aiFunctionWindow.ForceClose = true;
            }
            desktop.Shutdown();
        }
    }

    private static string ResolveDatabasePath()
    {
        try
        {
            var (path, migrated) = NexZen.Infrastructure.Data.DatabaseLocation.Resolve(
                null, AppContext.BaseDirectory, Environment.CurrentDirectory);
            Program.StartupLog.Write(migrated == null
                ? "database: " + path
                : $"database: {path} (copied from {migrated})");
            return path;
        }
        catch (Exception ex)
        {
            // Fall back to the old behaviour rather than failing to start.
            Program.StartupLog.Write("database location failed, using working directory: " + ex.Message);
            return NexZen.Infrastructure.Data.DatabaseLocation.FileName;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupTimer.Mark("Avalonia framework ready");
        var services = new ServiceCollection();

        // 1. Database: one fixed file, no longer relative to the working directory.
        var database = ResolveDatabasePath();
        services.AddDbContext<NexZenDbContext>(options =>
        {
            options.UseSqlite(NexZen.Infrastructure.Data.DatabaseLocation.ConnectionString(database));
        }, contextLifetime: ServiceLifetime.Transient);
        services.AddTransient<Func<IWorkspaceRepository>>(sp => () => sp.GetRequiredService<IWorkspaceRepository>());

        // 2. Repositories & Services
        services.AddTransient<IBookmarkRepository, BookmarkRepository>();
        services.AddTransient<IHistoryRepository, HistoryRepository>();
        services.AddTransient<IDownloadRepository, DownloadRepository>();
        services.AddTransient<ISavedPasswordRepository, SavedPasswordRepository>();
        services.AddTransient<IWorkspaceRepository, WorkspaceRepository>();
        services.AddSingleton<AesGcmPasswordEncryptionService>();
        services.AddSingleton<IPasswordEncryptionService>(provider => provider.GetRequiredService<AesGcmPasswordEncryptionService>());
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<PasswordVaultMasterPasswordStore>();
        services.AddSingleton<PasswordVaultAuthenticationService>();
        services.AddSingleton<DomainLockStore>();
        services.AddSingleton<IAiToolRuntime, LiveFactsToolRuntime>();
        services.AddSingleton<IWebLookup, PublicWebLookup>();
        services.AddSingleton<IFaviconFetcher, NexZen.Infrastructure.Services.FaviconFetcher>();
        services.AddSingleton<CefResearchBrowser>();
        services.AddSingleton<IBrowserResearch>(sp => sp.GetRequiredService<CefResearchBrowser>());
        services.AddSingleton<IAiChatClient, OpenAiCompatibleChatClient>();
        services.AddSingleton<IAiSecretStore, AiSecretStore>();
        services.AddSingleton<ILocalLlamaServer, LocalLlamaServer>();
        services.AddSingleton<ILocalImageGenerator, LocalJanusImageGenerator>();
        services.AddSingleton<AiFunctionViewModel>();
        services.AddSingleton<WorkspaceViewModel>();
        services.AddSingleton<WorkspaceEditorViewModel>();
        services.AddTransient<ICustomStyleRepository, CustomStyleRepository>();
        services.AddTransient<Func<ICustomStyleRepository>>(sp => () => sp.GetRequiredService<ICustomStyleRepository>());
        services.AddSingleton<NexZen.Application.Services.CustomStyleStore>();
        services.AddSingleton<CustomCssViewModel>();
        services.AddSingleton<PrintViewModel>();
        services.AddSingleton<SitePrivacyViewModel>();

        // 3. NexShare Services
        services.AddSingleton<INexShareCrypto, NexShareCrypto>();
        services.AddSingleton<INexShareDiscovery, LanDiscovery>();
        services.AddSingleton<INexShareTransportServer, LanWebSocketServer>();
        services.AddSingleton<INexShareBleService>(sp =>
        {
            if (OperatingSystem.IsLinux())
                return new NexZen.Infrastructure.NexShare.Ble.BlueZBleService(sp.GetRequiredService<INexShareCrypto>());
            return new NexZen.Infrastructure.NexShare.Ble.NoOpBleService();
        });
        services.AddSingleton<IWifiDirectJoiner>(sp =>
        {
            if (OperatingSystem.IsLinux())
                return new NexZen.Infrastructure.NexShare.WifiDirect.NmLegacyJoin();
            return new NexZen.Infrastructure.NexShare.WifiDirect.NoOpWifiDirectJoiner();
        });
        services.AddSingleton<INexShareOrchestrator, NexShareOrchestrator>();

        // 4. ViewModels (Singleton so event subscriptions aren't duplicated)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<NexShareViewModel>();

        ServiceProvider = services.BuildServiceProvider();
        var nexShareVm = ServiceProvider.GetRequiredService<NexZen.Application.ViewModels.NexShareViewModel>();
        nexShareVm.UiInvoker = action => Avalonia.Threading.Dispatcher.UIThread.Post(action);
        StartupTimer.Mark("services built (NexShare view model resolved)");

        // Create/upgrade the database on a worker thread while the window and CEF start.
        // Any DbContext created meanwhile waits for it (DatabaseStartup.WaitUntilReady).
        var databaseReady = NexZen.Infrastructure.Data.DatabaseStartup.Begin(() =>
        {
            // Own scope: the root DbContext is shared by repositories on the UI thread.
            using var scope = ServiceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NexZenDbContext>();
            dbContext.Database.EnsureCreated();
            try
            {
                dbContext.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS ""DownloadRecords"" (
                        ""Id"" TEXT NOT NULL CONSTRAINT ""PK_DownloadRecords"" PRIMARY KEY,
                        ""FileName"" TEXT NOT NULL,
                        ""Url"" TEXT NOT NULL,
                        ""FilePath"" TEXT NOT NULL,
                        ""TotalBytes"" INTEGER NOT NULL,
                        ""DownloadedBytes"" INTEGER NOT NULL,
                        ""State"" TEXT NOT NULL,
                        ""Timestamp"" TEXT NOT NULL
                    );");
                dbContext.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS ""SavedPasswords"" (
                        ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_SavedPasswords"" PRIMARY KEY AUTOINCREMENT,
                        ""Origin"" TEXT NOT NULL,
                        ""Username"" TEXT NOT NULL,
                        ""EncryptedPassword"" TEXT NOT NULL,
                        ""CreatedAt"" TEXT NOT NULL,
                        ""UpdatedAt"" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_SavedPasswords_Origin_Username""
                    ON ""SavedPasswords"" (""Origin"", ""Username"");");
            }
            catch { }
            NexZen.Infrastructure.Data.WorkspaceSchema.Ensure(dbContext);
            NexZen.Infrastructure.Data.CustomStyleSchema.Ensure(dbContext);
            StartupTimer.Mark("database ready (background)");
        }, ex => Program.StartupLog.Write("database setup failed: " + ex.Message));
        // Custom CSS is read after the tables exist; the first page starts loading well after that.
        _ = databaseReady.ContinueWith(_ =>
        {
            try
            {
                ServiceProvider.GetRequiredService<NexZen.Application.Services.CustomStyleStore>().LoadAsync().GetAwaiter().GetResult();
                StartupTimer.Mark("custom css loaded (background)");
            }
            catch (Exception ex)
            {
                Program.StartupLog.Write("custom css load failed: " + ex.Message);
            }
        }, System.Threading.Tasks.TaskScheduler.Default);

        var orchestrator = ServiceProvider.GetRequiredService<INexShareOrchestrator>();
        StartupTimer.Mark("NexShare orchestrator resolved");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Unregister the BLE advertisement / GATT app (BlueZ also drops them when our D-Bus connection closes).
            desktop.Exit += (_, _) =>
            {
                try { System.Threading.Tasks.Task.Run(() => orchestrator.StopAsync()).Wait(3000); } catch { }
            };
            StartupTimer.Mark("MainWindow create");
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            StartupTimer.Mark("MainWindow created");
            // NexShare's window/tray hooks are not needed to show the browser: attach them last.
            void AttachNexShareOnce(object? sender, EventArgs e)
            {
                mainWindow.Opened -= AttachNexShareOnce;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => AttachNexShare(nexShareVm, orchestrator),
                    Avalonia.Threading.DispatcherPriority.Background);
            }
            mainWindow.Opened += AttachNexShareOnce;
        }

        base.OnFrameworkInitializationCompleted();
#if DEBUG
        // F12 belongs to the page DevTools; keep the Avalonia inspector on Ctrl+Alt+F12.
        this.AttachDeveloperTools(options =>
            options.Gesture = new Avalonia.Input.KeyGesture(
                Avalonia.Input.Key.F12, Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Alt));
#endif
    }

    private void AttachNexShare(NexShareViewModel nexShareVm, INexShareOrchestrator orchestrator)
    {
        // Update Tray Icon when NexShare status changes
        orchestrator.OnConnectionStatusChanged += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var trayIcons = TrayIcon.GetIcons(this);
                if (trayIcons != null && trayIcons.Count > 0)
                {
                    trayIcons[0].ToolTipText = "NexShare: " + e;
                }
            });
        };

        nexShareVm.OnSasShowRequested += (s, e) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnShowNexShareWindow(null, EventArgs.Empty));
        nexShareVm.OnPickerShowRequested += (s, e) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OnShowNexShareWindow(null, EventArgs.Empty));

        orchestrator.OnFileOfferReceived += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnShowNexShareWindow(null, EventArgs.Empty);
            });
        };

        orchestrator.OnTextMessageReceived += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OnShowNexShareWindow(null, EventArgs.Empty);
            });
        };

        orchestrator.OnUrlOfferReceived += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is NexZen.Desktop.MainWindow mw)
                {
                    var from = string.IsNullOrWhiteSpace(e.DeviceName) ? "another device" : e.DeviceName;
                    mw.ShowNotification("NexShare", $"Opened a link from {from}");
                }
            });
        };
        StartupTimer.Mark("NexShare hooks attached");
    }
}
