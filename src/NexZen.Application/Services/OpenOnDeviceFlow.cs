using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Application.Services;

public enum OpenOnDeviceOutcome { InvalidUrl, NotFound, PortsClosed, InlinePicker, Sent, SendFailed, PickCancelled, Superseded, Error }

/// <summary>Stage log for "Open on another device": ~/.local/share/NexZen/nexshare-openon.log</summary>
public static class OpenOnLog
{
    private static readonly object Gate = new();
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZen", "nexshare-openon.log");

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine("[OpenOn] " + msg);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch { }
    }
}

/// <summary>
/// UI-agnostic "open on another device" flow (Android NexShareController.openOnPc):
/// scan LAN+BLE (min 2.5 s, stop once >=1 device stable 1.5 s, max 8 s) -> 0: "not found",
/// 1: send immediately (queued until connected), >=2: picker. Every path ends in a Notify
/// (except Superseded, when a newer request took over and reports itself).
/// Closing the browser menu does NOT cancel the flow (that silently ended it before).
/// </summary>
public sealed class OpenOnDeviceFlow
{
    public const string Title = "Open on another device";

    public required Func<Task<IReadOnlyList<DiscoveryMessage>>> GetReachable { get; init; }
    public required Func<DiscoveryMessage, string, string, Task<bool>> Send { get; init; }
    /// <summary>(message, isError)</summary>
    public required Action<string, bool> Notify { get; init; }
    /// <summary>Modal picker; null = user cancelled.</summary>
    public required Func<IReadOnlyList<DiscoveryMessage>, Task<DiscoveryMessage?>> Pick { get; init; }
    /// <summary>Optional inline chooser (e.g. pinned pane still open). True = shown; it sends by itself.</summary>
    public Func<IReadOnlyList<DiscoveryMessage>, bool>? TryShowInline { get; init; }
    public Func<Task<bool>> PortsClosed { get; init; } = () => Task.FromResult(false);
    public Func<Task> EnsureStarted { get; init; } = () => Task.CompletedTask;
    public Func<bool> IsSuperseded { get; init; } = () => false;
    public Func<int, Task> Delay { get; init; } = ms => Task.Delay(ms);
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;
    public Action<string> Log { get; init; } = OpenOnLog.Write;
    public int MinScanMs { get; init; } = 2500;
    public int StableMs { get; init; } = 1500;
    public int MaxScanMs { get; init; } = 8000;
    public int PollMs { get; init; } = 500;

    private static string Name(DiscoveryMessage d) => string.IsNullOrWhiteSpace(d.DeviceName) ? d.Host : d.DeviceName;
    private static string Ids(IReadOnlyList<DiscoveryMessage> l) => string.Join(",", l.Select(d => d.DeviceId).OrderBy(x => x, StringComparer.Ordinal));
    private static string Describe(IReadOnlyList<DiscoveryMessage> l) =>
        string.Join("; ", l.Select(d => $"{Name(d)}/{d.DeviceId[..Math.Min(8, d.DeviceId.Length)]} lan={d.Host}:{d.TcpPort} ble={d.BleAddress ?? "-"}"));

    public async Task<OpenOnDeviceOutcome> RunAsync(string? url, string title)
    {
        try
        {
            Log($"start url={url}");
            if (string.IsNullOrWhiteSpace(url)
                || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                Log("invalid url");
                Notify("This link cannot be opened on another device.", true);
                return OpenOnDeviceOutcome.InvalidUrl;
            }

            await EnsureStarted();
            Notify("Looking for devices…", false);

            var started = Clock();
            var current = await GetReachable();
            var lastIds = Ids(current);
            var stableSince = started;
            Log($"scan begin: {current.Count} device(s) {Describe(current)}");
            while (true)
            {
                if (IsSuperseded()) { Log("superseded during scan"); return OpenOnDeviceOutcome.Superseded; }
                await Delay(PollMs);
                current = await GetReachable();
                var ids = Ids(current);
                var now = Clock();
                if (ids != lastIds) { lastIds = ids; stableSince = now; Log($"scan +{now - started}ms: {current.Count} device(s) {Describe(current)}"); }
                var elapsed = now - started;
                if (elapsed >= MaxScanMs || (current.Count > 0 && elapsed >= MinScanMs && now - stableSince >= StableMs))
                    break;
                if (current.Count == 0 && await PortsClosed()) break;
            }
            if (IsSuperseded()) { Log("superseded after scan"); return OpenOnDeviceOutcome.Superseded; }

            var devices = await GetReachable();
            Log($"scan end: {devices.Count} device(s) {Describe(devices)}");

            if (devices.Count == 0)
            {
                if (await PortsClosed())
                {
                    Notify("Ports are blocked. Press Fix Firewall in NexShare.", true);
                    return OpenOnDeviceOutcome.PortsClosed;
                }
                Notify("No nearby NexShare devices found", true);
                return OpenOnDeviceOutcome.NotFound;
            }

            var target = devices[0];
            if (devices.Count > 1)
            {
                if (TryShowInline?.Invoke(devices) == true)
                {
                    Log("inline chooser shown");
                    return OpenOnDeviceOutcome.InlinePicker;
                }
                Log("picker shown");
                var chosen = await Pick(devices);
                if (chosen == null)
                {
                    Log("picker cancelled");
                    Notify("Link send cancelled.", false);
                    return OpenOnDeviceOutcome.PickCancelled;
                }
                target = chosen;
            }
            return await SendToAsync(target, url, title);
        }
        catch (Exception ex)
        {
            Log("error: " + ex);
            Notify($"Couldn't open on another device: {ex.Message}", true);
            return OpenOnDeviceOutcome.Error;
        }
    }

    public async Task<OpenOnDeviceOutcome> SendToAsync(DiscoveryMessage target, string url, string title)
    {
        var name = Name(target);
        Log($"send to {name} ({target.DeviceId}) lan={target.Host}:{target.TcpPort} ble={target.BleAddress ?? "-"}");
        Notify($"Opening on {name}…", false);
        bool ok;
        try { ok = await Send(target, url, title); }
        catch (Exception ex) { Log("send threw: " + ex); ok = false; }
        Log(ok ? "sent" : "send failed");
        if (ok) { Notify($"Sent the link to {name}", false); return OpenOnDeviceOutcome.Sent; }
        Notify($"Couldn't open the link on {name}", true);
        return OpenOnDeviceOutcome.SendFailed;
    }
}
