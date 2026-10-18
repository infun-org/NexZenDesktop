using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Ble;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;
using Tmds.DBus.Protocol;

namespace NexZen.Infrastructure.NexShare.Ble;

/// <summary>
/// Linux BLE for NexShare via BlueZ D-Bus (system bus). Mirrors Android NexShareBleDiscovery + NexShareBleHandshake:
/// peripheral (LEAdvertisement1 + GATT app with 0xBBCC / 0xCCDD / 0xDDEE) and central (scan, info exchange,
/// initiator handshake). The PC never acts as Wi-Fi Direct group owner (no "wfd_go" capability).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BlueZBleService : INexShareBleService, IDisposable
{
    private const string Root = "/com/nexzen/nexshare";
    private const string SvcPath = Root + "/service0";
    private const string InfoReadPath = SvcPath + "/char0";
    private const string InfoWritePath = SvcPath + "/char1";
    private const string HsPath = SvcPath + "/char2";
    private const string AdvPath = Root + "/adv0";
    private static readonly TimeSpan ExchangeCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);

    private readonly INexShareCrypto _crypto;
    private readonly object _gate = new();
    private DBusConnection? _conn;
    private string _adapterPath = "/org/bluez/hci0";
    private CancellationTokenSource? _cts;
    private bool _appRegistered, _advRegistered, _discovering;
    private int _discoveryMisses;
    private volatile bool _available;
    private string? _bluezOwner;
    private readonly List<DateTimeOffset> _bluezRestarts = new();

    private string _myId = "";
    private string _myName = "NexZen Desktop";
    private int _wsPort = 43000;

    // Peripheral state
    private readonly ConcurrentDictionary<string, NexShareBleHandshakeSession> _serverSessions = new();
    private readonly ConcurrentDictionary<string, int> _serverMtu = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _serverPeersSeen = new();
    private readonly ConcurrentDictionary<string, InfoWriteBuffer> _infoWrites = new();
    private readonly SemaphoreSlim _notifyLock = new(1, 1);
    private readonly Queue<byte[]> _notifyQueue = new();
    private volatile bool _notifying;

    // Central state
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastExchange = new();
    private DateTimeOffset _crashBackoffUntil = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, (NexShareBleInfo Info, string Name, string Address)> _infoByPath = new();
    private readonly SemaphoreSlim _linkLock = new(1, 1);
    private NexShareBleHandshakeSession? _clientSession;
    private volatile string? _clientDevPath;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPull = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pullDone = new();
    private CancellationTokenSource? _exchangeCts;
    private volatile bool _pullInFlight;

    public BlueZBleService(INexShareCrypto crypto) { _crypto = crypto; }

    public Func<string, bool>? TrustPredicate { get; set; }

    public bool IsAvailable => _available;

    public event EventHandler<DiscoveryMessage>? OnDeviceDiscovered;
    public event EventHandler<SasPrompt>? OnSasRequested;
    public event EventHandler<BleVerifiedEventArgs>? OnVerified;
    public event EventHandler<BleCredentialsEventArgs>? OnCredentials;
    public event EventHandler<BleHandshakeFailedEventArgs>? OnHandshakeFailed;
    public event EventHandler<string>? OnStatus;

    public void Configure(string myDeviceId, string myDeviceName, int wsPort)
    {
        _myId = myDeviceId;
        _myName = string.IsNullOrWhiteSpace(myDeviceName) ? _myName : myDeviceName;
        _wsPort = wsPort;
    }

    private string InfoPayload => NexShareBleInfoCodec.Build(_myId, _wsPort, NexShareBleInfoCodec.LocalCaps);

    private static readonly string LogFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexZen", "nexshare-ble.log");
    private static readonly object LogLock = new();

    /// <summary>Console + ~/.local/share/NexZen/nexshare-ble.log (so a crash timeline survives without the terminal).</summary>
    private static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [NexShareBle] {msg}";
        Console.WriteLine(line);
        try
        {
            lock (LogLock)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFile)!);
                System.IO.File.AppendAllText(LogFile, line + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>Last time a remote GATT client touched our server (read/write/notify). Used to avoid tearing down its link.</summary>
    private long _lastServerActivityTicks;
    private void MarkServerActivity() => Interlocked.Exchange(ref _lastServerActivityTicks, DateTime.UtcNow.Ticks);
    private bool ServerActiveWithin(TimeSpan t) => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastServerActivityTicks) < t.Ticks;

    // ---------------------------------------------------------------- lifecycle

    public Task StartListeningAsync()
    {
        lock (_gate)
        {
            if (_cts != null) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
        }
        var ct = _cts.Token;
        _ = Task.Run(() => RunAsync(ct));
        return Task.CompletedTask;
    }

    /// <summary>Keeps trying until BlueZ is reachable and the adapter is powered, then runs the scan loop.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var warned = false;
        _ = PullWatchLoopAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_available)
                {
                    if (DateTimeOffset.UtcNow < _crashBackoffUntil)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        continue;
                    }
                    if (await TryBringUpAsync())
                    {
                        warned = false;
                        OnStatus?.Invoke(this, "Bluetooth ready");
                    }
                    else
                    {
                        if (!warned) { Log("BlueZ/adapter unavailable; retrying every 10 s"); warned = true; }
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        continue;
                    }
                }
                await ScanOnceAsync(ct);
                await Task.Delay(ScanInterval, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("loop error: " + ex.Message);
                await TearDownAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<bool> TryBringUpAsync()
    {
        try
        {
            var conn = _conn;
            if (conn == null)
            {
                conn = new DBusConnection(DBusAddress.System!);
                await conn.ConnectAsync();
                conn.AddMethodHandler(new ObjectHandler(this));
                _conn = conn;
            }
            var owner = await BlueZDBus.GetBluezOwnerAsync(conn);
            if (owner == null) { Log("org.bluez is not on the system bus (bluetoothd not running)"); return false; }
            if (_bluezOwner != null && owner != _bluezOwner)
            {
                // bluetoothd restarted: it forgot our advertisement, GATT app and discovery session.
                _appRegistered = _advRegistered = _discovering = false;
            }
            _bluezOwner = owner;
            var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
            var adapter = objs.FirstOrDefault(kv => kv.Value.ContainsKey(BlueZDBus.Adapter1)
                && kv.Value.ContainsKey(BlueZDBus.GattManager1) && kv.Value.ContainsKey(BlueZDBus.AdvManager1));
            if (adapter.Key == null) { Log("no adapter with GattManager1+LEAdvertisingManager1"); return false; }
            _adapterPath = adapter.Key;
            if (!BlueZDBus.GetBool(adapter.Value[BlueZDBus.Adapter1], "Powered")) { Log($"{_adapterPath} is powered off"); return false; }

            if (!_appRegistered)
            {
                Log("step: RegisterApplication");
                await BlueZDBus.CallWithPathAndDictAsync(conn, _adapterPath, BlueZDBus.GattManager1, "RegisterApplication", Root);
                _appRegistered = true;
                Log("GATT application registered");
            }
            if (!_advRegistered)
            {
                Log("step: RegisterAdvertisement");
                try
                {
                    await BlueZDBus.CallWithPathAndDictAsync(conn, _adapterPath, BlueZDBus.AdvManager1, "RegisterAdvertisement", AdvPath);
                }
                catch (DBusErrorReplyException ex) when (_advIncludeName)
                {
                    Log($"advertisement with LocalName refused ({ex.ErrorName}); retrying without name");
                    _advIncludeName = false;
                    await BlueZDBus.CallWithPathAndDictAsync(conn, _adapterPath, BlueZDBus.AdvManager1, "RegisterAdvertisement", AdvPath);
                }
                _advRegistered = true;
                Log($"advertisement registered (name={(_advIncludeName ? _myName : "<none>")}, id hash={NexShareBleInfoCodec.JavaHashCode(_myId)})");
            }
            if (!_discovering)
            {
                Log("step: SetDiscoveryFilter + StartDiscovery");
                await BlueZDBus.CallWithDictAsync(conn, _adapterPath, BlueZDBus.Adapter1, "SetDiscoveryFilter", new Dictionary<string, VariantValue>
                {
                    // No "UUIDs" key: BlueZ 5.87 is_filter_match() (bluez/bluez#2282) SEGVs bluetoothd as soon
                    // as an advert matching a UUID filter arrives, i.e. whenever the phone (0xAABB) is nearby.
                    // ScanOnceAsync filters on the NEXSHARE service data itself.
                    ["Transport"] = VariantValue.String("le"),
                    ["DuplicateData"] = VariantValue.Bool(true)
                });
                try { await BlueZDBus.CallAsync(conn, _adapterPath, BlueZDBus.Adapter1, "StartDiscovery"); }
                catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.bluez.Error.InProgress") { }
                _discovering = true;
                Log("LE discovery started");
            }
            _available = true;
            return true;
        }
        catch (Exception ex)
        {
            Log("bring-up failed: " + ex.Message);
            await TearDownAsync();
            return false;
        }
    }

    public async Task StopListeningAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _cts; _cts = null; }
        try { cts?.Cancel(); } catch { }
        await TearDownAsync();
    }

    private async Task TearDownAsync()
    {
        _available = false;
        var conn = _conn;
        if (conn == null) return;
        try { _clientSession?.Fail("BLE stopping"); } catch { }
        foreach (var s in _serverSessions.Values) { try { s.Fail("BLE stopping"); } catch { } }
        _serverSessions.Clear();
        if (_discovering) { try { await BlueZDBus.CallAsync(conn, _adapterPath, BlueZDBus.Adapter1, "StopDiscovery"); } catch { } }
        if (_advRegistered) { try { await BlueZDBus.CallWithPathAsync(conn, _adapterPath, BlueZDBus.AdvManager1, "UnregisterAdvertisement", AdvPath); } catch { } }
        if (_appRegistered) { try { await BlueZDBus.CallWithPathAsync(conn, _adapterPath, BlueZDBus.GattManager1, "UnregisterApplication", Root); } catch { } }
        _discovering = _advRegistered = _appRegistered = false;
        _notifying = false;
        try { conn.RemoveMethodHandler(Root); } catch { }
        try { conn.Dispose(); } catch { }
        _conn = null;
        Log("stopped (advertisement + GATT app unregistered)");
    }

    public Task BroadcastPresenceAsync(DiscoveryMessage message)
    {
        // BLE presence is the advertisement itself. Keep the name/id in sync.
        if (!string.IsNullOrEmpty(message.DeviceId)) _myId = message.DeviceId;
        if (!string.IsNullOrEmpty(message.DeviceName)) _myName = message.DeviceName;
        return Task.CompletedTask;
    }

    public void Dispose() => StopListeningAsync().GetAwaiter().GetResult();

    // ---------------------------------------------------------------- central: scan + info exchange

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var conn = _conn;
        if (conn == null) return;
        var owner = await BlueZDBus.GetBluezOwnerAsync(conn);
        if (owner != _bluezOwner)
        {
            Log($"bluetoothd restarted (owner {_bluezOwner} -> {owner ?? "none"}); re-registering");
            var now = DateTimeOffset.UtcNow;
            _bluezRestarts.RemoveAll(t => now - t > TimeSpan.FromMinutes(5));
            _bluezRestarts.Add(now);
            _appRegistered = _advRegistered = _discovering = false;
            _available = false;
            if (_bluezRestarts.Count >= 3)
            {
                // Safety valve: if bluetoothd keeps dying right after we register, stop poking it.
                // Back off instead of disabling BLE for the rest of the session.
                _crashBackoffUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                _bluezRestarts.Clear();
                Log("bluetoothd restarted 3 times in 5 min; pausing BLE for 2 min, then retrying");
                OnStatus?.Invoke(this, "Bluetooth service keeps failing; pausing BLE for 2 minutes");
            }
            return;
        }
        var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
        if (objs.TryGetValue(_adapterPath, out var ad) && ad.TryGetValue(BlueZDBus.Adapter1, out var adp) && !BlueZDBus.GetBool(adp, "Powered"))
            throw new InvalidOperationException("adapter powered off");
        bool notDiscovering = _discovering && ad != null && ad.TryGetValue(BlueZDBus.Adapter1, out var adp2) && !BlueZDBus.GetBool(adp2, "Discovering");
        _discoveryMisses = notDiscovering ? _discoveryMisses + 1 : 0;
        if (_discoveryMisses >= 2) // property can lag right after StartDiscovery; require two consecutive misses
        {
            _discoveryMisses = 0;
            // Discovery session was dropped (bluetoothd restart/other client); without it the phone is never seen over BLE.
            Log("adapter Discovering=false; restarting LE discovery");
            _discovering = false;
            _available = false;
            return;
        }

        foreach (var (sp, ss) in _serverSessions)
        {
            if (ss.IsFinished) continue;
            if (!objs.TryGetValue(sp, out var dIf) || !dIf.TryGetValue(BlueZDBus.Device1, out var dProps) || !BlueZDBus.GetBool(dProps, "Connected"))
                ss.Fail("central disconnected");
        }

        // Pull first so an in-flight info exchange (15 s Connect) can be cancelled. A cached pull
        // advert must not skip the rest of discovery — that emptied the device list (PC → phone NotFound).
        foreach (var (path, ifaces) in objs)
        {
            if (ct.IsCancellationRequested) return;
            if (!path.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal)) continue;
            if (!ifaces.TryGetValue(BlueZDBus.Device1, out var dev)) continue;
            if (!BlueZDBus.TryGetServiceData(dev, NexShareBleUuids.Service, out var magic)) continue;
            var magicStr = Encoding.UTF8.GetString(magic);
            if (!magicStr.StartsWith(NexShareBleUuids.MagicWord, StringComparison.Ordinal)) continue;
            if (!dev.ContainsKey("RSSI")) continue;
            if (IsPullForMe(magicStr)) TryHandlePull(path, dev, magicStr);
        }

        var handshakeOwnsRadio = _pullInFlight || _clientSession is { IsFinished: false }
            || _serverSessions.Values.Any(x => !x.IsFinished)
            || _clientDevPath != null;

        // Android rotates the LE address; BlueZ keeps every old Device1. Only GATT-connect the
        // currently advertising (highest RSSI) NexShare peer — connecting the zombies times out
        // for 15 s and starves the handshake.
        string? bestPath = null;
        Dictionary<string, VariantValue>? bestDev = null;
        var bestRssi = short.MinValue;
        foreach (var (path, ifaces) in objs)
        {
            if (!path.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal)) continue;
            if (!ifaces.TryGetValue(BlueZDBus.Device1, out var dev)) continue;
            if (!BlueZDBus.TryGetServiceData(dev, NexShareBleUuids.Service, out var magic)) continue;
            var magicStr = Encoding.UTF8.GetString(magic);
            if (!magicStr.StartsWith(NexShareBleUuids.MagicWord, StringComparison.Ordinal)) continue;
            if (IsPullForMe(magicStr)) continue;
            if (!dev.ContainsKey("RSSI"))
            {
                if (!BlueZDBus.GetBool(dev, "Connected") && !PathInUse(path))
                    _ = ForgetDeviceAsync(conn, path);
                continue;
            }
            var rssi = BlueZDBus.GetInt16(dev, "RSSI");
            if (bestPath == null || rssi >= bestRssi)
            {
                bestRssi = rssi;
                bestPath = path;
                bestDev = dev;
            }
        }
        if (bestPath != null && bestDev != null)
        {
            if (_lastExchange.TryGetValue(bestPath, out var last) && DateTimeOffset.UtcNow - last < ExchangeCooldown)
            {
                if (_infoByPath.TryGetValue(bestPath, out var cached)) Publish(cached.Info, cached.Name, cached.Address);
            }
            else if (!handshakeOwnsRadio && !ServerActiveWithin(TimeSpan.FromSeconds(15)))
            {
                _lastExchange[bestPath] = DateTimeOffset.UtcNow;
                await ExchangeInfoAsync(conn, bestPath, bestDev);
            }
            else if (_infoByPath.TryGetValue(bestPath, out var cached))
                Publish(cached.Info, cached.Name, cached.Address);
        }
    }

    private readonly object _pullGate = new();
    private volatile string? _exchangePath;

    private bool IsPullForMe(string magicStr) =>
        magicStr.Length > NexShareBleUuids.MagicWord.Length && _myId.Length >= 4
        && magicStr[NexShareBleUuids.MagicWord.Length..].StartsWith(_myId[..4], StringComparison.Ordinal);

    /// <summary>
    /// Pull adverts must not wait for the 3 s scan loop, which is often blocked for 10-15 s by an info exchange
    /// with a stale (rotated) phone address. This loop only looks for pull adverts, every 700 ms.
    /// </summary>
    private async Task PullWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(700, ct);
                var conn = _conn;
                if (conn == null || !_available || _pullInFlight) continue;
                var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
                foreach (var (path, ifaces) in objs)
                {
                    if (!path.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal)) continue;
                    if (!ifaces.TryGetValue(BlueZDBus.Device1, out var dev)) continue;
                    if (!BlueZDBus.TryGetServiceData(dev, NexShareBleUuids.Service, out var magic)) continue;
                    var magicStr = Encoding.UTF8.GetString(magic);
                    if (!magicStr.StartsWith(NexShareBleUuids.MagicWord, StringComparison.Ordinal) || !dev.ContainsKey("RSSI")) continue;
                    if (IsPullForMe(magicStr)) TryHandlePull(path, dev, magicStr);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* the main loop handles BlueZ errors */ }
        }
    }

    private void TryHandlePull(string path, Dictionary<string, VariantValue> dev, string magicStr)
    {
        lock (_pullGate)
        {
            // Link pull: this phone has a link for us and waits for us to connect (typically 10 s before it
            // falls back to connecting to our GATT server itself).
            var now = DateTimeOffset.UtcNow;
            // The advert value carries a per-send nonce; once a link arrived for it, ignore that value (the phone
            // may keep advertising it for a moment, or BlueZ may keep the cached ServiceData).
            if (_pullDone.TryGetValue(magicStr, out var doneAt) && now - doneAt < TimeSpan.FromSeconds(60)) return;
            // Phone is already writing the handshake to our GATT server: let that path deliver the link.
            if (_serverSessions.Values.Any(s => !s.IsFinished)) return;
            if (_pullInFlight || _clientSession is { IsFinished: false }) return;
            if (_lastPull.TryGetValue(magicStr, out var lp) && now - lp < TimeSpan.FromSeconds(2)) return;

            _lastPull[magicStr] = now;
            _pullInFlight = true;
            var pullKey = magicStr;
            var pullAddr = BlueZDBus.GetString(dev, "Address") ?? path[(path.LastIndexOf("dev_", StringComparison.Ordinal) + 4)..].Replace('_', ':');
            var pullId = _infoByPath.TryGetValue(path, out var ci) ? ci.Info.DeviceId : "unknown";
            Log($"link pull advert from {pullAddr} ({pullId}); connecting to receive");
            AbortExchangeForPull(path);
            _ = Task.Run(async () =>
            {
                try
                {
                    _quietFail = true;
                    _lastFailReason = null;
                    var r = await RunInitiatorAsync(pullAddr, pullId, null, expectIncomingLink: true);
                    // Only suppress this advert when the link actually arrived. A WFD handshake (verified, no
                    // ble_url) is a failed pull — the phone reused a GATT session without pendingPullLink.
                    if (r.Delivered) _pullDone[pullKey] = DateTimeOffset.UtcNow;
                    else Log($"link pull from {pullAddr} did not deliver a link (verified={r.Verified})");
                }
                catch (Exception ex) { Log("link pull failed: " + ex.Message); }
                finally { _quietFail = false; _pullInFlight = false; }
            });
        }
    }

    /// <summary>Drop an in-flight info-read so the pull can take <see cref="_linkLock"/> immediately.</summary>
    private void AbortExchangeForPull(string pullPath)
    {
        try { _exchangeCts?.Cancel(); } catch { }
        var conn = _conn;
        var ex = _exchangePath;
        if (conn == null) return;
        if (ex != null && ex != pullPath)
        {
            Log($"aborting info exchange with {ex} for link pull");
            _ = BlueZDBus.CallAsync(conn, ex, BlueZDBus.Device1, "Disconnect");
        }
        _ = DisconnectOthersAsync(conn, pullPath);
    }

    private async Task ExchangeInfoAsync(DBusConnection conn, string devPath, Dictionary<string, VariantValue> dev)
    {
        if (_pullInFlight) return;
        if (!await _linkLock.WaitAsync(0)) return;
        if (_pullInFlight) { _linkLock.Release(); return; }
        _exchangePath = devPath;
        using var cts = new CancellationTokenSource();
        _exchangeCts = cts;
        var weConnected = false;
        try
        {
            var addr = BlueZDBus.GetString(dev, "Address") ?? "";
            var name = BlueZDBus.GetString(dev, "Alias") ?? BlueZDBus.GetString(dev, "Name") ?? "NexShare Device";
            Log($"exchange: connecting {addr} ({devPath})");
            weConnected = await EnsureConnectedAsync(conn, devPath, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var chars = await FindCharsAsync(conn, devPath);
            if (!chars.TryGetValue(NexShareBleUuids.CharInfoRead, out var readPath)) { Log($"{addr}: no 0xBBCC"); return; }
            var payload = Encoding.UTF8.GetString(await BlueZDBus.ReadValueAsync(conn, readPath));
            var info = NexShareBleInfoCodec.Parse(payload);
            if (info == null || info.DeviceId == _myId) return;
            Log($"GATT Client: read {addr} -> id={info.DeviceId} wsPort={info.WsPort} caps=[{string.Join(",", info.Caps)}]");
            _infoByPath[devPath] = (info, name, addr);
            Publish(info, name, addr);

            // Collision rule (Android): if the peer is also connected to our server and its id is larger, yield.
            if (_serverPeersSeen.TryGetValue(devPath, out var seen) && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(10)
                && string.CompareOrdinal(info.DeviceId, _myId) > 0)
            {
                Log("GATT Client: collision detected and peer > me, yielding");
                return;
            }
            if (chars.TryGetValue(NexShareBleUuids.CharInfoWrite, out var writePath))
                await BlueZDBus.WriteValueAsync(conn, writePath, Encoding.UTF8.GetBytes(InfoPayload));
        }
        catch (OperationCanceledException) { Log($"exchange {devPath} aborted for pull"); }
        catch (Exception ex) { Log($"exchange {devPath} failed: {ex.Message}"); }
        finally
        {
            if (_exchangeCts == cts) _exchangeCts = null;
            _exchangePath = null;
            _linkLock.Release();
            if (weConnected && !_pullInFlight) _ = DisconnectLaterAsync(conn, devPath);
        }
    }

    /// <summary>
    /// Device1.Disconnect drops the whole LE link. Only keep THIS path when a handshake or recent GATT-server
    /// use is on it — a client session on a different (rotated) address must not pin an old info-exchange link.
    /// Two simultaneous connections to the same phone (old addr + pull addr) make Android reuse a GATT session
    /// without pendingPullLink, so the phone sends p2p_credentials instead of ble_url.
    /// </summary>
    private async Task DisconnectLaterAsync(DBusConnection conn, string devPath)
    {
        await Task.Delay(800);
        if (PathInUse(devPath))
        {
            Log($"keeping link {devPath}: in use");
            return;
        }
        await ForgetDeviceAsync(conn, devPath);
    }

    /// <summary>
    /// Unpaired LE random addresses must be removed after Disconnect; otherwise BlueZ keeps a
    /// zombie Device1 that ScanOnce / handshake will try (Connect timeout / UnknownObject).
    /// </summary>
    private async Task ForgetDeviceAsync(DBusConnection conn, string devPath)
    {
        if (PathInUse(devPath)) return;
        try { await BlueZDBus.CallAsync(conn, devPath, BlueZDBus.Device1, "Disconnect"); } catch { }
        try { await BlueZDBus.CallWithPathAsync(conn, _adapterPath, BlueZDBus.Adapter1, "RemoveDevice", devPath); } catch { }
        _infoByPath.TryRemove(devPath, out _);
        _lastExchange.TryRemove(devPath, out _);
    }

    private bool PathInUse(string devPath)
    {
        // Claimed by an in-flight handshake (session may not exist yet — set _clientDevPath first).
        if (!string.IsNullOrEmpty(_clientDevPath) && string.Equals(_clientDevPath, devPath, StringComparison.Ordinal))
            return true;
        if (_serverSessions.TryGetValue(devPath, out var s) && !s.IsFinished) return true;
        if (_serverPeersSeen.TryGetValue(devPath, out var seen) && DateTimeOffset.UtcNow - seen < TimeSpan.FromSeconds(3))
            return true;
        return false;
    }

    /// <summary>Drop every other LE device so a pull handshake is the only link to the phone.</summary>
    private async Task DisconnectOthersAsync(DBusConnection conn, string exceptPath)
    {
        try
        {
            var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
            foreach (var (path, ifaces) in objs)
            {
                if (path == exceptPath) continue;
                if (!path.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal)) continue;
                if (!ifaces.TryGetValue(BlueZDBus.Device1, out var d)) continue;
                if (!BlueZDBus.GetBool(d, "Connected")) continue;
                if (PathInUse(path)) continue;
                Log($"disconnecting {path} so pull can use the radio");
                try { await BlueZDBus.CallAsync(conn, path, BlueZDBus.Device1, "Disconnect"); } catch { }
            }
        }
        catch (Exception ex) { Log("DisconnectOthers: " + ex.Message); }
    }

    private void Publish(NexShareBleInfo info, string name, string address)
    {
        OnDeviceDiscovered?.Invoke(this, new DiscoveryMessage
        {
            DeviceId = info.DeviceId,
            DeviceName = name,
            Host = "",
            TcpPort = info.WsPort,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViaBle = true,
            BleAddress = address,
            Caps = info.Caps.ToList()
        });
    }

    /// <summary>
    /// Poll until BlueZ has exported Device1 at <paramref name="devPath"/>. First Connect after launch,
    /// and every retry after a timed-out Connect, races this: the path is gone until the next advert.
    /// </summary>
    private static async Task WaitForDeviceObjectAsync(DBusConnection conn, string devPath, CancellationToken ct, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        var logged = false;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _ = await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "Connected");
                return;
            }
            catch (Exception ex) when (BlueZDBus.IsUnknownObject(ex))
            {
                last = ex;
                if (!logged)
                {
                    Log($"BlueZ has no object at {devPath} yet; waiting for rediscovery");
                    logged = true;
                }
                await Task.Delay(200, ct);
            }
        }
        throw last ?? new TimeoutException("BlueZ device object did not appear");
    }

    /// <summary>Returns true when this call created the connection.</summary>
    private static async Task<bool> EnsureConnectedAsync(DBusConnection conn, string devPath, CancellationToken ct = default)
    {
        await WaitForDeviceObjectAsync(conn, devPath, ct);
        var created = false;
        bool connected;
        try
        {
            connected = BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "Connected")).GetBool();
        }
        catch (Exception ex) when (BlueZDBus.IsUnknownObject(ex))
        {
            await WaitForDeviceObjectAsync(conn, devPath, ct);
            connected = BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "Connected")).GetBool();
        }
        if (!connected)
        {
            async Task ConnectOnceAsync()
            {
                var connectTask = BlueZDBus.CallAsync(conn, devPath, BlueZDBus.Device1, "Connect");
                var delay = Task.Delay(15000, ct);
                if (await Task.WhenAny(connectTask, delay) != connectTask)
                {
                    try { await BlueZDBus.CallAsync(conn, devPath, BlueZDBus.Device1, "Disconnect"); } catch { }
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("Connect timeout");
                }
                await connectTask;
            }
            try { await ConnectOnceAsync(); }
            catch (Exception ex) when (BlueZDBus.IsUnknownObject(ex))
            {
                Log($"Connect hit UnknownObject on {devPath}; waiting then retrying Connect");
                await WaitForDeviceObjectAsync(conn, devPath, ct);
                await ConnectOnceAsync();
            }
            created = true;
        }
        var vanished = 0;
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "ServicesResolved")).GetBool())
                    return created;
                vanished = 0;
            }
            catch (Exception ex) when (BlueZDBus.IsUnknownObject(ex))
            {
                if (++vanished >= 3) throw new TimeoutException("device vanished during ServicesResolved");
                await WaitForDeviceObjectAsync(conn, devPath, ct, timeoutMs: 4000);
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("ServicesResolved timeout");
    }

    private static async Task<Dictionary<string, string>> FindCharsAsync(DBusConnection conn, string devPath)
    {
        var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, ifaces) in objs)
        {
            if (!path.StartsWith(devPath + "/", StringComparison.Ordinal)) continue;
            if (!ifaces.TryGetValue(BlueZDBus.GattChar1, out var ch)) continue;
            var uuid = BlueZDBus.GetString(ch, "UUID");
            if (uuid != null) result[uuid.ToLowerInvariant()] = path;
        }
        return result;
    }

    // ---------------------------------------------------------------- central: initiator handshake

    public event EventHandler<BleLinkReceivedEventArgs>? OnLinkReceived;

    public async Task<bool> StartHandshakeAsync(string bleAddress, string peerId) =>
        (await RunInitiatorAsync(bleAddress, peerId, null)).Verified;

    public async Task<BleLinkResult> SendLinkAsync(string bleAddress, string peerId, WireBleUrl link)
    {
        _quietFail = true;
        _lastFailReason = null;
        bool verified, delivered;
        try
        {
            (verified, delivered) = await RunInitiatorAsync(bleAddress, peerId, link);
            if (!verified && !delivered)
            {
                var reason = _lastFailReason ?? "unknown";
                if (IsTransientBleError(reason))
                {
                    var fresh = await WaitForLiveAddressAsync(peerId, bleAddress);
                    Log($"first BLE link attempt failed transiently ({reason}) -> retrying once"
                        + (string.Equals(fresh, bleAddress, StringComparison.OrdinalIgnoreCase) ? "" : $" via {fresh}"));
                    (verified, delivered) = await RunInitiatorAsync(fresh, peerId, link);
                }
            }
        }
        finally { _quietFail = false; }
        if (!verified && !delivered) Fail(peerId, _lastFailReason ?? "unknown");
        if (delivered) return BleLinkResult.Delivered;
        return verified ? BleLinkResult.FellBackToWifiDirect : BleLinkResult.Failed;
    }

    /// <summary>
    /// Address currently advertising (has RSSI). Never return a cached RPA whose Device1 is gone or
    /// has no RSSI — that is what made every PC→phone send hit UnknownObject.
    /// </summary>
    private async Task<string> ResolveLiveAddressAsync(string peerId, string fallback)
    {
        var conn = _conn;
        if (conn == null) return fallback;
        BlueZDBus.ManagedObjects objs;
        try { objs = await BlueZDBus.GetManagedObjectsAsync(conn); }
        catch { return fallback; }

        string? fallbackLive = null;
        string? bestKnown = null;
        var bestKnownRssi = short.MinValue;
        string? bestAny = null;
        var bestAnyRssi = short.MinValue;
        foreach (var (path, ifaces) in objs)
        {
            if (!path.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal)) continue;
            if (!ifaces.TryGetValue(BlueZDBus.Device1, out var dev)) continue;
            if (!dev.ContainsKey("RSSI")) continue;
            if (!BlueZDBus.TryGetServiceData(dev, NexShareBleUuids.Service, out var magic)) continue;
            var magicStr = Encoding.UTF8.GetString(magic);
            if (!magicStr.StartsWith(NexShareBleUuids.MagicWord, StringComparison.Ordinal)) continue;
            if (IsPullForMe(magicStr)) continue;
            var rssi = BlueZDBus.GetInt16(dev, "RSSI");
            var addr = BlueZDBus.GetString(dev, "Address")
                ?? path[(path.LastIndexOf("dev_", StringComparison.Ordinal) + 4)..].Replace('_', ':');
            if (string.Equals(addr, fallback, StringComparison.OrdinalIgnoreCase)) fallbackLive = addr;
            if (rssi >= bestAnyRssi) { bestAnyRssi = rssi; bestAny = addr; }
            if (_infoByPath.TryGetValue(path, out var cached)
                && (peerId == "unknown" || string.Equals(cached.Info.DeviceId, peerId, StringComparison.Ordinal))
                && rssi >= bestKnownRssi)
            {
                bestKnownRssi = rssi;
                bestKnown = addr;
            }
        }
        return fallbackLive ?? bestKnown ?? bestAny ?? fallback;
    }

    private async Task<string> WaitForLiveAddressAsync(string peerId, string fallback)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string addr = fallback;
        while (sw.ElapsedMilliseconds < 5000)
        {
            addr = await ResolveLiveAddressAsync(peerId, fallback);
            var conn = _conn;
            if (conn != null)
            {
                var path = BlueZDBus.DevicePath(_adapterPath, addr);
                try
                {
                    var objs = await BlueZDBus.GetManagedObjectsAsync(conn);
                    if (objs.TryGetValue(path, out var ifaces)
                        && ifaces.TryGetValue(BlueZDBus.Device1, out var dev)
                        && dev.ContainsKey("RSSI"))
                    {
                        if (sw.ElapsedMilliseconds >= 400) return addr;
                    }
                }
                catch { }
            }
            await Task.Delay(250);
        }
        return await ResolveLiveAddressAsync(peerId, fallback);
    }

    private async Task<(bool Verified, bool Delivered)> RunInitiatorAsync(string bleAddress, string peerId, WireBleUrl? link, bool expectIncomingLink = false)
    {
        var conn = _conn;
        if (conn == null || !_available) return (false, false);
        var resolved = await ResolveLiveAddressAsync(peerId, bleAddress);
        if (!string.Equals(resolved, bleAddress, StringComparison.OrdinalIgnoreCase))
        {
            Log($"peer {peerId} BLE address {bleAddress} -> {resolved} (live advert)");
            bleAddress = resolved;
        }
        var devPath = BlueZDBus.DevicePath(_adapterPath, bleAddress);
        var delivered = false;
        await _linkLock.WaitAsync();
        IDisposable? watch = null;
        var sendLock = new SemaphoreSlim(1, 1);
        _clientDevPath = devPath;
        try
        {
            _clientSession?.Fail("superseded");
            Log($"connecting to {bleAddress} for handshake with {peerId}");
            OnStatus?.Invoke(this, "Connecting over Bluetooth...");
            if (expectIncomingLink) await DisconnectOthersAsync(conn, devPath);
            await WaitForLinkIdleAsync(conn, devPath, bleAddress);
            var live = await ResolveLiveAddressAsync(peerId, bleAddress);
            if (!string.Equals(live, bleAddress, StringComparison.OrdinalIgnoreCase))
            {
                Log($"peer {peerId} moved {bleAddress} -> {live} before Connect");
                bleAddress = live;
                devPath = BlueZDBus.DevicePath(_adapterPath, bleAddress);
                _clientDevPath = devPath;
            }
            await EnsureConnectedAsync(conn, devPath);
            var chars = await FindCharsAsync(conn, devPath);
            if (!chars.TryGetValue(NexShareBleUuids.CharHandshake, out var hsPath))
            {
                Fail(peerId, "peer has no 0xDDEE handshake characteristic");
                return (false, false);
            }
            var mtu = 23;
            try { mtu = BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, hsPath, BlueZDBus.GattChar1, "MTU")).GetUInt16(); }
            catch { /* BlueZ < 5.62: no MTU property */ }
            Log($"handshake char {hsPath} MTU={mtu}");

            NexShareBleHandshakeSession? session = null;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watch = await conn.WatchSignalAsync(BlueZDBus.Bus, hsPath, BlueZDBus.Props, "PropertiesChanged",
                (Message m, object? _) =>
                {
                    var r = m.GetBodyReader();
                    var iface = r.ReadString();
                    var changed = r.ReadDictionaryOfStringToVariantValue();
                    if (iface != BlueZDBus.GattChar1 || !changed.TryGetValue("Value", out var v)) return null;
                    v = BlueZDBus.Unwrap(v);
                    return v.Type == VariantValueType.Array ? v.GetArray<byte>() : null;
                },
                (Notification<byte[]?> n) => { if (n.HasValue && n.Value != null) session?.OnFrame(n.Value); },
                ObserverFlags.None, false, null);

            session = new NexShareBleHandshakeSession(_crypto, _myId, isInitiator: true, isTrusted: id => TrustPredicate?.Invoke(id) == true,
                sendMessage: bytes =>
                {
                    var frames = NexShareBleFraming.Split(bytes, mtu);
                    _ = Task.Run(async () =>
                    {
                        await sendLock.WaitAsync();
                        try
                        {
                            foreach (var f in frames)
                            {
                                if (session!.IsFinished && !IsTerminalFrame(bytes)) return;
                                await BlueZDBus.WriteValueAsync(conn, hsPath, f);
                            }
                        }
                        catch (Exception ex) { session!.Fail("write failed: " + ex.Message); }
                        finally { sendLock.Release(); }
                    });
                },
                closeLink: () => done.TrySetResult())
            { PeerIdHint = peerId, PendingLink = link, ExpectIncomingLink = expectIncomingLink };
            session.LinkDelivered += s => { delivered = true; Log($"ble_url acked by {s.PeerId}"); };
            session.LinkReceived += (_, _) => delivered = true;
            Wire(session, null);
            _clientSession = session;

            await BlueZDBus.CallAsync(conn, hsPath, BlueZDBus.GattChar1, "StartNotify");
            session.Start();
            OnStatus?.Invoke(this, "Bluetooth handshake started");

            // Overall budget: SAS timeout + credentials wait (GO needs time to create the group).
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(SasPrompt.TimeoutSeconds + 60)));
            if (finished != done.Task) session.Fail("BLE handshake timeout");
            await Task.Delay(delivered ? 50 : 200); // let sas_reject / last writes flush
            return (session.IsVerified, delivered);
        }
        catch (Exception ex)
        {
            if (_clientSession is { IsFinished: false } s) s.Fail("BLE: " + ex.Message);
            else Fail(peerId, "BLE: " + ex.Message);
            return (_clientSession?.IsVerified == true, delivered);
        }
        finally
        {
            watch?.Dispose();
            if (string.Equals(_clientDevPath, devPath, StringComparison.Ordinal)) _clientDevPath = null;
            // Drop the unpaired RPA so the next send does not Connect a zombie Device1.
            _ = ForgetDeviceAsync(conn, devPath);
            _linkLock.Release();
        }
    }

    private static bool IsTerminalFrame(byte[] message) =>
        Encoding.UTF8.GetString(message).Contains("\"sas_reject\"");

    private volatile bool _quietFail;
    private volatile string? _lastFailReason;

    private static bool IsTransientBleError(string reason)
    {
        var r = reason.ToLowerInvariant();
        return r.Contains("br-connection") || r.Contains("le-connection") || r.Contains("not connected")
            || r.Contains("in progress") || r.Contains("connection") || r.Contains("timeout") || r.Contains("timed out")
            || r.Contains("unknownobject") || r.Contains("does not exist") || r.Contains("doesn't exist")
            || r.Contains("no 0xddee");
    }

    /// <summary>
    /// Reuse the info-exchange GATT link when it is still up. Tearing it down then Connect-ing again
    /// is what made the first send after launch hit UnknownObject (BlueZ drops unpaired LE Device1).
    /// </summary>
    private async Task WaitForLinkIdleAsync(DBusConnection conn, string devPath, string bleAddress)
    {
        if (_clientSession is { IsFinished: false }) return;
        try
        {
            var connected = BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "Connected")).GetBool();
            if (connected) Log($"reusing existing BLE link to {bleAddress}");
        }
        catch (Exception ex) when (BlueZDBus.IsUnknownObject(ex)) { /* EnsureConnected waits for advert */ }
        catch { }
        await Task.CompletedTask;
    }

    private void Fail(string? peerId, string reason)
    {
        _lastFailReason = reason;
        if (_quietFail) { Log("handshake attempt failed: " + reason); return; }
        Log("handshake failed: " + reason);
        OnHandshakeFailed?.Invoke(this, new BleHandshakeFailedEventArgs { PeerId = peerId, Reason = reason });
    }

    private void Wire(NexShareBleHandshakeSession s, string? peerName)
    {
        s.SasRequested += (pid, code, confirm, reject) =>
        {
            Log($"SAS for {pid}: {code}");
            OnSasRequested?.Invoke(this, new SasPrompt(pid, peerName, code, confirm, reject));
        };
        s.Verified += sess =>
        {
            Log($"verified with {sess.PeerId} (initiator={sess.IsInitiator}, peerCaps=[{string.Join(",", sess.PeerCaps)}], peerIntent={sess.PeerIntent ?? "-"}); "
                + (sess.IsLinkOnly ? "link-only over BLE (no Wi-Fi Direct)" : "waiting for p2p_credentials"));
            OnVerified?.Invoke(this, new BleVerifiedEventArgs
            {
                PeerId = sess.PeerId!, SessionKey = sess.SessionKey!, SessionId = sess.SessionId!,
                PeerCaps = sess.PeerCaps, IsInitiator = sess.IsInitiator, IsLinkOnly = sess.IsLinkOnly
            });
        };
        s.CredentialsReceived += (sess, creds) =>
        {
            if (sess.ExpectIncomingLink)
            {
                // Phone reused a GATT session without pendingPullLink and sent WFD creds. Joining that group
                // fails across Wi-Fi; drop the link so the phone can retry (direct connect to our GATT server).
                Log($"ignoring p2p_credentials on link-pull session from {sess.PeerId}");
                sess.Fail("unexpected p2p_credentials on link pull");
                return;
            }
            Log($"p2p_credentials from {sess.PeerId}: ssid={creds.Ssid} passphrase=<redacted {creds.Passphrase.Length} chars> goIp={creds.GoIp} port={creds.Port}");
            OnCredentials?.Invoke(this, new BleCredentialsEventArgs
            {
                PeerId = sess.PeerId!, Credentials = creds, SessionKey = sess.SessionKey!, SessionId = sess.SessionId!
            });
        };
        s.LinkReceived += (sess, link) =>
        {
            Log($"ble_url from {sess.PeerId}: {link.Url}");
            OnLinkReceived?.Invoke(this, new BleLinkReceivedEventArgs { PeerId = sess.PeerId!, Link = link });
        };
        s.Failed += (pid, reason) => Fail(pid, reason);
    }

    // ---------------------------------------------------------------- peripheral: responder

    private sealed class InfoWriteBuffer
    {
        public byte[] Data = Array.Empty<byte>();
        public CancellationTokenSource? Debounce;
    }

    private void OnInfoWrite(string devPath, byte[] value, int offset)
    {
        var buf = _infoWrites.GetOrAdd(devPath, _ => new InfoWriteBuffer());
        lock (buf)
        {
            // Long/prepared writes arrive as several WriteValue calls with increasing offsets.
            if (offset == 0) buf.Data = value;
            else
            {
                var n = new byte[Math.Max(buf.Data.Length, offset + value.Length)];
                Buffer.BlockCopy(buf.Data, 0, n, 0, Math.Min(buf.Data.Length, offset));
                Buffer.BlockCopy(value, 0, n, offset, value.Length);
                buf.Data = n;
            }
            buf.Debounce?.Cancel();
            var cts = buf.Debounce = new CancellationTokenSource();
            _ = Task.Delay(300, cts.Token).ContinueWith(async t =>
            {
                if (t.IsCanceled) return;
                byte[] data;
                lock (buf) data = buf.Data;
                await HandleInfoWriteAsync(devPath, data);
            }, TaskScheduler.Default);
        }
    }

    private async Task HandleInfoWriteAsync(string devPath, byte[] data)
    {
        var payload = Encoding.UTF8.GetString(data);
        Log("GATT Server: received write from peer: " + payload);
        var info = NexShareBleInfoCodec.Parse(payload);
        if (info == null || info.DeviceId == _myId) return;
        var conn = _conn;
        string addr = devPath[(devPath.LastIndexOf("dev_", StringComparison.Ordinal) + 4)..].Replace('_', ':');
        var name = "NexShare BLE";
        if (conn != null)
        {
            try { name = BlueZDBus.Unwrap(await BlueZDBus.GetPropertyAsync(conn, devPath, BlueZDBus.Device1, "Alias")).GetString(); } catch { }
        }
        Publish(info, name, addr);
    }

    private NexShareBleHandshakeSession ServerSessionFor(string devPath)
    {
        if (_serverSessions.TryGetValue(devPath, out var s) && !s.IsFinished) return s;
        NexShareBleHandshakeSession? sess = null;
        sess = new NexShareBleHandshakeSession(_crypto, _myId, isInitiator: false, isTrusted: id => TrustPredicate?.Invoke(id) == true,
            sendMessage: bytes => EnqueueNotify(NexShareBleFraming.Split(bytes, _serverMtu.TryGetValue(devPath, out var m) ? m : 23)),
            closeLink: () => _serverSessions.TryRemove(new KeyValuePair<string, NexShareBleHandshakeSession>(devPath, sess!)));
        sess.AckFlushDelay = TimeSpan.FromMilliseconds(80); // ack notify then drop so the phone can advertise again
        Wire(sess, null);
        _serverSessions[devPath] = sess;
        Log($"GATT Server: new responder session for {devPath}");
        // Watchdogs: an unfinished responder session blocks the exchange loop (no rediscovery) and keeps the LE
        // link up (phone stops advertising) -> "not found" on both sides. Never let one linger.
        sess.Verified += s =>
        {
            if (!s.IsLinkOnly) return;
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ =>
            {
                if (!s.IsFinished) s.Fail("ble_url not received within 15 s");
            }, TaskScheduler.Default);
        };
        _ = Task.Delay(TimeSpan.FromSeconds(SasPrompt.TimeoutSeconds + 30)).ContinueWith(_ =>
        {
            if (!sess.IsFinished) sess.Fail("responder session timeout");
        }, TaskScheduler.Default);
        sess.Finished += s2 => { _ = DropServerLinkLaterAsync(devPath); };
        return sess;
    }

    /// <summary>After a responder session ends, drop the LE link (unless something else uses it) and any queued notifications.</summary>
    private async Task DropServerLinkLaterAsync(string devPath)
    {
        // Wait until queued notifications (ble_url_ack / sas_reject) are emitted, max ~1 s, then disconnect at once.
        for (var i = 0; i < 20; i++)
        {
            bool empty;
            lock (_notifyQueue) empty = _notifyQueue.Count == 0;
            if (empty) break;
            await Task.Delay(50);
        }
        await Task.Delay(40);
        if (_serverSessions.TryGetValue(devPath, out var s) && !s.IsFinished) return;
        if (_clientSession is { IsFinished: false }) return;
        lock (_notifyQueue) _notifyQueue.Clear();
        _lastServerActivityTicks = 0; // do not let "GATT server in use" keep any link after the session
        var conn = _conn;
        if (conn == null || devPath == "unknown") return;
        Log($"GATT Server: responder session for {devPath} ended; disconnecting link");
        try { await BlueZDBus.CallAsync(conn, devPath, BlueZDBus.Device1, "Disconnect"); } catch { }
    }

    private void EnqueueNotify(List<byte[]> frames)
    {
        lock (_notifyQueue) foreach (var f in frames) _notifyQueue.Enqueue(f);
        _ = PumpNotifyAsync();
    }

    private async Task PumpNotifyAsync()
    {
        if (!await _notifyLock.WaitAsync(0)) return;
        try
        {
            while (_notifying)
            {
                byte[]? frame;
                lock (_notifyQueue) frame = _notifyQueue.Count > 0 ? _notifyQueue.Dequeue() : null;
                if (frame == null) break;
                EmitValueChanged(frame);
                await Task.Delay(15); // pace notifications; BlueZ queues them but phones drop bursts
            }
        }
        finally { _notifyLock.Release(); }
        bool more;
        lock (_notifyQueue) more = _notifyQueue.Count > 0;
        if (more && _notifying) _ = PumpNotifyAsync();
    }

    private void EmitValueChanged(byte[] value)
    {
        var conn = _conn;
        if (conn == null) return;
        var w = conn.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(null, HsPath, BlueZDBus.Props, "PropertiesChanged", "sa{sv}as");
            w.WriteString(BlueZDBus.GattChar1);
            w.WriteDictionary(new Dictionary<string, VariantValue> { ["Value"] = VariantValue.Array(value) });
            w.WriteArray(Array.Empty<string>());
            conn.TrySendMessage(w.CreateMessage());
        }
        finally { w.Dispose(); }
    }

    private bool _advIncludeName = true;

    private Dictionary<string, VariantValue>? PropertiesOf(string path, string iface)
    {
        switch (path)
        {
            case AdvPath when iface == "org.bluez.LEAdvertisement1":
                var d = new Dictionary<string, VariantValue>
                {
                    ["Type"] = VariantValue.String("peripheral"),
                    ["ServiceUUIDs"] = VariantValue.Array(new[] { NexShareBleUuids.Service }),
                    ["ServiceData"] = new Dict<string, VariantValue>
                    {
                        { NexShareBleUuids.Service, VariantValue.Array(Encoding.UTF8.GetBytes(NexShareBleUuids.MagicWord)) }
                    }.AsVariantValue(),
                    ["ManufacturerData"] = new Dict<ushort, VariantValue>
                    {
                        { NexShareBleUuids.ManufacturerId, VariantValue.Array(NexShareBleInfoCodec.ManufacturerBytes(_myId)) }
                    }.AsVariantValue(),
                };
                // The 31-byte primary payload is nearly full (flags+UUID+service data+manufacturer data = 27 B);
                // BlueZ puts the local name into the scan response like Android's setIncludeDeviceName(true).
                if (_advIncludeName) d["LocalName"] = VariantValue.String(_myName.Length > 29 ? _myName[..29] : _myName);
                return d;
            case SvcPath when iface == "org.bluez.GattService1":
                return new()
                {
                    ["UUID"] = VariantValue.String(NexShareBleUuids.Service),
                    ["Primary"] = VariantValue.Bool(true),
                };
            case InfoReadPath when iface == BlueZDBus.GattChar1:
                return CharProps(NexShareBleUuids.CharInfoRead, "read");
            case InfoWritePath when iface == BlueZDBus.GattChar1:
                return CharProps(NexShareBleUuids.CharInfoWrite, "write", "write-without-response");
            case HsPath when iface == BlueZDBus.GattChar1:
                var p = CharProps(NexShareBleUuids.CharHandshake, "write", "notify");
                p["Notifying"] = VariantValue.Bool(_notifying);
                return p;
        }
        return null;
    }

    private static Dictionary<string, VariantValue> CharProps(string uuid, params string[] flags) => new()
    {
        ["UUID"] = VariantValue.String(uuid),
        ["Service"] = VariantValue.ObjectPath(new ObjectPath(SvcPath)),
        ["Flags"] = VariantValue.Array(flags),
    };

    private static readonly (string Path, string Iface)[] GattObjects =
    {
        (SvcPath, "org.bluez.GattService1"),
        (InfoReadPath, BlueZDBus.GattChar1),
        (InfoWritePath, BlueZDBus.GattChar1),
        (HsPath, BlueZDBus.GattChar1),
    };

    private static (string? Device, int Mtu, int Offset) ParseOptions(Dictionary<string, VariantValue> o)
    {
        string? dev = null; int mtu = 0, off = 0;
        if (o.TryGetValue("device", out var d)) { d = BlueZDBus.Unwrap(d); if (d.Type == VariantValueType.ObjectPath) dev = d.GetObjectPathAsString(); }
        if (o.TryGetValue("mtu", out var m)) { m = BlueZDBus.Unwrap(m); if (m.Type == VariantValueType.UInt16) mtu = m.GetUInt16(); }
        if (o.TryGetValue("offset", out var f)) { f = BlueZDBus.Unwrap(f); if (f.Type == VariantValueType.UInt16) off = f.GetUInt16(); }
        return (dev, mtu, off);
    }

    /// <summary>Serves our exported objects (ObjectManager, Properties, GattCharacteristic1, LEAdvertisement1).</summary>
    private sealed class ObjectHandler : IPathMethodHandler
    {
        private readonly BlueZBleService _s;
        public ObjectHandler(BlueZBleService s) { _s = s; }
        public string Path => Root;
        public bool HandlesChildPaths => true;

        public ValueTask HandleMethodAsync(MethodContext ctx)
        {
            try { Handle(ctx); }
            catch (Exception ex)
            {
                Log($"handler error {ctx.Request.MemberAsString}: {ex.Message}");
                if (!ctx.ReplySent && !ctx.NoReplyExpected) ctx.ReplyError("org.bluez.Error.Failed", ex.Message);
            }
            return default;
        }

        private void Handle(MethodContext ctx)
        {
            var req = ctx.Request;
            var path = req.PathAsString ?? "";
            var iface = req.InterfaceAsString ?? "";
            var member = req.MemberAsString ?? "";

            if (iface == BlueZDBus.ObjMgr && member == "GetManagedObjects" && path == Root)
            {
                var w = ctx.CreateReplyWriter("a{oa{sa{sv}}}");
                try
                {
                    var outer = w.WriteDictionaryStart();
                    foreach (var (p, i) in GattObjects)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteObjectPath(p);
                        var mid = w.WriteDictionaryStart();
                        w.WriteDictionaryEntryStart();
                        w.WriteString(i);
                        w.WriteDictionary(_s.PropertiesOf(p, i)!);
                        w.WriteDictionaryEnd(mid);
                    }
                    w.WriteDictionaryEnd(outer);
                    ctx.Reply(w.CreateMessage());
                }
                finally { w.Dispose(); }
                return;
            }

            if (iface == BlueZDBus.Props && (member == "GetAll" || member == "Get"))
            {
                var r = req.GetBodyReader();
                var targetIface = r.ReadString();
                var props = _s.PropertiesOf(path, targetIface);
                if (props == null) { ctx.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "No such interface"); return; }
                if (member == "GetAll")
                {
                    var w = ctx.CreateReplyWriter("a{sv}");
                    try { w.WriteDictionary(props); ctx.Reply(w.CreateMessage()); } finally { w.Dispose(); }
                }
                else
                {
                    var name = r.ReadString();
                    if (!props.TryGetValue(name, out var v)) { ctx.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "No such property"); return; }
                    var w = ctx.CreateReplyWriter("v");
                    try { w.WriteVariant(v); ctx.Reply(w.CreateMessage()); } finally { w.Dispose(); }
                }
                return;
            }

            if (iface == "org.bluez.LEAdvertisement1" && member == "Release")
            {
                Log("advertisement released by BlueZ");
                _s._advRegistered = false;
                ReplyEmpty(ctx);
                return;
            }

            if (iface == BlueZDBus.GattChar1)
            {
                switch (member)
                {
                    case "ReadValue" when path == InfoReadPath:
                    {
                        _s.MarkServerActivity();
                        var r = req.GetBodyReader();
                        var (dev, mtu, off) = ParseOptions(r.ReadDictionaryOfStringToVariantValue());
                        if (dev != null && mtu > 0) _s._serverMtu[dev] = mtu;
                        var full = Encoding.UTF8.GetBytes(_s.InfoPayload);
                        var slice = off >= full.Length ? Array.Empty<byte>() : full[off..];
                        var w = ctx.CreateReplyWriter("ay");
                        try { w.WriteArray(slice); ctx.Reply(w.CreateMessage()); } finally { w.Dispose(); }
                        return;
                    }
                    case "WriteValue" when path == InfoWritePath || path == HsPath:
                    {
                        var r = req.GetBodyReader();
                        var value = r.ReadArrayOfByte();
                        var (dev, mtu, off) = ParseOptions(r.ReadDictionaryOfStringToVariantValue());
                        dev ??= "unknown";
                        _s.MarkServerActivity();
                        if (mtu > 0) _s._serverMtu[dev] = mtu;
                        _s._serverPeersSeen[dev] = DateTimeOffset.UtcNow;
                        if (!ctx.NoReplyExpected) ReplyEmpty(ctx);
                        if (path == InfoWritePath) _s.OnInfoWrite(dev, value, off);
                        else _s.ServerSessionFor(dev).OnFrame(value);
                        return;
                    }
                    case "StartNotify" when path == HsPath:
                        _s.MarkServerActivity();
                        _s._notifying = true;
                        Log("GATT Server: notifications enabled on 0xDDEE");
                        ReplyEmpty(ctx);
                        _ = _s.PumpNotifyAsync();
                        return;
                    case "StopNotify" when path == HsPath:
                        _s._notifying = false;
                        ReplyEmpty(ctx);
                        return;
                }
                ctx.ReplyError("org.bluez.Error.NotSupported", member + " not supported on " + path);
                return;
            }

            ctx.ReplyUnknownMethodError();
        }

        private static void ReplyEmpty(MethodContext ctx)
        {
            var w = ctx.CreateReplyWriter(null!);
            try { ctx.Reply(w.CreateMessage()); } finally { w.Dispose(); }
        }
    }
}
