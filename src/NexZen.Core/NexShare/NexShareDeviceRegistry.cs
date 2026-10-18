using System;
using System.Collections.Generic;
using System.Linq;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare;

/// <summary>
/// One discovered peer, keyed by deviceId, merged from LAN (UDP/mDNS) and BLE sightings.
/// Each source has its own last-seen time so badges/routing only use paths seen recently.
/// </summary>
public sealed class NexShareDeviceEntry
{
    /// <summary>LAN presence is broadcast every 3 s; 15 s without one means LAN is gone.</summary>
    public const long LanTtlMs = 15_000;
    /// <summary>BLE sightings are slower (GATT exchange every 30 s, cached re-announce while BlueZ still sees it).</summary>
    public const long BleTtlMs = 60_000;
    /// <summary>Entry removed from the list when neither source has been seen this long (and it isn't connected).</summary>
    public const long RemoveAfterMs = 60_000;

    public NexShareDeviceEntry(DiscoveryMessage first, long nowMs) { Device = first; Touch(first, nowMs); }

    public DiscoveryMessage Device { get; private set; }
    public long LastLanMs { get; private set; }
    public long LastBleMs { get; private set; }
    public long LastSeenMs => Math.Max(LastLanMs, LastBleMs);
    public string DeviceId => Device.DeviceId;

    public void Touch(DiscoveryMessage incoming, long nowMs)
    {
        if (!ReferenceEquals(incoming, Device)) Device = DiscoveryMessage.Merge(Device, incoming);
        if (!string.IsNullOrEmpty(incoming.Host)) LastLanMs = nowMs;
        if (incoming.ViaBle || !string.IsNullOrEmpty(incoming.BleAddress)) LastBleMs = nowMs;
    }

    public bool LanFresh(long nowMs) => !string.IsNullOrEmpty(Device.Host) && LastLanMs > 0 && nowMs - LastLanMs <= LanTtlMs;
    public bool BleFresh(long nowMs) => !string.IsNullOrEmpty(Device.BleAddress) && LastBleMs > 0 && nowMs - LastBleMs <= BleTtlMs;
    public bool IsStale(long nowMs) => nowMs - LastSeenMs > RemoveAfterMs;

    /// <summary>The merged device with stale paths stripped (Host cleared if LAN is stale, BLE address if BLE is stale).</summary>
    public DiscoveryMessage RoutableView(long nowMs)
    {
        var d = Device;
        var lan = LanFresh(nowMs);
        var ble = BleFresh(nowMs);
        return new DiscoveryMessage
        {
            DeviceId = d.DeviceId, DeviceName = d.DeviceName,
            Host = lan ? d.Host : "", TcpPort = d.TcpPort, HttpPort = d.HttpPort,
            Timestamp = d.Timestamp, Signature = d.Signature,
            ViaBle = ble && d.ViaBle, BleAddress = ble ? d.BleAddress : null, Caps = d.Caps,
        };
    }
}

/// <summary>Thread-safe registry used by the orchestrator for routing.</summary>
public sealed class NexShareDeviceRegistry
{
    private readonly Dictionary<string, NexShareDeviceEntry> _byId = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public NexShareDeviceEntry Upsert(DiscoveryMessage incoming, long nowMs)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(incoming.DeviceId, out var e)) e.Touch(incoming, nowMs);
            else _byId[incoming.DeviceId] = e = new NexShareDeviceEntry(incoming, nowMs);
            return e;
        }
    }

    public NexShareDeviceEntry? Get(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        lock (_gate) return _byId.TryGetValue(deviceId, out var e) ? e : null;
    }

    public IReadOnlyList<NexShareDeviceEntry> Snapshot() { lock (_gate) return _byId.Values.ToList(); }

    /// <summary>Removes stale entries except those <paramref name="keep"/> returns true for. Returns removed ids.</summary>
    public IReadOnlyList<string> Prune(long nowMs, Func<string, bool>? keep = null)
    {
        lock (_gate)
        {
            var dead = _byId.Values.Where(e => e.IsStale(nowMs) && keep?.Invoke(e.DeviceId) != true).Select(e => e.DeviceId).ToList();
            foreach (var id in dead) _byId.Remove(id);
            return dead;
        }
    }

    public void Clear() { lock (_gate) _byId.Clear(); }
}

public enum NexShareRoute { None, Lan, BleWifiDirect, BleDirect }

/// <summary>
/// Route choice (mirrors Android connectToDevice):
///  1. LAN WebSocket when the peer is reachable on the LAN, it hasn't just failed, and it isn't an
///     untrusted host on the Wi-Fi Direct subnet (192.168.49.x would skip SAS);
///  2. otherwise BLE handshake -> Wi-Fi Direct legacy join -> TCP 25556 when the peer advertises wfd_legacy;
///  3. LAN failure falls back to (2).
/// </summary>
public static class NexShareRouting
{
    public const string WfdSubnetPrefix = "192.168.49.";

    public static bool HasLan(DiscoveryMessage d) => !string.IsNullOrWhiteSpace(d.Host) && d.TcpPort > 0;
    public static bool HasBleWfd(DiscoveryMessage d) =>
        !string.IsNullOrEmpty(d.BleAddress) && d.Caps.Contains(NexShareCaps.WfdLegacy);
    public static bool IsUntrustedWfdSubnet(DiscoveryMessage d, bool trusted) =>
        !trusted && d.Host.StartsWith(WfdSubnetPrefix, StringComparison.Ordinal);

    public static NexShareRoute Choose(DiscoveryMessage d, bool lanFailed, bool trusted, bool bleAvailable)
    {
        var lanOk = HasLan(d) && !IsUntrustedWfdSubnet(d, trusted);
        if (lanOk && !lanFailed) return NexShareRoute.Lan;
        if (bleAvailable && HasBleWfd(d)) return NexShareRoute.BleWifiDirect;
        if (lanOk) return NexShareRoute.Lan; // LAN failed before but there is nothing else: retry it
        return NexShareRoute.None;
    }

    public static bool HasBleLink(DiscoveryMessage d) =>
        !string.IsNullOrEmpty(d.BleAddress) && d.Caps.Contains(NexShareCaps.BleLink);

    /// <summary>Link-only sends: LAN if usable, else BLE direct (peer has ble_link), else BLE -> Wi-Fi Direct.</summary>
    public static NexShareRoute ChooseForLink(DiscoveryMessage d, bool lanFailed, bool trusted, bool bleAvailable)
    {
        var lanOk = HasLan(d) && !IsUntrustedWfdSubnet(d, trusted);
        if (lanOk && !lanFailed) return NexShareRoute.Lan;
        if (bleAvailable && HasBleLink(d)) return NexShareRoute.BleDirect;
        if (bleAvailable && HasBleWfd(d)) return NexShareRoute.BleWifiDirect;
        if (lanOk) return NexShareRoute.Lan;
        return NexShareRoute.None;
    }

    public static NexShareRoute FallbackAfterLanFailure(DiscoveryMessage d, bool bleAvailable) =>
        bleAvailable && HasBleWfd(d) ? NexShareRoute.BleWifiDirect : NexShareRoute.None;

    public static string Describe(NexShareRoute r) => r switch
    {
        NexShareRoute.Lan => "LAN",
        NexShareRoute.BleWifiDirect => "Bluetooth → Wi-Fi Direct",
        NexShareRoute.BleDirect => "Bluetooth",
        _ => "none",
    };
}
