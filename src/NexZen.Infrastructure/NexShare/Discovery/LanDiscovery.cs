using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Infrastructure.NexShare.Discovery;

public class LanDiscovery : INexShareDiscovery, IDisposable
{
    private const int DiscoveryPort = 42999;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Dedup cache: key = deviceId:timestamp, prevents duplicate events from multiple NICs
    private readonly System.Collections.Generic.HashSet<string> _seenPackets = new();
    private readonly object _seenLock = new();
    private long _lastPurgeMs = 0;
    // Device ids already logged, so the 3s presence broadcasts don't flood the console
    private readonly System.Collections.Generic.HashSet<string> _loggedDevices = new();

    public event EventHandler<DiscoveryMessage>? OnDeviceDiscovered;

    public async Task StartListeningAsync()
    {
        if (_udpClient != null) return;

        // The previous socket can still own port 42999 for a moment after Off.
        SocketException? lastBindError = null;
        for (var attempt = 0; attempt < 5 && _udpClient == null; attempt++)
        {
            var client = new UdpClient();
            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _udpClient = client;
                lastBindError = null;
            }
            catch (SocketException ex)
            {
                lastBindError = ex;
                client.Dispose();
                await Task.Delay(200);
            }
        }
        if (_udpClient == null)
            throw lastBindError ?? new SocketException((int)SocketError.AddressAlreadyInUse);
        
        _cts = new CancellationTokenSource();
        
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    
                    try
                    {
                        var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json, _jsonOptions);
                        if (packet != null && packet.Type == "nexshare_discovery" && packet.Device != null)
                        {
                            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            if (Math.Abs(now - packet.Timestamp) > 30_000) 
                            {
                                Console.WriteLine($"[NexShare LanDiscovery] Timestamp too old: {packet.Timestamp} vs Now: {now}");
                                continue;
                            }

                            var expectedHmac = ComputeHmac(packet.Device.DeviceId, packet.WsPort, packet.Timestamp);
                            if (packet.Hmac != expectedHmac) 
                            {
                                Console.WriteLine($"[NexShare LanDiscovery] HMAC Mismatch! Expected: {expectedHmac}, Got: {packet.Hmac}");
                                continue;
                            }
                            
                            // --- Dedup: thread-safe, skip if we already processed this exact packet ---
                            var dedupKey = $"{packet.Device.DeviceId}:{packet.Timestamp}";
                            bool isNew;
                            lock (_seenLock)
                            {
                                var nowMs2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                if (nowMs2 - _lastPurgeMs > 10_000)
                                {
                                    _seenPackets.Clear();
                                    _lastPurgeMs = nowMs2;
                                }
                                isNew = _seenPackets.Add(dedupKey);
                            }
                            if (!isNew) continue;
                            // -----------------------------------------------------------------------

                            bool firstSeen;
                            lock (_seenLock) firstSeen = _loggedDevices.Add(packet.Device.DeviceId);
                            if (firstSeen)
                                Console.WriteLine($"[NexShare LanDiscovery] Discovered device {packet.Device.DeviceName}");

                            var msg = new DiscoveryMessage
                            {
                                DeviceId = packet.Device?.DeviceId ?? "",
                                DeviceName = packet.Device?.DeviceName ?? "",
                                Host = result.RemoteEndPoint.Address.ToString(),
                                TcpPort = packet.WsPort,
                                HttpPort = packet.WsPort,
                                Timestamp = packet.Timestamp,
                                Signature = packet.Hmac ?? ""
                            };
                            OnDeviceDiscovered?.Invoke(this, msg);
                        }
                        else
                        {
                            Console.WriteLine($"[NexShare LanDiscovery] Invalid packet structure: {json}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NexShare LanDiscovery] Parsing error: {ex.Message} for JSON: {json}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }, _cts.Token);
    }

    public Task StopListeningAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;

        return Task.CompletedTask;
    }

    public async Task BroadcastPresenceAsync(DiscoveryMessage message)
    {
        using var client = new UdpClient();
        client.EnableBroadcast = true;
        
        var packet = new DiscoveryPacket
        {
            Type = "nexshare_discovery",
            Version = 1,
            Device = new WireDevice
            {
                DeviceId = message.DeviceId,
                DeviceName = message.DeviceName,
                DeviceType = "pc"
            },
            WsPort = message.TcpPort,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        
        packet.Hmac = ComputeHmac(packet.Device.DeviceId, packet.WsPort, packet.Timestamp);
        
        var json = JsonSerializer.Serialize(packet, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        await client.SendAsync(bytes, bytes.Length, endpoint);
    }

    private string ComputeHmac(string deviceId, int wsPort, long ts)
    {
        var keyStr = $"nexshare_discovery_v1:{deviceId}";
        var dataStr = $"{deviceId}:{wsPort}:{ts}";
        
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(keyStr));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataStr));
        
        var base64 = Convert.ToBase64String(hash);
        var signature = base64.Substring(0, Math.Min(16, base64.Length));
        return signature;
    }

    public void Dispose()
    {
        _ = StopListeningAsync();
    }
}
