using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;
using Tmds.DBus.Protocol;

namespace NexZen.Infrastructure.NexShare.WifiDirect;

/// <summary>
/// Linux: joins the phone's legacy Wi-Fi Direct group (DIRECT-NX-xxxx, WPA2-PSK) as a plain Wi-Fi
/// station through NetworkManager D-Bus (AddAndActivateConnection2 with persist=volatile, so the
/// profile never touches disk and NM deletes it when it goes down).
///
/// Interface choice: NetworkManager cannot create a second managed station interface over D-Bus
/// (that needs `iw ... interface add`, i.e. CAP_NET_ADMIN/root), so the join uses the existing Wi-Fi
/// device (wlan0). The previously active Wi-Fi connection is recorded first and re-activated on
/// <see cref="LeaveAsync"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class NmLegacyJoin : IWifiDirectJoiner, IDisposable
{
    private const string Nm = "org.freedesktop.NetworkManager";
    private const string NmPath = "/org/freedesktop/NetworkManager";
    private const string Props = "org.freedesktop.DBus.Properties";
    private const int NmDeviceTypeWifi = 2;
    private const uint ActStateActivated = 2;
    private const uint ActStateDeactivated = 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DBusConnection? _conn;
    private string? _devicePath;
    private string? _iface;
    private string? _tempActive;
    private string? _tempSettings;
    private string? _prevSettings;
    private string? _prevName;

    public bool IsSupported => OperatingSystem.IsLinux();
    public event EventHandler<string>? OnLog;

    private void Log(string m)
    {
        Console.WriteLine("[NexShare] WFD/NM: " + m);
        OnLog?.Invoke(this, m);
    }

    private async Task<DBusConnection> ConnAsync()
    {
        if (_conn != null) return _conn;
        var c = new DBusConnection(DBusAddress.System!);
        await c.ConnectAsync();
        return _conn = c;
    }

    public async Task<WfdJoinResult> JoinAsync(WireP2pCredentials creds, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(creds.Ssid) || string.IsNullOrEmpty(creds.Passphrase))
            return WfdJoinResult.Fail("missing SSID/passphrase");
        await _gate.WaitAsync(ct);
        try
        {
            await LeaveCoreAsync(restore: false);
            var c = await ConnAsync();
            var dev = await FindWifiDeviceAsync(c);
            if (dev == null) return WfdJoinResult.Fail("no Wi-Fi device managed by NetworkManager");
            (_devicePath, _iface) = dev.Value;

            // Record the current Wi-Fi connection so it can be restored afterwards.
            _prevSettings = null; _prevName = null;
            var act = await GetObjPathAsync(c, _devicePath, "org.freedesktop.NetworkManager.Device", "ActiveConnection");
            if (act != null && act != "/")
            {
                _prevSettings = await GetObjPathAsync(c, act, "org.freedesktop.NetworkManager.Connection.Active", "Connection");
                _prevName = await GetStringAsync(c, act, "org.freedesktop.NetworkManager.Connection.Active", "Id");
            }
            Log($"joining {creds.Ssid} on {_iface}; previous connection: {_prevName ?? "none"}");

            try { await RequestScanAsync(c, _devicePath); } catch { /* scan throttled: fine, hidden=true probes anyway */ }

            (string settings, string active) r;
            try { r = await AddAndActivateAsync(c, _devicePath, creds); }
            catch (DBusErrorReplyException ex)
            {
                var denied = ex.ErrorName.Contains("PermissionDenied", StringComparison.Ordinal) ||
                             ex.ErrorName.Contains("NotAuthorized", StringComparison.Ordinal) ||
                             ex.ErrorName.Contains("AccessDenied", StringComparison.Ordinal);
                Log($"AddAndActivateConnection2 failed: {ex.ErrorName}: {ex.Message}");
                await RestorePreviousAsync(c);
                return WfdJoinResult.Fail(denied ? "NetworkManager denied permission (polkit). Allow network control for this user." : ex.Message, denied);
            }
            _tempSettings = r.settings;
            _tempActive = r.active;

            // Wait for activation + a 192.168.49.x DHCP lease (~25 s).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            string? ip = null;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                uint state;
                try { state = await GetUIntAsync(c, _tempActive, "org.freedesktop.NetworkManager.Connection.Active", "State"); }
                catch (DBusErrorReplyException) { state = ActStateDeactivated; } // object vanished
                if (state == ActStateDeactivated)
                {
                    Log("activation failed (wrong passphrase / group not found)");
                    await LeaveCoreAsync(restore: true);
                    return WfdJoinResult.Fail($"could not connect to {creds.Ssid}");
                }
                if (state == ActStateActivated)
                {
                    ip = FindIpv4(_iface, creds.GoIp);
                    if (ip != null) break;
                }
                await Task.Delay(500, ct);
            }
            if (ip == null)
            {
                Log("timed out waiting for an IP on the group");
                await LeaveCoreAsync(restore: true);
                return WfdJoinResult.Fail($"timed out joining {creds.Ssid}");
            }
            Log($"joined {creds.Ssid}: {_iface} {ip}");
            return new WfdJoinResult { Success = true, LocalIp = ip, InterfaceName = _iface };
        }
        catch (OperationCanceledException) { await LeaveCoreAsync(restore: true); return WfdJoinResult.Fail("cancelled"); }
        catch (Exception ex)
        {
            Log("join error: " + ex.Message);
            try { await LeaveCoreAsync(restore: true); } catch { }
            return WfdJoinResult.Fail(ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task LeaveAsync()
    {
        await _gate.WaitAsync();
        try { await LeaveCoreAsync(restore: true); }
        catch (Exception ex) { Log("leave error: " + ex.Message); }
        finally { _gate.Release(); }
    }

    private async Task LeaveCoreAsync(bool restore)
    {
        if (_tempActive == null && _tempSettings == null) return;
        var c = await ConnAsync();
        if (_tempActive != null)
        {
            try { await CallAsync(c, NmPath, Nm, "DeactivateConnection", _tempActive); }
            catch (DBusErrorReplyException ex) { Log("deactivate: " + ex.ErrorName); }
        }
        if (_tempSettings != null)
        {
            // volatile profiles are removed by NM on deactivation; Delete is a best-effort fallback.
            try { await CallAsync(c, _tempSettings, "org.freedesktop.NetworkManager.Settings.Connection", "Delete"); }
            catch (DBusErrorReplyException) { }
        }
        _tempActive = null; _tempSettings = null;
        Log("left Wi-Fi Direct group");
        if (restore) await RestorePreviousAsync(c);
    }

    private async Task RestorePreviousAsync(DBusConnection c)
    {
        if (_prevSettings == null || _devicePath == null) return;
        var prev = _prevSettings; _prevSettings = null;
        try
        {
            await CallAsync(c, NmPath, Nm, "ActivateConnection", prev, _devicePath, "/");
            Log($"restoring previous Wi-Fi: {_prevName}");
        }
        catch (DBusErrorReplyException ex) { Log($"could not restore {_prevName}: {ex.ErrorName}"); }
    }

    private static string? FindIpv4(string? iface, string goIp)
    {
        var prefix = goIp.Contains('.') ? goIp[..(goIp.LastIndexOf('.') + 1)] : "192.168.49.";
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface != null && ni.Name != iface) continue;
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && a.Address.ToString().StartsWith(prefix, StringComparison.Ordinal))
                    return a.Address.ToString();
        }
        return null;
    }

    // ---------------- D-Bus helpers ----------------

    private static Task CallAsync(DBusConnection c, string path, string iface, string member, params string[] objPaths)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Nm, path, iface, member, objPaths.Length == 0 ? null : string.Concat(Enumerable.Repeat("o", objPaths.Length)), MessageFlags.None);
        foreach (var o in objPaths) w.WriteObjectPath(o);
        return c.CallMethodAsync(w.CreateMessage());
    }

    private static Task<VariantValue> GetPropAsync(DBusConnection c, string path, string iface, string name)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Nm, path, Props, "Get", "ss", MessageFlags.None);
        w.WriteString(iface);
        w.WriteString(name);
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var v = m.GetBodyReader().ReadVariantValue();
            while (v.Type == VariantValueType.Variant) v = v.GetVariantValue();
            return v;
        }, null);
    }

    private static async Task<string?> GetObjPathAsync(DBusConnection c, string path, string iface, string name)
    {
        var v = await GetPropAsync(c, path, iface, name);
        return v.Type == VariantValueType.ObjectPath ? v.GetObjectPathAsString() : null;
    }

    private static async Task<string?> GetStringAsync(DBusConnection c, string path, string iface, string name)
    {
        var v = await GetPropAsync(c, path, iface, name);
        return v.Type == VariantValueType.String ? v.GetString() : null;
    }

    private static async Task<uint> GetUIntAsync(DBusConnection c, string path, string iface, string name)
    {
        var v = await GetPropAsync(c, path, iface, name);
        return v.Type == VariantValueType.UInt32 ? v.GetUInt32() : 0;
    }

    private static async Task<(string, string)?> FindWifiDeviceAsync(DBusConnection c)
    {
        var devs = await GetPropAsync(c, NmPath, Nm, "Devices");
        if (devs.Type != VariantValueType.Array) return null;
        (string, string)? fallback = null;
        foreach (var p in devs.GetArray<string>())
        {
            var type = await GetUIntAsync(c, p, "org.freedesktop.NetworkManager.Device", "DeviceType");
            if (type != NmDeviceTypeWifi) continue;
            var name = await GetStringAsync(c, p, "org.freedesktop.NetworkManager.Device", "Interface") ?? "wlan";
            var managed = await GetPropAsync(c, p, "org.freedesktop.NetworkManager.Device", "Managed");
            if (managed.Type == VariantValueType.Bool && !managed.GetBool()) continue;
            fallback ??= (p, name);
        }
        return fallback;
    }

    private static Task RequestScanAsync(DBusConnection c, string dev)
    {
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Nm, dev, "org.freedesktop.NetworkManager.Device.Wireless", "RequestScan", "a{sv}", MessageFlags.None);
        w.WriteDictionary(new Dictionary<string, VariantValue>());
        return c.CallMethodAsync(w.CreateMessage());
    }

    private static Task<(string, string)> AddAndActivateAsync(DBusConnection c, string dev, WireP2pCredentials creds)
    {
        var settings = new Dictionary<string, Dictionary<string, VariantValue>>
        {
            ["connection"] = new()
            {
                ["id"] = VariantValue.String("NexShare " + creds.Ssid),
                ["uuid"] = VariantValue.String(Guid.NewGuid().ToString()),
                ["type"] = VariantValue.String("802-11-wireless"),
                ["autoconnect"] = VariantValue.Bool(false),
            },
            ["802-11-wireless"] = new()
            {
                ["ssid"] = VariantValue.Array(Encoding.UTF8.GetBytes(creds.Ssid)),
                ["mode"] = VariantValue.String("infrastructure"),
                ["hidden"] = VariantValue.Bool(true),
            },
            ["802-11-wireless-security"] = new()
            {
                ["key-mgmt"] = VariantValue.String("wpa-psk"),
                ["psk"] = VariantValue.String(creds.Passphrase),
            },
            ["ipv4"] = new() { ["method"] = VariantValue.String("auto"), ["never-default"] = VariantValue.Bool(true) },
            ["ipv6"] = new() { ["method"] = VariantValue.String("ignore") },
        };
        using var w = c.GetMessageWriter();
        w.WriteMethodCallHeader(Nm, NmPath, Nm, "AddAndActivateConnection2", "a{sa{sv}}ooa{sv}", MessageFlags.None);
        var outer = w.WriteDictionaryStart();
        foreach (var (group, dict) in settings)
        {
            w.WriteDictionaryEntryStart();
            w.WriteString(group);
            w.WriteDictionary(dict);
        }
        w.WriteDictionaryEnd(outer);
        w.WriteObjectPath(dev);
        w.WriteObjectPath("/");
        w.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["persist"] = VariantValue.String("volatile"),
            ["bind-activation"] = VariantValue.String("none"),
        });
        return c.CallMethodAsync(w.CreateMessage(), (Message m, object? _) =>
        {
            var r = m.GetBodyReader();
            var s = r.ReadObjectPathAsString();
            var a = r.ReadObjectPathAsString();
            return (s, a);
        }, null);
    }

    public void Dispose() => _conn?.Dispose();
}
