using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.Shared;

namespace Xilium.CefGlue.Common
{
    public static class CefRuntimeLoader
    {
        private const string DefaultBrowserProcessDirectory = "CefGlueBrowserProcess";

        private static Action<BrowserProcessHandler> _delayedInitialization;

        public static void Initialize(CefSettings settings = null, KeyValuePair<string, string>[] flags = null, CustomScheme[] customSchemes = null)
        {
            _delayedInitialization = (browserProcessHandler) => InternalInitialize(settings, flags, customSchemes, browserProcessHandler);
        }

        private static void InternalInitialize(CefSettings settings = null, KeyValuePair<string, string>[] flags = null, CustomScheme[] customSchemes = null, BrowserProcessHandler browserProcessHandler = null)
        {
            CefRuntime.Load();

            if (settings == null)
            {
                settings = new CefSettings();
            }

            settings.UncaughtExceptionStackSize = 100; // for uncaught exception event work properly

            var basePath = AppContext.BaseDirectory;
            var probingPaths = GetSubProcessPaths(basePath);
            var subProcessPath = probingPaths.FirstOrDefault(p => File.Exists(p));
            if (subProcessPath == null)
                throw new FileNotFoundException($"Unable to find SubProcess. Probed locations: {string.Join(Environment.NewLine, probingPaths)}");

            settings.BrowserSubprocessPath = subProcessPath;

            switch (CefRuntime.Platform)
            {
                case CefRuntimePlatform.Windows:
                    settings.MultiThreadedMessageLoop = true;
                    break;

                case CefRuntimePlatform.MacOS:
                    var resourcesPath = Path.Combine(basePath, "Resources");
                    if (!Directory.Exists(resourcesPath))
                    {
                        throw new FileNotFoundException($"Unable to find Resources folder");
                    }

                    settings.NoSandbox = true;
                    settings.MultiThreadedMessageLoop = false;
                    settings.ExternalMessagePump = true;
                    settings.MainBundlePath = basePath;
                    settings.FrameworkDirPath = basePath;
                    settings.ResourcesDirPath = resourcesPath;
                    break;
                
                case CefRuntimePlatform.Linux:
                    settings.NoSandbox = true;
                    settings.MultiThreadedMessageLoop = true;
                    break;
            }

            AppDomain.CurrentDomain.ProcessExit += delegate { CefRuntime.Shutdown(); };

            IsOSREnabled = settings.WindowlessRenderingEnabled;

            // On Linux, with osr disable, the filename in CefMainArgs will be used as accessible name.
            // If the name is empty, chromium will crash at ui::AXNodeData:SetNamechecked.
            var exeFileName = Process.GetCurrentProcess().MainModule.FileName;
            if (string.IsNullOrEmpty(exeFileName))
            {
                exeFileName = "CefGlue";
            }
            
            try
            {
                CefRuntime.Initialize(new CefMainArgs(new[] { exeFileName }.Concat(LaunchSwitches ?? Array.Empty<string>()).ToArray()), settings, new BrowserCefApp(customSchemes, flags, browserProcessHandler), IntPtr.Zero);
            }
            catch (InvalidOperationException) when (CefRuntime.GetExitCode() == 24 /* CEF_RESULT_CODE_NORMAL_EXIT_PROCESS_NOTIFIED */)
            {
                ForwardedToRunningInstance = true;
                throw;
            }

            if (customSchemes != null)
            {
                foreach (var scheme in customSchemes)
                {
                    CefRuntime.RegisterSchemeHandlerFactory(scheme.SchemeName, scheme.DomainName, scheme.SchemeHandlerFactory);
                }
            }
        }

        private static IEnumerable<string> GetSubProcessPaths(string baseDirectory)
        {
            yield return Path.Combine(baseDirectory, DefaultBrowserProcessDirectory, BrowserProcessFileName);
            yield return Path.Combine(baseDirectory, BrowserProcessFileName);

            // The executing DLL might not be in the current domain directory (plugins scenario)
            baseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            yield return Path.Combine(baseDirectory, DefaultBrowserProcessDirectory, BrowserProcessFileName);
            yield return Path.Combine(baseDirectory, BrowserProcessFileName);
        }

        private static System.Threading.Tasks.Task _backgroundLoad;

        /// <summary>
        /// The app was started again while running (same root cache path): CEF hands the new launch's
        /// command line (argv, program first) to this process. Return true when handled; without a handler CEF opens a plain
        /// Chrome-style window. Called on CEF's UI thread.
        /// </summary>
        public static Func<string[], bool> AppRelaunched { get; set; }

        /// <summary>
        /// Extra switches for this launch's CEF command line. A second launch forwards its command line
        /// to the running instance, so this is how the app's own arguments reach <see cref="AppRelaunched"/>.
        /// Use switches CEF does not know (they are ignored by Chromium).
        /// </summary>
        public static string[] LaunchSwitches { get; set; } = Array.Empty<string>();

        /// <summary>True when initialization failed because the launch was passed to a running instance.</summary>
        public static bool ForwardedToRunningInstance { get; private set; }

        /// <summary>The background initialization (null if it was not started); faults if CEF failed.</summary>
        public static System.Threading.Tasks.Task BackgroundLoad => _backgroundLoad;

        /// <summary>
        /// Starts CefRuntime.Initialize (≈1 s) on a background thread right away, so it overlaps the
        /// UI start-up instead of blocking the first browser. With the multi-threaded message loop CEF
        /// runs its UI on its own thread, so the initializing thread does not matter.
        /// </summary>
        public static void LoadInBackground()
        {
            var init = _delayedInitialization;
            if (init == null || _backgroundLoad != null) return;
            _delayedInitialization = null;
            _backgroundLoad = System.Threading.Tasks.Task.Factory.StartNew(() => init(null),
                System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.LongRunning,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        internal static void Load(BrowserProcessHandler browserProcessHandler = null)
        {
            if (_backgroundLoad != null)
            {
                _backgroundLoad.GetAwaiter().GetResult();
                return;
            }
            if (_delayedInitialization != null)
            {
                _delayedInitialization.Invoke(browserProcessHandler);
                _delayedInitialization = null;
            }
            else
            {
                InternalInitialize(browserProcessHandler: browserProcessHandler);
            }
        }

        public static bool IsLoaded => CefRuntime.IsInitialized;

        internal static bool IsOSREnabled { get; private set; }

        private static string BrowserProcessFileName
        {
            get
            {
                const string Filename = "Xilium.CefGlue.BrowserProcess";
                switch (CefRuntime.Platform)
                {
                    case CefRuntimePlatform.Windows:
                        return Filename + ".exe";
                    default:
                        return Filename;
                }
            }
        }
    }
}
