using System;
using System.Text.Json;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Application.Services;

public class NexShareOrchestrator : INexShareOrchestrator
{
    private readonly INexShareCrypto _crypto;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NexZen.Core.NexShare.Crypto.EnvelopeCipher> _e2eByPeerId = new();

    /// <summary>
    /// Registers a LAN/QR E2E session (Android E2eContext: key = HKDF(token, sessionId), AAD tag = sessionId)
    /// so encrypted text from <paramref name="peerId"/> can be decrypted.
    /// </summary>
    public void RegisterE2eSession(string peerId, string sessionId, string token, int cryptoVersion = 2)
    {
        var key = _crypto.DeriveLanSessionKey(sessionId, token, cryptoVersion);
        _e2eByPeerId[peerId] = new NexZen.Core.NexShare.Crypto.EnvelopeCipher(_crypto, key, sessionId);
    }

    /// <summary>Registers an already-derived key (e.g. a Wi-Fi Direct session key with its hex sessionId).</summary>
    public void RegisterE2eSession(string peerId, byte[] key, string aadTag) =>
        _e2eByPeerId[peerId] = new NexZen.Core.NexShare.Crypto.EnvelopeCipher(_crypto, key, aadTag);

    public void ClearE2eSession(string peerId) => _e2eByPeerId.TryRemove(peerId, out _);
    private readonly INexShareDiscovery _discovery;
    private readonly INexShareBleService? _ble;
    private bool _bleWired;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WireP2pCredentials> _p2pCredentials = new();
    public System.Collections.Generic.IReadOnlyDictionary<string, WireP2pCredentials> ReceivedP2pCredentials => _p2pCredentials;
    public event EventHandler<SasPrompt>? OnSasRequested;
    private readonly INexShareTransportServer _server;
    private INexShareTransport? _currentConnection;
    private readonly System.Threading.SemaphoreSlim _connectLock = new(1, 1);
    private bool _isConnecting;
    private bool _started; // Guard against double-start
    private readonly System.Threading.SemaphoreSlim _lifecycleLock = new(1, 1);
    private System.Threading.CancellationTokenSource _broadcastCts = new();
    private int _dbgTextReceiveCount = 0; // Debug counter

    private byte[]? _myPublicKey;
    private System.Security.Cryptography.ECDiffieHellman? _myPrivateKey;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileOfferMessage> _pendingFileOffers = new();

    /// <summary>Persistent across launches (app data) so phones keep recognising/trusting this PC.</summary>
    public string MyDeviceId { get; } = NexZen.Core.NexShare.NexShareLocalStore.Shared.DeviceId;
    private readonly NexZen.Core.NexShare.NexShareLocalStore _store = NexZen.Core.NexShare.NexShareLocalStore.Shared;
    private readonly IWifiDirectJoiner? _wfdJoiner;
    /// <summary>Android WfdPreVerified: peers SAS-verified over BLE, consumed once by the next WFD TCP link.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _wfdPreVerified = new();
    private NexZen.Core.NexShare.Transport.TcpFramedTransport? _wfdTransport;
    private int _wfdBusy;
    private readonly NexZen.Core.NexShare.NexShareDeviceRegistry _registry = new();
    /// <summary>Peers whose LAN connect failed (Android lanFailedIds): next attempt goes BLE -> Wi-Fi Direct.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _lanFailed = new();
    /// <summary>Bumped on every route failure / refresh so pending waits (queued url_offer) give up early.</summary>
    private int _connectEpoch;
    private int _refreshInFlight;
    public event EventHandler<bool>? OnRefreshed;
    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private volatile bool _wfdJoined;
    public string MyDeviceName { get; private set; } = "NexZen Desktop";

    public event EventHandler<DiscoveryMessage>? OnDeviceFound;
    public event EventHandler<string>? OnTextMessageReceived;
    public event EventHandler<FileOfferMessage>? OnFileOfferReceived;
    public event EventHandler<UrlOfferMessage>? OnUrlOfferReceived;
    public event EventHandler<string>? OnConnectionStatusChanged;
    public event EventHandler<PeerLinkState>? OnPeerConnectionChanged;

    private string? _connectedDeviceId;
    private string? _connectedHost;
    private int _helloAnnounced;
    private static readonly JsonSerializerOptions HelloJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public NexShareOrchestrator(
        INexShareCrypto crypto,
        INexShareDiscovery discovery,
        INexShareTransportServer server,
        INexShareBleService? ble = null,
        IWifiDirectJoiner? wfdJoiner = null)
    {
        _ble = ble;
        _wfdJoiner = wfdJoiner;
        if (_ble != null) _ble.TrustPredicate = id => _store.IsTrusted(id);
        _crypto = crypto;
        _discovery = discovery;
        _server = server;
    }

