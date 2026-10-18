using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace NexZen.Application.Services;

public static class NexShareFirewall
{
    public const int TcpPort = 43000;
    public const int UdpPort = 42999;
    /// <summary>Wi-Fi Direct TCP link (Android WifiDirectTransport). Opened for future PC-as-GO / inbound;
    /// not required by <see cref="ArePortsOpenAsync"/> because the PC currently connects out.</summary>
    public const int WfdTcpPort = 25556;

    public static string LinuxAllowScript =>
        "if command -v ufw >/dev/null 2>&1; then ufw allow " + TcpPort + "/tcp && ufw allow " + UdpPort + "/udp && ufw allow " + WfdTcpPort + "/tcp; "
        + "elif command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --add-port=" + TcpPort + "/tcp --permanent && firewall-cmd --add-port=" + UdpPort + "/udp --permanent && firewall-cmd --add-port=" + WfdTcpPort + "/tcp --permanent && firewall-cmd --reload; fi";

    public static string WindowsAllowArgs =>
        "/c netsh advfirewall firewall add rule name=\"NexShare\" dir=in action=allow protocol=TCP localport=" + TcpPort
        + " & netsh advfirewall firewall add rule name=\"NexShare Discovery\" dir=in action=allow protocol=UDP localport=" + UdpPort
        + " & netsh advfirewall firewall add rule name=\"NexShare Wi-Fi Direct\" dir=in action=allow protocol=TCP localport=" + WfdTcpPort;
    public const string PortsClosedMessage = "Ports are closed. Press Fix Firewall in the NexShare widget.";

    /// <summary>
    /// True when nothing is filtering the NexShare ports, or the active firewall already allows them.
    /// A failed probe does not report the ports as closed.
    /// </summary>
    public static async Task<bool> ArePortsOpenAsync()
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return await LinuxPortsOpenAsync();
            if (OperatingSystem.IsWindows())
                return await WindowsPortsOpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexShare] Firewall probe failed: {ex.Message}");
            return true;
        }
    }

    internal static bool UfwRulesAllow(string rules, int tcpPort, int udpPort)
    {
        var tcp = false;
        var udp = false;
        foreach (var raw in rules.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("### tuple ### allow ", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // ### tuple ### allow <proto> <port> ... in
            if (parts.Length < 6 || !string.Equals(parts[^1], "in", StringComparison.Ordinal))
                continue;
            if (!string.Equals(parts[3], "allow", StringComparison.Ordinal))
                continue;
            if (string.Equals(parts[4], "tcp", StringComparison.Ordinal) && PortSpecIncludes(parts[5], tcpPort))
                tcp = true;
            else if (string.Equals(parts[4], "udp", StringComparison.Ordinal) && PortSpecIncludes(parts[5], udpPort))
                udp = true;
        }
        return tcp && udp;
    }

    private static bool PortSpecIncludes(string spec, int port)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            var colon = token.IndexOf(':');
            if (colon >= 0)
            {
                if (int.TryParse(token[..colon], out var from)
                    && int.TryParse(token[(colon + 1)..], out var to)
                    && port >= Math.Min(from, to)
                    && port <= Math.Max(from, to))
                    return true;
            }
            else if (int.TryParse(token, out var exact) && exact == port)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> LinuxPortsOpenAsync()
    {
        if (await IsServiceActiveAsync("ufw"))
        {
            const string path = "/etc/ufw/user.rules";
            if (!File.Exists(path))
                return false;
            var rules = await File.ReadAllTextAsync(path);
            return UfwRulesAllow(rules, TcpPort, UdpPort);
        }

        if (await IsServiceActiveAsync("firewalld"))
        {
            if (!await CommandExistsAsync("firewall-cmd"))
                return true;
            var tcp = await RunAsync("firewall-cmd", "--query-port=" + TcpPort + "/tcp");
            var udp = await RunAsync("firewall-cmd", "--query-port=" + UdpPort + "/udp");
            return tcp.ExitCode == 0 && udp.ExitCode == 0;
        }

        return true;
    }

    private static async Task<bool> WindowsPortsOpenAsync()
    {
        var profile = await RunAsync("netsh", "advfirewall show currentprofile");
        if (profile.ExitCode != 0)
            return true;
        if (!ProfileStateIsOn(profile.StdOut))
            return true;

        var tcp = await RunAsync("netsh", "advfirewall firewall show rule name=all protocol=tcp localport=" + TcpPort + " dir=in");
        var udp = await RunAsync("netsh", "advfirewall firewall show rule name=all protocol=udp localport=" + UdpPort + " dir=in");
        if (tcp.ExitCode != 0 || udp.ExitCode != 0)
            return true;
        return HasAllowRule(tcp.StdOut) && HasAllowRule(udp.StdOut);
    }

    internal static bool ProfileStateIsOn(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase))
                return line.EndsWith("ON", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static bool HasAllowRule(string output)
    {
        if (output.Contains("No rules match", StringComparison.OrdinalIgnoreCase))
            return false;
        var enabled = false;
        var allow = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Rule Name:", StringComparison.OrdinalIgnoreCase))
            {
                if (enabled && allow)
                    return true;
                enabled = false;
                allow = false;
            }
            else if (line.StartsWith("Enabled:", StringComparison.OrdinalIgnoreCase)
                     && line.EndsWith("Yes", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
            }
            else if (line.StartsWith("Action:", StringComparison.OrdinalIgnoreCase)
                     && line.Contains("Allow", StringComparison.OrdinalIgnoreCase))
            {
                allow = true;
            }
        }
        return enabled && allow;
    }

    private static async Task<bool> IsServiceActiveAsync(string name)
    {
        var result = await RunAsync("systemctl", "is-active " + name);
        return result.ExitCode == 0 && result.StdOut.Trim().Equals("active", StringComparison.Ordinal);
    }

    private static async Task<bool> CommandExistsAsync(string command)
    {
        var result = await RunAsync("bash", "-c \"command -v " + command + "\"");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    }

    private static async Task<(int ExitCode, string StdOut)> RunAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        if (process == null)
            return (-1, "");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "");
        }
        var stdout = await stdoutTask;
        await stderrTask;
        return (process.ExitCode, stdout);
    }
}
