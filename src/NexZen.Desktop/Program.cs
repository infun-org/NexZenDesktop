using Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NexZen.Desktop.Services;
using Xilium.CefGlue;

namespace NexZen.Desktop;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupLog.Open();
        StartupTimer.Mark("Main");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            StartupLog.Fail(e.ExceptionObject as Exception
                ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        };

        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            StartupLog.Fail(ex);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        // Phase 5: Log IME config (kill-switch + trace) at startup for diagnosability
        Console.WriteLine($"[Info][Program.cs] NEXZEN_IME_TRACE={Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE")} NEXZEN_IME_DISABLED={Environment.GetEnvironmentVariable("NEXZEN_IME_DISABLED")}");
        StartupLog.Write("entered Run");

        // Default to X11 (via XWayland) on Linux: Avalonia's Wayland backend
        // lacks reliable IME (Vietnamese input), clipboard and window icons.
        // Opt in to native Wayland (e.g. for Dolphin drag-and-drop) via NEXZEN_FORCE_WAYLAND=1.
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Linux))
        {
            var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            var useWayland = UseNativeWayland();
            Console.WriteLine($"[CEF-DND] Linux session={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown"} WAYLAND_DISPLAY={waylandDisplay ?? "<unset>"} DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "<unset>"}; selecting {(useWayland ? "native Wayland" : "X11")}");

            if (!useWayland)
            {
                Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
                Environment.SetEnvironmentVariable("XMODIFIERS", "@im=fcitx");
                Environment.SetEnvironmentVariable("GTK_IM_MODULE", "fcitx");
                Environment.SetEnvironmentVariable("QT_IM_MODULE", "fcitx");
            }
        }
        
        // Trick Chromium into thinking we have Google API keys so it doesn't auto-disable Geolocation/Location
        Environment.SetEnvironmentVariable("GOOGLE_API_KEY", "dummy_api_key");
        Environment.SetEnvironmentVariable("GOOGLE_DEFAULT_CLIENT_ID", "dummy_client_id");
        Environment.SetEnvironmentVariable("GOOGLE_DEFAULT_CLIENT_SECRET", "dummy_client_secret");
        
        StartupLog.Write("CefRuntime.Load");
        CefRuntime.Load();
        StartupLog.Write("CefRuntime.Load done");
        StartupTimer.Mark("CEF loaded");
        
        var argsWithExe = new string[args.Length + 1];
        argsWithExe[0] = "NexZen.Desktop";
        Array.Copy(args, 0, argsWithExe, 1, args.Length);
        var cefArgs = new CefMainArgs(argsWithExe);

        StartupLog.Write("CefRuntime.ExecuteProcess");
        var exitCode = CefRuntime.ExecuteProcess(cefArgs, null, IntPtr.Zero);
        StartupLog.Write("ExecuteProcess exit=" + exitCode);
        if (exitCode != -1)
        {
            // A real CEF subprocess is launched with --type= and should stay quiet.
            // The main process must get -1; anything else means startup stopped here.
            if (Environment.CommandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                StartupLog.Fail(new InvalidOperationException(
                    "CEF ExecuteProcess returned " + exitCode + " for the main process. Expected -1."));
            }
            return exitCode;
        }

        try
        {
            StartupLog.Write("pending data clear");
            var pendingResult = NexZen.Infrastructure.Services.PendingProfileCleaner.Apply();
            StartupLog.Write("pending data clear: " + pendingResult);
        }
        catch (Exception ex)
        {
            StartupLog.Write("pending data clear failed: " + ex);
        }

        try
        {
            StartupLog.Write("load extensions");
            var extensionCatalog = new ExtensionCatalog();
            var extensions = extensionCatalog.Load();
            var extensionPaths = new List<string>();
            foreach (var ext in extensions)
            {
                if (ext.IsRunning && !string.IsNullOrEmpty(ext.Directory) && System.IO.Directory.Exists(ext.Directory))
                {
                    TrimExtensionRulesets(ext.Directory);
                    extensionPaths.Add(ext.Directory);
                }
            }
            // Removed hardcoded test extension to avoid duplicate conflicts

            var cefSettingsArgs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("disable-features", "UserAgentClientHint,CompressionDictionaryTransport,CompressionDictionaryTransportBacking,AudioServiceOutOfProcess"),
                new KeyValuePair<string, string>("autoplay-policy", "no-user-gesture-required"),
            };

            if (extensionPaths.Count > 0)
            {
                cefSettingsArgs.Add(new KeyValuePair<string, string>("load-extension", string.Join(",", extensionPaths)));
            }

            var logFile = Path.Combine(AppContext.BaseDirectory, "cef.log");
            StartupLog.Write("CefRuntimeLoader.Initialize log=" + logFile);
            var cefSettings = new CefSettings
            {
                WindowlessRenderingEnabled = true,
                LogSeverity = CefLogSeverity.Info,
                LogFile = logFile,
                UserAgent = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"
                    : "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36"
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // CEF resolves Linux resource bundles beside the main executable.
                // The post-build script recreates these links after dotnet clean.
                var cefResourcesDir = AppContext.BaseDirectory;
                var cefLocalesDir = Path.Combine(cefResourcesDir, "locales");
                var cefRootCache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NexZenDesktop", "CefRootCache");
                Directory.CreateDirectory(cefRootCache);
                cefSettings.ResourcesDirPath = cefResourcesDir;
                cefSettings.LocalesDirPath = cefLocalesDir;
                cefSettings.Locale = "en-US";
                cefSettings.RootCachePath = cefRootCache;
                StartupLog.Write($"CEF resources={cefResourcesDir} locales={cefLocalesDir} rootCache={cefRootCache}");
            }

            var newTabRoot = Path.Combine(AppContext.BaseDirectory, "NewTab");
            var customSchemes = new[]
            {
                new Xilium.CefGlue.Common.Shared.CustomScheme
                {
                    SchemeName = NexZen.Desktop.Handlers.NewTabSchemeHandlerFactory.SchemeName,
                    DomainName = NexZen.Desktop.Handlers.NewTabSchemeHandlerFactory.Host,
                    IsCSPBypassing = true,
                    SchemeHandlerFactory = new NexZen.Desktop.Handlers.NewTabSchemeHandlerFactory(newTabRoot)
                }
            };
            StartupTimer.Mark("CEF initialize");
            Xilium.CefGlue.Common.CefRuntimeLoader.Initialize(cefSettings, cefSettingsArgs.ToArray(), customSchemes);
            // CefRuntime.Initialize (~1 s) and EF Core's first context (~0.4 s: internal services,
            // model, query pipeline) run on background threads while Avalonia and the XAML load,
            // instead of on the UI thread when the first tab / the view model needs them.
            if (Environment.GetEnvironmentVariable("NEXZEN_CEF_SYNC_INIT") != "1")
                Xilium.CefGlue.Common.CefRuntimeLoader.LoadInBackground();
            NexZen.Infrastructure.Data.DatabaseWarmUp.Start();
            StartupLog.Write("CEF initialized, starting Avalonia");
            StartupTimer.Mark("Avalonia start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            StartupLog.Write("Avalonia exited");
            return 0;
        }
        finally
        {
        }
    }

    internal static class StartupLog
    {
        static string _path = "";
        static readonly object WriteGate = new();

        public static void Open()
        {
            _path = Path.Combine(AppContext.BaseDirectory, "startup.log");
            try
            {
                File.WriteAllText(_path, "");
            }
            catch
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NexZenDesktop");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "startup.log");
                try { File.WriteAllText(_path, ""); } catch { _path = ""; }
            }

            Write("process start");
            Write("base=" + AppContext.BaseDirectory);
            Write("os=" + RuntimeInformation.OSDescription);
            Write("cmd=" + Environment.CommandLine);
        }

        public static void Write(string message)
        {
            if (message.StartsWith("[PasswordCapture]", StringComparison.Ordinal)
                || message.StartsWith("[PasswordVaultAuth]", StringComparison.Ordinal)
                || message.StartsWith("[PasswordVaultPIN]", StringComparison.Ordinal)
                || message.StartsWith("[DomainLock]", StringComparison.Ordinal)
                || message.StartsWith("[MainWindowClose]", StringComparison.Ordinal)
                || message.StartsWith("[BrowserLifecycle]", StringComparison.Ordinal)
                || message.StartsWith("[NewWindow]", StringComparison.Ordinal))
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                // Startup also logs from the background database setup.
                lock (WriteGate)
                    File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine);
            }
            catch { }
        }

        public static void Fail(Exception ex)
        {
            Console.Error.WriteLine($"[NexZenUnhandled] {ex}");
            Write(ex.ToString());
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            try
            {
                MessageBoxW(IntPtr.Zero, ex + Environment.NewLine + Environment.NewLine + "Log: " + _path,
                    "NexZen failed to start", 0x10);
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }

    // Disabled ad-block rulesets would be re-indexed by Chromium on every start (~6 s for
    // Adblock Plus). NEXZEN_EXT_ALL_RULESETS=1 puts the full manifests back.
    static void TrimExtensionRulesets(string directory)
    {
        try
        {
            var restore = Environment.GetEnvironmentVariable("NEXZEN_EXT_ALL_RULESETS") == "1";
            var result = NexZen.Infrastructure.Services.ExtensionRulesetTrimmer.Apply(directory, restore);
            if (result != NexZen.Infrastructure.Services.ExtensionRulesetTrimmer.Result.Unchanged)
                StartupLog.Write($"extension rulesets {result.ToString().ToLowerInvariant()}: {directory}");
        }
        catch (Exception ex)
        {
            // A broken manifest must not stop the browser; load the extension as it is.
            StartupLog.Write($"extension ruleset trim failed for {directory}: {ex.Message}");
        }
    }

    // Native Wayland is opt-in only. NEXZEN_FORCE_X11=1 still forces X11.
    static bool UseNativeWayland() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && string.Equals(Environment.GetEnvironmentVariable("NEXZEN_FORCE_WAYLAND"), "1", StringComparison.Ordinal)
        && !string.Equals(Environment.GetEnvironmentVariable("NEXZEN_FORCE_X11"), "1", StringComparison.Ordinal);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && UseNativeWayland())
        {
            return builder.UseSkia()
                .UseHarfBuzz()
                .UseWayland()
                .WithInterFont()
                .LogToTrace();
        }

        // WmClass ties the window to nexzen-desktop.desktop (StartupWMClass=NexZen) in the
        // taskbar and app menu; the default is the executable name, "NexZen.Desktop".
        return builder.UsePlatformDetect()
            .With(new X11PlatformOptions
            {
                EnableIme = true,
                WmClass = "NexZen",
                // GPU page painting imports CEF's dmabuf frames with EGL, so Avalonia must render
                // through EGL rather than GLX (falls back to GLX/software if EGL is unavailable).
                RenderingMode = Xilium.CefGlue.Common.GpuPaint.Enabled
                    ? new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software }
                    : new[] { X11RenderingMode.Glx, X11RenderingMode.Software }
            })
            .WithInterFont()
            .LogToTrace();
    }
}