    public async Task StartAsync(string deviceName)
    {
        if (NexShareHost.IsPreviewer)
        {
            Console.WriteLine("[NexShare] Not starting inside the Avalonia previewer.");
            return;
        }

        await _lifecycleLock.WaitAsync();
        try
        {
            if (_started)
            {
                Console.WriteLine("[NexShare] StartAsync called again while already running — ignoring.");
                return;
            }
            _started = true;
            MyDeviceName = deviceName;

            try
            {
                var keys = _crypto.GenerateKeyPair();
                _myPrivateKey = keys.PrivateKey;
                _myPublicKey = keys.PublicKey;

                _server.OnClientConnected -= Server_OnClientConnected;
                _server.OnClientConnected += Server_OnClientConnected;
                try
                {
                    await _server.StartAsync(43000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NexShare] Warning: Failed to start WebSocket Server: {ex.Message}. Outbound connections will still work.");
                }

                _discovery.OnDeviceDiscovered -= Discovery_OnDeviceDiscovered;
                _discovery.OnDeviceDiscovered += Discovery_OnDeviceDiscovered;
                await _discovery.StartListeningAsync();

                if (_ble != null)
                {
                    WireBle();
                    _ble.Configure(MyDeviceId, MyDeviceName, 43000);
                    try { await _ble.StartListeningAsync(); }
                    catch (Exception ex) { Console.WriteLine($"[NexShare] BLE start failed: {ex.Message}"); }
                }

                try { _broadcastCts.Cancel(); } catch { }
                _broadcastCts = new System.Threading.CancellationTokenSource();
                _ = BroadcastPresenceLoopAsync(_broadcastCts.Token);

                OnConnectionStatusChanged?.Invoke(this, "Started listening on port " + _server.Port);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NexShare] Start failed: {ex}");
                await StopCoreAsync();
                OnConnectionStatusChanged?.Invoke(this, "Error starting NexShare");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void SetDeviceName(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
            MyDeviceName = deviceName.Trim();
        _ble?.Configure(MyDeviceId, MyDeviceName, 43000);
    }

    private void WireBle()
    {
        if (_bleWired || _ble == null) return;
        _bleWired = true;
        _ble.OnDeviceDiscovered += Discovery_OnDeviceDiscovered;
        _ble.OnSasRequested += (_, e) => OnSasRequested?.Invoke(this, e);
        _ble.OnStatus += (_, e) => Console.WriteLine($"[NexShare] BLE: {e}");
        _ble.OnVerified += (_, e) =>
        {
            // Keep the WFD session key for the Wi-Fi Direct TCP link (Phase 3).
            RegisterE2eSession(e.PeerId, e.SessionKey, e.SessionId);
            _wfdPreVerified[e.PeerId] = 1;
            _store.AddTrusted(e.PeerId);
            OnConnectionStatusChanged?.Invoke(this, e.IsLinkOnly
                ? "Verified over Bluetooth, sending the link..."
                : "Verified over Bluetooth. Waiting for Wi-Fi Direct details...");
        };
        _ble.OnLinkReceived += (_, e) =>
        {
            var name = _registry.Get(e.PeerId)?.RoutableView(NowMs())?.DeviceName;
            var offer = new UrlOfferMessage
            {
                Url = e.Link.Url,
                Title = e.Link.Title,
                DeviceName = string.IsNullOrWhiteSpace(e.Link.DeviceName) ? (name ?? "") : e.Link.DeviceName
            };
            Console.WriteLine($"[ORCH] url received over BLE from {e.PeerId}: {offer.Url}");
            OnUrlOfferReceived?.Invoke(this, offer);
        };
        _ble.OnCredentials += (_, e) =>
        {
            _p2pCredentials[e.PeerId] = e.Credentials;
            Console.WriteLine($"[NexShare] Stored p2p_credentials for {e.PeerId}: ssid={e.Credentials.Ssid} passphrase=<redacted> goIp={e.Credentials.GoIp} port={e.Credentials.Port}");
            OnConnectionStatusChanged?.Invoke(this, $"Received Wi-Fi Direct details ({e.Credentials.Ssid}).");
            _ = JoinWifiDirectAsync(e.PeerId, e.Credentials);
        };
        _ble.OnHandshakeFailed += (_, e) =>
        {
            System.Threading.Interlocked.Increment(ref _connectEpoch);
            OnConnectionStatusChanged?.Invoke(this, $"Bluetooth pairing failed: {e.Reason}");
        };
    }

    private async Task BroadcastPresenceLoopAsync(System.Threading.CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Pause broadcasting if we are already connected to a device.
                // This prevents spamming UDP packets which triggers a listener duplication bug on Android.
                if (_currentConnection == null)
                {
                    var msg = new DiscoveryMessage
                    {
                        DeviceId = MyDeviceId,
                        DeviceName = MyDeviceName,
                        TcpPort = _server?.Port ?? 0,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                    
                    await _discovery.BroadcastPresenceAsync(msg);
                }
                
                await Task.Delay(3000, ct); // Broadcast every 3 seconds
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private void Discovery_OnDeviceDiscovered(object? sender, DiscoveryMessage e)
    {
        // Our own broadcast comes back on the LAN. It is this PC, not a peer.
        if (e.DeviceId == MyDeviceId || (!e.ViaBle && IsOwnHost(e.Host)))
            return;
        if (!string.IsNullOrEmpty(e.DeviceId))
        {
            var entry = _registry.Upsert(e, NowMs());
            if (!string.IsNullOrEmpty(e.Host)) _lanFailed.TryRemove(e.DeviceId, out _); // fresh LAN sighting: allow LAN again later
            _ = entry;
        }
        OnDeviceFound?.Invoke(this, e);
    }

    private bool IsOwnIdentity(string? deviceId, string? deviceName)
    {
        if (!string.IsNullOrEmpty(deviceId) && string.Equals(deviceId, MyDeviceId, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.IsNullOrEmpty(deviceId)
            && !string.IsNullOrEmpty(deviceName)
            && string.Equals(deviceName, MyDeviceName, StringComparison.Ordinal);
    }

    private static bool IsOwnHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        if (!System.Net.IPAddress.TryParse(host, out var address))
            return false;
        if (System.Net.IPAddress.IsLoopback(address))
            return true;
        try
        {
            foreach (var local in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
            {
                if (local.Equals(address))
                    return true;
            }
        }
        catch { }
        return false;
    }

    private void Server_OnClientConnected(object? sender, INexShareTransport client)
    {
        Console.WriteLine($"[ORCH] Server_OnClientConnected: new inbound from {client.RemoteIpAddress}. _currentConnection={(_currentConnection == null ? "NULL" : $"{_currentConnection.GetHashCode():X} (IsConnected={_currentConnection.IsConnected})")}");

        if (IsOwnHost(client.RemoteIpAddress))
        {
            Console.WriteLine("[NexShare] Ignoring a connection from this PC.");
            client.Dispose();
            return;
        }

        // If we already have a live connection, reject the duplicate inbound connection.
        // IMPORTANT: if the existing connection is dead (outbound to Android that timed out),
        // we must accept the new inbound connection — otherwise Android's url_offer / file_offer
        // will always fail because the PC rejects the WebSocket that Android opened to send it.
        if (_currentConnection != null && _currentConnection.IsConnected)
        {
            Console.WriteLine("[ORCH] Server_OnClientConnected: LIVE connection exists — rejecting new inbound.");
            client.Dispose();
            return;
        }

        if (_currentConnection != null)
        {
            // Previous connection is dead — clean it up before accepting the new one.
            Console.WriteLine($"[ORCH] Server_OnClientConnected: stale/dead connection {_currentConnection.GetHashCode():X} — replacing with new inbound.");
            _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
            _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
            _currentConnection = null;
            _isConnecting = false;
        }

        Console.WriteLine($"[ORCH] Server_OnClientConnected: ACCEPTING inbound from {client.RemoteIpAddress}.");
        System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
        SetCurrentConnection(client);
        // Read first. The phone sends HELLO and ignores every later frame until
        // we answer, so the answer goes out after that HELLO is in hand.
        _ = client.StartReceiveLoopAsync();
        PublishPeer(true, null, client.RemoteIpAddress);
        OnConnectionStatusChanged?.Invoke(this, "Client connected");
    }

    private async Task SendHelloAsync(INexShareTransport transport)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _helloAnnounced, 1, 0) != 0)
            return;
        var wireDevice = new WireDevice
        {
            DeviceId = MyDeviceId,
            DeviceName = MyDeviceName,
            DeviceType = "pc"
        };
        var env = new WireEnvelope
        {
            Type = NexShareMessageTypes.Hello,
            SenderId = MyDeviceId,
            Payload = JsonSerializer.Serialize(wireDevice, HelloJson)
        };
        Console.WriteLine($"[NexShare] Sending HELLO name={MyDeviceName} id={MyDeviceId}");
        try
        {
            await transport.SendMessageAsync(env);
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
            Console.WriteLine($"[NexShare] HELLO send failed: {ex.Message}");
        }
    }

    private readonly object _connectionLock = new();

    private void SetCurrentConnection(INexShareTransport connection)
    {
        lock (_connectionLock)
        {
            // If this exact same object is already the current connection, don't re-subscribe
            if (ReferenceEquals(_currentConnection, connection))
            {
                Console.WriteLine($"[NexShare] SetCurrentConnection: same transport re-submitted, skipping.");
                return;
            }

            // Detach old handlers from previous connection
            if (_currentConnection != null)
            {
                _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
                _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
            }

            _currentConnection = connection;
            _currentConnection.OnMessageReceived += Client_OnMessageReceived;
            _currentConnection.OnDisconnected += OnCurrentConnectionDisconnected;

            Console.WriteLine($"[NexShare] SetCurrentConnection: transport={connection.GetHashCode():X}");
        }
    }

    private void OnCurrentConnectionDisconnected(object? sender, EventArgs e)
    {
        Console.WriteLine($"[ORCH] OnCurrentConnectionDisconnected fired. Clearing _currentConnection ({(_currentConnection == null ? "null" : _currentConnection.GetHashCode().ToString("X"))}).");
        lock (_connectionLock)
        {
            if (_currentConnection != null)
            {
                _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
                _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
            }
            _currentConnection = null;
            _isConnecting = false;
        }
        PublishPeer(false, null, null);
        System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
        OnConnectionStatusChanged?.Invoke(this, "Disconnected");
    }

    public event EventHandler<WireEnvelope>? OnMirrorMessage;

    public string? ConnectedDeviceId => _connectedDeviceId;

    public async Task<bool> ConnectOverTunnelAsync(int localPort, string label)
    {
        await DisconnectCurrentAsync();
        // The old transport's OnDisconnected may not have run yet; ConnectCoreAsync would then think
        // a connection is still current and do nothing.
        lock (_connectionLock)
        {
            if (_currentConnection != null)
            {
                _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
                _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
                _currentConnection = null;
            }
        }
        for (var i = 0; i < 20 && _isConnecting; i++) await Task.Delay(50);
        await ConnectCoreAsync(new DiscoveryMessage
        {
            DeviceId = string.Empty, // learned from the phone's HELLO
            DeviceName = label,
            Host = "127.0.0.1",
            TcpPort = localPort,
        });
        return _currentConnection?.IsConnected == true;
    }

    public async Task<bool> SendEnvelopeAsync(WireEnvelope envelope)
    {
        var connection = _currentConnection;
        if (connection == null || !connection.IsConnected) return false;
        envelope.SenderId = MyDeviceId;
        try
        {
            await connection.SendMessageAsync(envelope);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexShare] send {envelope.Type} failed: {ex.Message}");
            return false;
        }
    }

    private void Client_OnMessageReceived(object? sender, WireEnvelope e)
    {
        if (NexZen.Core.NexShare.Mirror.MirrorMessageTypes.IsMirror(e.Type))
        {
            OnMirrorMessage?.Invoke(this, e);
            return;
        }
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (e.Type == NexShareMessageTypes.PeerRefresh)
        {
            // Android: peer_refresh => full local reset, back to the device list, no auto-reconnect.
            Console.WriteLine($"[NexShare] peer_refresh received from {e.SenderId} -> local reset");
            _ = RefreshCoreAsync(byPeer: true);
            return;
        }
        
        if (e.Type == NexShareMessageTypes.Hello)
        {
            // Android sends WireDevice (deviceId, deviceName, deviceType) as HELLO payload
            try
            {
                var peerDevice = JsonSerializer.Deserialize<WireDevice>(e.Payload, options);
                if (peerDevice != null)
                {
                    var peerId = string.IsNullOrEmpty(peerDevice.DeviceId) ? e.SenderId : peerDevice.DeviceId;
                    // The HELLO we send introduces this PC. Reading it back is not a peer.
                    if (IsOwnIdentity(peerId, peerDevice.DeviceName) || IsOwnIdentity(e.SenderId, peerDevice.DeviceName))
                    {
                        Console.WriteLine("[NexShare] Ignoring our own HELLO.");
                        return;
                    }
                    PublishPeer(true, peerId, _connectedHost);
                    if (!string.IsNullOrWhiteSpace(peerDevice.DeviceName))
                        OnConnectionStatusChanged?.Invoke(this, $"Connected with {peerDevice.DeviceName}");
                    if (sender is INexShareTransport helloTransport)
                        _ = SendHelloAsync(helloTransport);
                }
            }
            catch { /* ignore parse errors in HELLO */ }
        }
        else if (e.Type == NexShareMessageTypes.Text)
        {
            var text = e.Payload;
            if (NexZen.Core.NexShare.Crypto.EnvelopeCipher.LooksEncryptedText(text))
            {
                // Decrypt only when an E2E session context exists for this sender; plaintext LAN is untouched.
                string? plain = null;
                if (!string.IsNullOrEmpty(e.SenderId) && _e2eByPeerId.TryGetValue(e.SenderId, out var cipher))
                    plain = cipher.Decrypt(e)?.Payload;
                text = plain ?? "Encrypted message (no E2E session with this device on PC)";
            }
            
            var callN = System.Threading.Interlocked.Increment(ref _dbgTextReceiveCount);
            Console.WriteLine($"[DEBUG] Client_OnMessageReceived TEXT call #{callN}, sender={sender?.GetHashCode():X}, payload={text}");
            
            OnTextMessageReceived?.Invoke(this, text);

            // Send ACK back
            if (!string.IsNullOrEmpty(e.MessageId) && sender is INexShareTransport transport)
            {
                var ackEnv = new WireEnvelope
                {
                    Type = NexShareMessageTypes.Ack,
                    SenderId = MyDeviceId,
                    AckFor = e.MessageId
                };
                _ = transport.SendMessageAsync(ackEnv);
            }
        }
        else if (e.Type == NexShareMessageTypes.FileOffer)
        {
            try
            {
                var offer = JsonSerializer.Deserialize<FileOfferMessage>(e.Payload, options);
                if (offer != null)
                {
                    _pendingFileOffers[offer.TransferId] = offer;
                    OnFileOfferReceived?.Invoke(this, offer);
                }
            }
            catch { /* Ignore parse errors */ }
        }
        else if (e.Type == NexShareMessageTypes.UrlOffer)
        {
            Console.WriteLine($"[ORCH] ★ url_offer received! Payload={e.Payload}");
            try
            {
                var offer = JsonSerializer.Deserialize<UrlOfferMessage>(e.Payload, options);
                if (offer != null)
                {
                    Console.WriteLine($"[ORCH] url_offer deserialized: url={offer.Url}, title={offer.Title}, device={offer.DeviceName}. Firing OnUrlOfferReceived...");
                    OnUrlOfferReceived?.Invoke(this, offer);
                    Console.WriteLine($"[ORCH] OnUrlOfferReceived fired successfully.");
                }
                else
                {
                    Console.WriteLine($"[ORCH] url_offer deserialized to NULL. Payload was: {e.Payload}");
                }
            }
            catch (Exception urlEx)
            {
                Console.WriteLine($"[ORCH] url_offer parse ERROR: {urlEx.Message} | Payload={e.Payload}");
            }
        }
        else if (e.Type == NexShareMessageTypes.HandoffCreated)
        {
            // Android sends this when direct url_offer WebSocket send fails.
            // The handoff payload carries the URL directly — treat it as a url_offer.
            Console.WriteLine($"[ORCH] ★ handoff_created received! Payload={e.Payload}");
            try
            {
                var handoff = JsonSerializer.Deserialize<HandoffCreatedMessage>(e.Payload, options);
                if (handoff != null && !string.IsNullOrEmpty(handoff.Url))
                {
                    // Check expiry — Android sets expireAt as Unix ms
                    var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (handoff.ExpireAt > 0 && nowMs > handoff.ExpireAt)
                    {
                        Console.WriteLine($"[ORCH] handoff_created EXPIRED (expireAt={handoff.ExpireAt}, now={nowMs}). Ignoring.");
                    }
                    else
                    {
                        var offer = new UrlOfferMessage
                        {
                            Url = handoff.Url,
                            Title = string.IsNullOrEmpty(handoff.Title) ? handoff.Url : handoff.Title,
                            DeviceName = e.SenderId // use senderId as device name fallback
                        };
                        Console.WriteLine($"[ORCH] handoff_created → firing OnUrlOfferReceived: url={offer.Url}, title={offer.Title}");
                        OnUrlOfferReceived?.Invoke(this, offer);
                    }
                }
                else
                {
                    Console.WriteLine($"[ORCH] handoff_created deserialized to NULL or empty URL. Payload={e.Payload}");
                }
            }
            catch (Exception hEx)
            {
                Console.WriteLine($"[ORCH] handoff_created parse ERROR: {hEx.Message} | Payload={e.Payload}");
            }
        }
        else if (e.Type == NexShareMessageTypes.FileAccept)
        {
            try
            {
                var accept = JsonSerializer.Deserialize<FileAcceptMessage>(e.Payload, options);
                if (accept != null)
                {
                    OnConnectionStatusChanged?.Invoke(this, $"Receiver accepted file. Transfer starting...");
                }
            }
            catch { /* Ignore */ }
        }
        else if (e.Type == NexShareMessageTypes.FileCancel)
        {
            try
            {
                var cancel = JsonSerializer.Deserialize<FileCancelMessage>(e.Payload, options);
                OnConnectionStatusChanged?.Invoke(this, $"Receiver rejected the file.");
                // Cleanup server if rejected
                if (cancel != null)
                {
                    _server.RemoveFileOffer(cancel.TransferId);
                }
            }
            catch { /* Ignore */ }
        }
    }

    public async Task ConnectToDeviceAsync(DiscoveryMessage device)
    {
        if (_currentConnection != null && _currentConnection.IsConnected && IsCurrentPeer(device))
            return;

        // Route on the merged, freshness-filtered view (LAN host only if heard in the last 15 s, etc.).
        var entry = _registry.Get(device.DeviceId);
        var view = entry?.RoutableView(NowMs()) ?? device;
        var name = string.IsNullOrWhiteSpace(view.DeviceName) ? view.Host : view.DeviceName;
        var bleOk = _ble != null && _ble.IsAvailable;
        var trusted = _store.IsTrusted(view.DeviceId);
        var route = NexZen.Core.NexShare.NexShareRouting.Choose(view, _lanFailed.ContainsKey(view.DeviceId), trusted, bleOk);
        Console.WriteLine($"[NexShare] route for {name} ({view.DeviceId}): {route} lan={view.Host} ble={view.BleAddress} trusted={trusted} bleOk={bleOk}");

        switch (route)
        {
            case NexZen.Core.NexShare.NexShareRoute.Lan:
                OnConnectionStatusChanged?.Invoke(this, $"Connecting to {name} over LAN...");
                Console.WriteLine($"[NexShare] LAN connect {view.Host}:{view.TcpPort}");
                if (_currentConnection != null)
                    await DisconnectCurrentAsync();
                await ConnectCoreAsync(view);
                if (_currentConnection != null && _currentConnection.IsConnected)
                {
                    _lanFailed.TryRemove(view.DeviceId, out _);
                    OnConnectionStatusChanged?.Invoke(this, $"Connected to {name} over LAN");
                    return;
                }
                _lanFailed[view.DeviceId] = 1;
                if (_linkSendInProgress && bleOk && NexZen.Core.NexShare.NexShareRouting.HasBleLink(view))
                {
                    // Link-only send: SendUrlToDeviceAsync retries over BLE directly (no Wi-Fi Direct).
                    System.Threading.Interlocked.Increment(ref _connectEpoch);
                    OnConnectionStatusChanged?.Invoke(this, "LAN unavailable, switching to Bluetooth...");
                    return;
                }
                if (NexZen.Core.NexShare.NexShareRouting.FallbackAfterLanFailure(view, bleOk) == NexZen.Core.NexShare.NexShareRoute.BleWifiDirect)
                {
                    OnConnectionStatusChanged?.Invoke(this, $"LAN unavailable, switching to Bluetooth → Wi-Fi Direct...");
                    StartBleWfd(view, name);
                    return;
                }
                System.Threading.Interlocked.Increment(ref _connectEpoch);
                OnConnectionStatusChanged?.Invoke(this, $"Couldn't connect to {name} over LAN");
                return;

            case NexZen.Core.NexShare.NexShareRoute.BleWifiDirect:
                StartBleWfd(view, name);
                return;

            default:
                System.Threading.Interlocked.Increment(ref _connectEpoch);
                if (!string.IsNullOrEmpty(view.BleAddress) && !bleOk)
                    OnConnectionStatusChanged?.Invoke(this, "Bluetooth is not available on this computer");
                else if (!string.IsNullOrEmpty(view.BleAddress))
                    OnConnectionStatusChanged?.Invoke(this, $"{name}'s NexShare version does not support Bluetooth pairing");
                else if (NexZen.Core.NexShare.NexShareRouting.HasLan(view))
                    OnConnectionStatusChanged?.Invoke(this, $"{name} is on a Wi-Fi Direct network but is not verified; pair over Bluetooth");
                else
                    OnConnectionStatusChanged?.Invoke(this, $"Lost sight of {name}. Press Reset Connect");
                return;
        }
    }

    private void StartBleWfd(DiscoveryMessage view, string name)
    {
        OnConnectionStatusChanged?.Invoke(this, $"Pairing {name} over Bluetooth → Wi-Fi Direct...");
        _ = _ble!.StartHandshakeAsync(view.BleAddress!, view.DeviceId);
    }

    private bool IsReadyFor(DiscoveryMessage device)
    {
        var c = _currentConnection;
        return c != null && c.IsConnected && IsCurrentPeer(device)
            && System.Threading.Volatile.Read(ref _helloAnnounced) == 1;
    }

    public async Task<bool> ConnectAndWaitAsync(DiscoveryMessage device, TimeSpan timeout)
    {
        if (IsReadyFor(device)) return true;
        var epoch = System.Threading.Volatile.Read(ref _connectEpoch);
        await ConnectToDeviceAsync(device);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsReadyFor(device)) return true;
            if (System.Threading.Volatile.Read(ref _connectEpoch) != epoch) return false;
            await Task.Delay(250);
        }
        return IsReadyFor(device);
    }

