using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NexZen.Desktop.Mirror;

/// <summary>
/// USB mode for NexZen Mirror: when a phone is attached with USB debugging, "adb forward" opens a
/// TCP tunnel over the cable to the phone's NexShare server, and the normal NexShare connection
/// (and mirroring, with its code check and encryption) runs through it.
/// </summary>
internal static class AdbTunnel
{
    private const int PhonePortMin = 43000, PhonePortMax = 43100; // NexShareConstants.WS_PORT_MIN/MAX

    public static string? FindAdb()
    {
        var name = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } sdk) dirs.Add(Path.Combine(sdk, "platform-tools"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, "Android", "Sdk", "platform-tools"));
        dirs.Add("/opt/android-sdk/platform-tools");
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools"));
        return dirs.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    private static async Task<string?> RunAsync(string adb, string args, int timeoutMs = 5000)
    {
        try
        {
            var psi = new ProcessStartInfo(adb, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            // Wait for the output to end, not for the exit: inside NexZen, CEF/Chromium owns SIGCHLD,
            // so .NET may never see adb exit (WaitForExit timed out and USB was never found).
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            try
            {
                return await output.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException)
            {
                try { p.Kill(); } catch { }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds an attached, authorized phone and its NexShare port, and forwards a local port to it.
    /// Null when there is no adb, no phone in "device" state, or NexShare is not listening.
    /// </summary>
    public static async Task<(string Serial, int LocalPort)?> OpenAsync()
    {
        var adb = FindAdb();
        if (adb == null) return null;
        var devices = await RunAsync(adb, "devices");
        var serial = devices?.Split('\n').Skip(1)
            .Select(l => l.Trim().Split('\t'))
            .FirstOrDefault(parts => parts.Length == 2 && parts[1] == "device")?[0];
        if (serial == null) return null;

        // The phone's NexShare server listens on a port in 43000–43100 (all interfaces).
        var tcp = await RunAsync(adb, $"-s {serial} shell cat /proc/net/tcp6 /proc/net/tcp");
        var port = tcp?.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length > 3 && f[3] == "0A" && f[1].Contains(':'))
            .Select(f => Convert.ToInt32(f[1][(f[1].LastIndexOf(':') + 1)..], 16))
            .Where(p => p is >= PhonePortMin and <= PhonePortMax)
            .OrderBy(p => p)
            .FirstOrDefault() ?? 0;
        if (port == 0) return null;

        var forwarded = await RunAsync(adb, $"-s {serial} forward tcp:0 tcp:{port}");
        return int.TryParse(forwarded?.Trim(), out var local) && local > 0 ? (serial, local) : null;
    }
}
