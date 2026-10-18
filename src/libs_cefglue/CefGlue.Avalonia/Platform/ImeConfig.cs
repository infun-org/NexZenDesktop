using System;

namespace Xilium.CefGlue.Avalonia.Platform
{
    /// <summary>
    /// Central configuration for the Linux IME bridge.
    /// 
    /// Environment variables (set before app launch):
    ///   NEXZEN_IME_DISABLED=1  → Kill-switch: disables the entire IME bridge.
    ///                            CEF will receive raw key events as if no IME is installed.
    ///   NEXZEN_IME_TRACE=1     → Enables verbose trace logging to stdout.
    ///                            Works in both Debug and Release builds.
    /// </summary>
    internal static class ImeConfig
    {
        /// <summary>Kill-switch: disables the entire IME bridge when set to "1".</summary>
        public static readonly bool IsDisabled =
            Environment.GetEnvironmentVariable("NEXZEN_IME_DISABLED") == "1";

        /// <summary>
        /// Verbose trace logging flag. Reads at startup so it is zero-cost at runtime
        /// when disabled (no repeated env-var lookups in the hot path).
        /// </summary>
        public static readonly bool IsTraceEnabled =
            Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE") == "1";

        public static void LogStartup()
        {
            if (!IsTraceEnabled) return;
            Console.WriteLine($"[Trace][ImeConfig] IsDisabled={IsDisabled}, IsTraceEnabled={IsTraceEnabled}");
        }
    }
}