    /// <summary>~SAS (60 s) + join (25 s) + TCP retries (20 s).</summary>
    public static readonly TimeSpan PendingSendTimeout = TimeSpan.FromSeconds(110);

    private volatile bool _linkSendInProgress;

    /// <summary>
    /// Link-only send. Route: already-connected peer / LAN -> url_offer on the link; else BLE direct (ble_url, peer
    /// has ble_link; no Wi-Fi Direct, Wi-Fi untouched); else BLE -> Wi-Fi Direct -> url_offer (idle timeout leaves the group).
    /// </summary>
    public async Task<bool> SendUrlToDeviceAsync(DiscoveryMessage device, string url, string title)
    {
        var name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.Host : device.DeviceName;
        if (IsReadyFor(device)) { await SendUrlAsync(url, title); return true; }

        var view = _registry.Get(device.DeviceId)?.RoutableView(NowMs()) ?? device;
        var bleOk = _ble != null && _ble.IsAvailable;
        var trusted = _store.IsTrusted(view.DeviceId);
        var route = NexZen.Core.NexShare.NexShareRouting.ChooseForLink(view, _lanFailed.ContainsKey(view.DeviceId), trusted, bleOk);
        Console.WriteLine($"[NexShare] link route for {name} ({view.DeviceId}): {route}");

        if (route == NexZen.Core.NexShare.NexShareRoute.Lan)
        {
            _linkSendInProgress = true;
            bool ok;
            try { ok = await ConnectAndWaitAsync(device, PendingSendTimeout); }
            finally { _linkSendInProgress = false; }
            if (ok) { await SendUrlAsync(url, title); return true; }
            view = _registry.Get(device.DeviceId)?.RoutableView(NowMs()) ?? device;
            if (!(bleOk && NexZen.Core.NexShare.NexShareRouting.HasBleLink(view)))
            {
                OnConnectionStatusChanged?.Invoke(this, $"Couldn't connect to {name}");
                return false;
            }
            route = NexZen.Core.NexShare.NexShareRoute.BleDirect;
        }

        if (route == NexZen.Core.NexShare.NexShareRoute.BleDirect)
        {
            OnConnectionStatusChanged?.Invoke(this, $"Sending the link to {name} over Bluetooth...");
            var link = new WireBleUrl { Url = url, Title = title, DeviceName = MyDeviceName };
            var epoch = System.Threading.Volatile.Read(ref _connectEpoch);
            var r = await _ble!.SendLinkAsync(view.BleAddress!, view.DeviceId, link);
            Console.WriteLine($"[NexShare] BLE link send to {name}: {r}");
            if (r == BleLinkResult.Delivered)
            {
                OnConnectionStatusChanged?.Invoke(this, $"Sent the link to {name} over Bluetooth");
                return true;
            }
            if (r == BleLinkResult.Failed)
            {
                OnConnectionStatusChanged?.Invoke(this, $"Couldn't send the link to {name} over Bluetooth");
                return false;
            }
            // Peer lacks ble_link: credentials follow on the same handshake; wait for the Wi-Fi Direct link.
            var deadline = DateTime.UtcNow + PendingSendTimeout;
            while (DateTime.UtcNow < deadline && !IsReadyFor(device))
            {
                if (System.Threading.Volatile.Read(ref _connectEpoch) != epoch) break;
                await Task.Delay(250);
            }
            if (!IsReadyFor(device))
            {
                OnConnectionStatusChanged?.Invoke(this, $"Couldn't connect to {name}");
                return false;
            }
            await SendUrlAsync(url, title);
            return true;
        }

        if (!await ConnectAndWaitAsync(device, PendingSendTimeout))
        {
            OnConnectionStatusChanged?.Invoke(this, $"Couldn't connect to {name}");
            return false;
        }
        await SendUrlAsync(url, title);
        return true;
    }

