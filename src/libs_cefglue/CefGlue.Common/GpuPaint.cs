using System;
using System.IO;

namespace Xilium.CefGlue.Common
{
    /// <summary>
    /// GPU path for off-screen browsers on Linux: CEF shares each frame as a dmabuf texture instead
    /// of copying it back to system memory, which limits full-window WebGL pages on integrated GPUs.
    /// On by default; NEXZEN_GPU_PAINT=0 turns it off. If the renderer turns out not to support it
    /// (no EGL), it is switched off and remembered, so later starts use the copying path.
    /// </summary>
    public static class GpuPaint
    {
        /// <summary>
        /// Frame rate CEF paints at while frames are shared: at the display rate the compositor
        /// falls far behind for a full-screen window (~6 fps drawn), at 30 it keeps up.
        /// </summary>
        public const int FrameRate = 30;

        private static readonly string FailureMarker = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NexZenDesktop", "gpu-paint-unsupported");

        public static bool Enabled { get; private set; } = Decide();

        private static bool Decide()
        {
            if (!OperatingSystem.IsLinux())
                return false;
            var env = Environment.GetEnvironmentVariable("NEXZEN_GPU_PAINT");
            if (env == "0")
                return false;
            if (env == "1")
                return true; // forced, even after a remembered failure
            try
            {
                return !File.Exists(FailureMarker);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Switches GPU painting off for browsers created from now on and remembers it for later starts.
        /// </summary>
        public static void Disable(string reason)
        {
            if (!Enabled)
                return;
            Enabled = false;
            Console.WriteLine($"[GpuPaint] off: {reason}");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FailureMarker)!);
                File.WriteAllText(FailureMarker, reason + Environment.NewLine);
            }
            catch
            {
                // Not remembered; the check runs again at the next start.
            }
        }
    }
}
