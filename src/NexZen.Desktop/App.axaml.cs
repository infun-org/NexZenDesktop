using System;
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

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        // 1. Database
        services.AddDbContext<NexZenDbContext>(options =>
        {
            options.UseSqlite("Data Source=nexzen.db");
        }, contextLifetime: ServiceLifetime.Transient);

        // 2. Repositories & Services
        services.AddTransient<IBookmarkRepository, BookmarkRepository>();
        services.AddTransient<IHistoryRepository, HistoryRepository>();
        services.AddTransient<IDownloadRepository, DownloadRepository>();
        services.AddTransient<ISavedPasswordRepository, SavedPasswordRepository>();
        services.AddSingleton<AesGcmPasswordEncryptionService>();
        services.AddSingleton<IPasswordEncryptionService>(provider => provider.GetRequiredService<AesGcmPasswordEncryptionService>());
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<PasswordVaultMasterPasswordStore>();
        services.AddSingleton<PasswordVaultAuthenticationService>();
        services.AddSingleton<DomainLockStore>();
        services.AddSingleton<IAiToolRuntime, LiveFactsToolRuntime>();
        services.AddSingleton<IWebLookup, PublicWebLookup>();
        services.AddSingleton<CefResearchBrowser>();
        services.AddSingleton<IBrowserResearch>(sp => sp.GetRequiredService<CefResearchBrowser>());
        services.AddSingleton<IAiChatClient, OpenAiCompatibleChatClient>();
        services.AddSingleton<IAiSecretStore, AiSecretStore>();
        services.AddSingleton<ILocalLlamaServer, LocalLlamaServer>();
        services.AddSingleton<ILocalImageGenerator, LocalJanusImageGenerator>();
        services.AddSingleton<AiFunctionViewModel>();

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

        // Ensure database is created (Migration)
        var dbContext = ServiceProvider.GetRequiredService<NexZenDbContext>();
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

        // Update Tray Icon when NexShare status changes
        var orchestrator = ServiceProvider.GetRequiredService<INexShareOrchestrator>();
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

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Unregister the BLE advertisement / GATT app (BlueZ also drops them when our D-Bus connection closes).
            desktop.Exit += (_, _) =>
            {
                try { System.Threading.Tasks.Task.Run(() => orchestrator.StopAsync()).Wait(3000); } catch { }
            };
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }
}