    public Task RefreshAsync() => RefreshCoreAsync(byPeer: false);

    /// <summary>
    /// Android requestRefresh / performRefresh(fullReset): tell the peer (local refresh only), drop every link
    /// and the Wi-Fi Direct group, forget per-peer state (pre-verification, credentials, LAN-failed marks,
    /// device list, pending offers/waits), restart LAN + BLE discovery. Nothing reconnects by itself:
    /// the PC never auto-connects and pending sends are cancelled.
    /// </summary>
    private async Task RefreshCoreAsync(bool byPeer)
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshInFlight, 1) == 1) return;
        try
        {
            INexShareTransport? conn;
            lock (_connectionLock) conn = _currentConnection;
            if (!byPeer && conn != null && conn.IsConnected)
            {
                try
                {
                    var send = conn.SendMessageAsync(new WireEnvelope { Type = NexShareMessageTypes.PeerRefresh, SenderId = MyDeviceId, Payload = "{}" });
                    await Task.WhenAny(send, Task.Delay(400));
                }
                catch { }
            }
            System.Threading.Interlocked.Increment(ref _connectEpoch);
            lock (_connectionLock)
            {
                if (_currentConnection != null)
                {
                    _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
                    _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
                    var old = _currentConnection;
                    _currentConnection = null;
                    try { old.Dispose(); } catch { }
                }
                _isConnecting = false;
            }
            var wfd = _wfdTransport;
            _wfdTransport = null;
            try { wfd?.Dispose(); } catch { }
            await LeaveWifiDirectAsync();

            _wfdPreVerified.Clear();
            _p2pCredentials.Clear();
            _pendingFileOffers.Clear();
            _lanFailed.Clear();
            _registry.Clear();
            System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
            PublishPeer(false, null, null);

            if (_started)
            {
                try { await _discovery.StopListeningAsync(); await _discovery.StartListeningAsync(); }
                catch (Exception ex) { Console.WriteLine($"[NexShare] Refresh LAN discovery: {ex.Message}"); }
                // BLE is NOT unregistered/re-registered here: on BlueZ 5.87 an immediate
                // Unregister+Register of the advertisement/GATT app makes bluetoothd SEGV ~3 s later
                // (nexshare-ble.log 13:36/13:38/13:40), which tripped the crash valve and disabled BLE.
                // Handshake sessions are per-link and die with the dropped links; the registry was cleared.
            }
            Console.WriteLine($"[NexShare] full reset done (byPeer={byPeer})");
            OnConnectionStatusChanged?.Invoke(this, byPeer ? "The other device refreshed the connection" : "NexShare refreshed");
            OnRefreshed?.Invoke(this, byPeer);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private bool IsCurrentPeer(DiscoveryMessage device)
    {
        if (!string.IsNullOrEmpty(_connectedDeviceId)
            && string.Equals(_connectedDeviceId, device.DeviceId, StringComparison.Ordinal))
            return true;
        return !string.IsNullOrEmpty(_connectedHost)
            && string.Equals(_connectedHost, device.Host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Plain disconnect (no peer_refresh). The peer sees the link drop; Refresh is the synchronized reset.</summary>
    public Task DisconnectCurrentAsync() => DisconnectCoreAsync(sendRefresh: false);

    private async Task DisconnectCoreAsync(bool sendRefresh)
    {
        INexShareTransport? connection;
        lock (_connectionLock)
            connection = _currentConnection;
        if (connection == null)
        {
            PublishPeer(false, null, null);
            return;
        }
        if (sendRefresh && connection is NexZen.Core.NexShare.Transport.TcpFramedTransport)
        {
            // Android "Refresh": tell the peer, give the frame ~400 ms to flush.
            try
            {
                var send = connection.SendMessageAsync(new WireEnvelope
                {
                    Type = NexShareMessageTypes.PeerRefresh, SenderId = MyDeviceId, Payload = "{}"
                });
                await Task.WhenAny(send, Task.Delay(400));
            }
            catch { }
        }
        try
        {
            await connection.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexShare] Disconnect failed: {ex.Message}");
        }
    }

    // ---------------- Wi-Fi Direct (Phase 3): legacy join + TCP 25556 ----------------

    private async Task JoinWifiDirectAsync(string peerId, WireP2pCredentials creds)
    {
        if (_wfdJoiner == null || !_wfdJoiner.IsSupported)
        {
            System.Threading.Interlocked.Increment(ref _connectEpoch);
            OnConnectionStatusChanged?.Invoke(this, "This computer cannot join Wi-Fi Direct.");
            return;
        }
        if (System.Threading.Interlocked.Exchange(ref _wfdBusy, 1) == 1)
        {
            Console.WriteLine("[NexShare] Wi-Fi Direct join already in progress; ignoring duplicate credentials");
            return;
        }
        try
        {
            var port = creds.Port > 0 ? creds.Port : NexZen.Core.NexShare.Transport.TcpFramedTransport.DefaultPort;
            OnConnectionStatusChanged?.Invoke(this, $"Joining {creds.Ssid}...");
            var r = await _wfdJoiner.JoinAsync(creds);
            if (!r.Success)
            {
                System.Threading.Interlocked.Increment(ref _connectEpoch);
                OnConnectionStatusChanged?.Invoke(this, r.PermissionDenied
                    ? $"Couldn't join {creds.Ssid}: {r.Error}"
                    : $"Couldn't join {creds.Ssid}: {r.Error}");
                return;
            }
            _wfdJoined = true;
            OnConnectionStatusChanged?.Invoke(this, $"Joined {creds.Ssid} ({r.LocalIp}). Connecting to {creds.GoIp}:{port}...");

            var t = new NexZen.Core.NexShare.Transport.TcpFramedTransport(_crypto, MyDeviceId)
            {
                ConsumePreVerified = id => _wfdPreVerified.TryRemove(id, out _),
                IsTrusted = id => _store.IsTrusted(id),
                MarkTrusted = id => _store.AddTrusted(id),
            };
            t.OnLog += (_, m) => Console.WriteLine("[NexShare] WFD: " + m);
            t.OnSasRequested += (_, p) => OnSasRequested?.Invoke(this, p);
            try { await t.ConnectAsync(creds.GoIp, port); }
            catch (Exception ex)
            {
                t.Dispose();
                System.Threading.Interlocked.Increment(ref _connectEpoch);
                OnConnectionStatusChanged?.Invoke(this, $"Wi-Fi Direct connection failed: {ex.Message}");
                await LeaveWifiDirectAsync();
                return;
            }

            lock (_connectionLock)
            {
                var old = _currentConnection;
                if (old != null)
                {
                    old.OnMessageReceived -= Client_OnMessageReceived;
                    old.OnDisconnected -= OnCurrentConnectionDisconnected;
                    _currentConnection = null;
                    try { old.Dispose(); } catch { }
                }
                _isConnecting = false;
            }
            _wfdTransport = t;
            t.OnDisconnected += (_, _) => _ = OnWfdTransportClosedAsync(t);
            System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
            SetCurrentConnection(t);
            PublishPeer(true, peerId, creds.GoIp);
            _ = t.StartReceiveLoopAsync();
            _ = WfdIdleWatchAsync(t);
            OnConnectionStatusChanged?.Invoke(this, "Connected over Wi-Fi Direct");
        }
        catch (Exception ex)
        {
            OnConnectionStatusChanged?.Invoke(this, $"Wi-Fi Direct error: {ex.Message}");
            await LeaveWifiDirectAsync();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _wfdBusy, 0);
        }
    }

    /// <summary>Leave the Wi-Fi Direct group (and restore the previous Wi-Fi) after this long without traffic.</summary>
    public static TimeSpan WfdIdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private async Task WfdIdleWatchAsync(NexZen.Core.NexShare.Transport.TcpFramedTransport t)
    {
        while (ReferenceEquals(_wfdTransport, t) && t.IsConnected)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(_wfdTransport, t)) return;
            var idleMs = Environment.TickCount64 - t.LastActivityMs;
            if (idleMs < WfdIdleTimeout.TotalMilliseconds) continue;
            Console.WriteLine($"[NexShare] Wi-Fi Direct idle for {idleMs / 1000}s; leaving group and restoring Wi-Fi");
            OnConnectionStatusChanged?.Invoke(this, "Wi-Fi Direct is down, restoring Wi-Fi...");
            try { t.Dispose(); } catch { }
            // Dispose raises OnDisconnected -> OnWfdTransportClosedAsync; leave here too in case it was not raised.
            await OnWfdTransportClosedAsync(t);
            await LeaveWifiDirectAsync();
            return;
        }
    }

    private async Task OnWfdTransportClosedAsync(NexZen.Core.NexShare.Transport.TcpFramedTransport t)
    {
        if (!ReferenceEquals(_wfdTransport, t)) return;
        _wfdTransport = null;
        // Android: disconnect => refresh semantics (full reset, back to the device list).
        await LeaveWifiDirectAsync();
        OnConnectionStatusChanged?.Invoke(this, "Wi-Fi Direct disconnected.");
    }

    private async Task LeaveWifiDirectAsync()
    {
        if (_wfdJoiner == null || !_wfdJoined) return;
        _wfdJoined = false;
        try { await _wfdJoiner.LeaveAsync(); }
        catch (Exception ex) { Console.WriteLine($"[NexShare] WFD leave failed: {ex.Message}"); }
    }

    private async Task ConnectCoreAsync(DiscoveryMessage device)
    {
        if (_currentConnection != null || _isConnecting) return;
        if (!await _connectLock.WaitAsync(0)) return;

        _isConnecting = true;
        try
        {
            if (_currentConnection != null) return;

            OnConnectionStatusChanged?.Invoke(this, $"Connecting to {device.DeviceName}...");
            var transport = await _server.ConnectOutboundAsync(device.Host, device.TcpPort);

            System.Threading.Interlocked.Exchange(ref _helloAnnounced, 0);
            SetCurrentConnection(transport);
            await SendHelloAsync(transport);
            _ = transport.StartReceiveLoopAsync();
            _isConnecting = false;
            PublishPeer(true, device.DeviceId, device.Host);
            OnConnectionStatusChanged?.Invoke(this, $"Connected with {device.DeviceName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexShare] Failed to connect to {device.DeviceName} ({device.Host}:{device.TcpPort}): {ex.Message}");
            OnConnectionStatusChanged?.Invoke(this, $"Failed to connect to {device.DeviceName}");
            _isConnecting = false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void PublishPeer(bool connected, string? deviceId, string? host)
    {
        _connectedDeviceId = connected ? deviceId : null;
        _connectedHost = connected ? host : null;
        OnPeerConnectionChanged?.Invoke(this, new PeerLinkState
        {
            IsConnected = connected,
            DeviceId = _connectedDeviceId,
            Host = _connectedHost
        });
    }

    public async Task SendTextMessageAsync(string text)
    {
        if (_currentConnection != null)
        {
            var env = new WireEnvelope 
            { 
                Type = NexShareMessageTypes.Text, 
                SenderId = MyDeviceId,
                Payload = text 
            };
            await _currentConnection.SendMessageAsync(env);
        }
    }

    public async Task SendFileAsync(string filePath)
    {
        if (_currentConnection != null)
        {
            var fileName = System.IO.Path.GetFileName(filePath);
            var fileInfo = new System.IO.FileInfo(filePath);
            var transferId = Guid.NewGuid().ToString("N");

            // Register file on existing WebSocket server's HTTP listener
            _server.RegisterFileOffer(transferId, filePath);

            // Send FileOffer
            var fileOffer = new FileOfferMessage
            {
                TransferId = transferId,
                FileName = fileName,
                FileSize = fileInfo.Length,
                MimeType = GuessFileMimeType(fileName),
                HttpPort = _server.Port
            };

            var env = new WireEnvelope
            {
                Type = NexShareMessageTypes.FileOffer,
                SenderId = MyDeviceId,
                Payload = JsonSerializer.Serialize(fileOffer, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            };
            
            OnConnectionStatusChanged?.Invoke(this, $"Sent file offer for: {fileName}");
            await _currentConnection.SendMessageAsync(env);
        }
    }

    public async Task<string> AcceptFileAsync(string transferId)
    {
        if (_currentConnection != null && _pendingFileOffers.TryGetValue(transferId, out var offer))
        {
            var accept = new FileAcceptMessage
            {
                TransferId = transferId
            };
            
            var env = new WireEnvelope
            {
                Type = NexShareMessageTypes.FileAccept,
                SenderId = MyDeviceId,
                Payload = JsonSerializer.Serialize(accept, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            };
            await _currentConnection.SendMessageAsync(env);

            var ip = _currentConnection.RemoteIpAddress;
            var httpUrl = $"http://{ip}:{offer.HttpPort}/nexshare/file/{offer.TransferId}";
            return httpUrl;
        }
        return string.Empty;
    }

    public async Task SendUrlAsync(string url, string title)
    {
        if (_currentConnection != null)
        {
            var urlOffer = new UrlOfferMessage
            {
                Url = url,
                Title = title,
                DeviceName = MyDeviceName
            };

            var env = new WireEnvelope
            {
                Type = NexShareMessageTypes.UrlOffer,
                SenderId = MyDeviceId,
                Payload = JsonSerializer.Serialize(urlOffer, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            };
            
            OnConnectionStatusChanged?.Invoke(this, $"Sent URL offer: {url}");
            await _currentConnection.SendMessageAsync(env);
        }
    }

    private static string GuessFileMimeType(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "bmp" => "image/bmp",
            "heic" or "heif" => "image/heic",
            "avif" => "image/avif",
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "mkv" => "video/x-matroska",
            "mov" => "video/quicktime",
            "pdf" => "application/pdf",
            "txt" => "text/plain",
            "zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private string GetLocalIPAddress()
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return ip.ToString();
            }
        }
        return "127.0.0.1";
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        // Cancel the presence loop, then drop the source. The loop owns the
        // old token and exits on its own; disposing it here can throw.
        try { _broadcastCts.Cancel(); } catch { }
        _broadcastCts = new System.Threading.CancellationTokenSource();

        if (_ble != null)
        {
            try { await _ble.StopListeningAsync(); }
            catch (Exception ex) { Console.WriteLine($"[NexShare] Stop BLE: {ex.Message}"); }
        }
        try { await _discovery.StopListeningAsync(); }
        catch (Exception ex) { Console.WriteLine($"[NexShare] Stop discovery: {ex.Message}"); }

        try { await _server.StopAsync(); }
        catch (Exception ex) { Console.WriteLine($"[NexShare] Stop server: {ex.Message}"); }

        try { _wfdTransport?.Dispose(); } catch { }
        _wfdTransport = null;
        await LeaveWifiDirectAsync();

        lock (_connectionLock)
        {
            if (_currentConnection != null)
            {
                _currentConnection.OnMessageReceived -= Client_OnMessageReceived;
                _currentConnection.OnDisconnected -= OnCurrentConnectionDisconnected;
                try { _currentConnection.Dispose(); } catch { }
                _currentConnection = null;
            }
            _isConnecting = false;
        }

        try { _myPrivateKey?.Dispose(); } catch { }
        _myPrivateKey = null;
        _myPublicKey = null;
        _started = false;
        PublishPeer(false, null, null);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}
