using System;
using System.Collections.Generic;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.InternalHandlers;
using Xilium.CefGlue.Common.Shared;

namespace Xilium.CefGlue.Common
{
    internal class BrowserCefApp : CommonCefApp
    {
        private readonly CefBrowserProcessHandler _browserProcessHandler;
        private readonly KeyValuePair<string, string>[] _flags;

        internal BrowserCefApp(CustomScheme[] customSchemes = null, KeyValuePair<string, string>[] flags = null, BrowserProcessHandler browserProcessHandler = null) :
            base(customSchemes)
        {
            _browserProcessHandler = new CommonBrowserProcessHandler(browserProcessHandler, customSchemes);
            _flags = flags;
        }

        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            if (string.IsNullOrEmpty(processType))
            {
                if (CefRuntime.Platform == CefRuntimePlatform.Linux)
                {
                    if (!commandLine.HasSwitch("no-zygote"))
                        commandLine.AppendSwitch("no-zygote");
                    // Wayland blocks clipboard for OSR (windowless) surfaces.
                    if (!commandLine.HasSwitch("ozone-platform"))
                        commandLine.AppendSwitch("ozone-platform", "x11");
                }

                if (_flags != null)
                {
                    foreach (var flag in _flags)
                    {
                        if (flag.Key == "disable-features")
                            MergeCommaSwitch(commandLine, "disable-features", flag.Value);
                        else if (!commandLine.HasSwitch(flag.Key))
                            commandLine.AppendSwitch(flag.Key, flag.Value);
                    }
                }

                CollapseDuplicateDisableFeatures(commandLine);
            }
        }

        private static void MergeCommaSwitch(CefCommandLine commandLine, string name, string extra)
        {
            var parts = new List<string>();
            void Add(string source)
            {
                if (string.IsNullOrWhiteSpace(source))
                    return;
                foreach (var item in source.Split(','))
                {
                    var token = item.Trim();
                    if (token.Length == 0)
                        continue;
                    if (!parts.Exists(p => string.Equals(p, token, StringComparison.Ordinal)))
                        parts.Add(token);
                }
            }

            if (commandLine.HasSwitch(name))
                Add(commandLine.GetSwitchValue(name));
            Add(extra);
            commandLine.AppendSwitch(name, string.Join(",", parts));
        }

        private static void CollapseDuplicateDisableFeatures(CefCommandLine commandLine)
        {
            if (CefRuntime.Platform == CefRuntimePlatform.Windows)
                return;

            var argv = commandLine.GetArgv();
            if (argv == null || argv.Length == 0)
                return;

            string lastDisable = null;
            var rebuilt = new List<string>(argv.Length);
            for (var i = 0; i < argv.Length; i++)
            {
                var token = argv[i];
                if (token.StartsWith("--disable-features=", StringComparison.Ordinal)
                    || string.Equals(token, "--disable-features", StringComparison.Ordinal))
                {
                    lastDisable = token.StartsWith("--disable-features=", StringComparison.Ordinal)
                        ? token
                        : (i + 1 < argv.Length ? "--disable-features=" + argv[++i] : token);
                    continue;
                }

                if (string.Equals(token, "--flag-switches-begin", StringComparison.Ordinal)
                    || string.Equals(token, "--flag-switches-end", StringComparison.Ordinal))
                    continue;

                rebuilt.Add(token);
            }

            if (lastDisable != null)
                rebuilt.Insert(Math.Min(1, rebuilt.Count), lastDisable);

            if (rebuilt.Count == argv.Length)
            {
                var same = true;
                for (var i = 0; i < argv.Length; i++)
                {
                    if (!string.Equals(argv[i], rebuilt[i], StringComparison.Ordinal))
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                    return;
            }

            commandLine.InitFromArgv(rebuilt.ToArray());
        }

        protected override CefBrowserProcessHandler GetBrowserProcessHandler()
        {
            return _browserProcessHandler;
        }
    }
}
