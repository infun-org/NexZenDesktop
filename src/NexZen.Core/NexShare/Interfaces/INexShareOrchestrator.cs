using System;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Interfaces;

public interface INexShareOrchestrator : IDisposable
{
    event EventHandler<DiscoveryMessage>? OnDeviceFound;
    event EventHandler<string>? OnTextMessageReceived;
    event EventHandler<FileOfferMessage>? OnFileOfferReceived;
    event EventHandler<UrlOfferMessage>? OnUrlOfferReceived;
    event EventHandler<string>? OnConnectionStatusChanged;
    event EventHandler<PeerLinkState>? OnPeerConnectionChanged;
    /// <summary>A BLE pairing needs the user to compare a 6-digit code.</summary>
    event EventHandler<SasPrompt>? OnSasRequested;

    /// <summary>Wi-Fi Direct credentials received over BLE, by peer deviceId (used by the Wi-Fi Direct join, Phase 3).</summary>
    System.Collections.Generic.IReadOnlyDictionary<string, WireP2pCredentials> ReceivedP2pCredentials { get; }

    string MyDeviceId { get; }
    string MyDeviceName { get; }

    Task StartAsync(string deviceName);
    void SetDeviceName(string deviceName);
    Task StopAsync();
    
    Task ConnectToDeviceAsync(DiscoveryMessage device);
    Task DisconnectCurrentAsync();
    Task SendTextMessageAsync(string text);
    Task SendUrlAsync(string url, string title);
    Task SendFileAsync(string filePath);
    Task<string> AcceptFileAsync(string transferId);

    /// <summary>Raised after a full reset (Refresh). Arg: true when the peer requested it (peer_refresh).</summary>
    event EventHandler<bool>? OnRefreshed;

    /// <summary>Picks LAN or BLE -> Wi-Fi Direct, connects, and waits until the HELLO exchange is done.</summary>
    Task<bool> ConnectAndWaitAsync(DiscoveryMessage device, TimeSpan timeout);

    /// <summary>Queues the url_offer until the peer is connected over whichever route, then sends it.</summary>
    Task<bool> SendUrlToDeviceAsync(DiscoveryMessage device, string url, string title);

    /// <summary>Android requestRefresh: peer_refresh to the connected peer, full reset, restart discovery.</summary>
    Task RefreshAsync();

    /// <summary>Screen-mirroring messages (mirror_*) from the connected peer; see NexZen.Core.NexShare.Mirror.</summary>
    event EventHandler<WireEnvelope>? OnMirrorMessage;

    /// <summary>Device id of the peer on the current connection (after its HELLO), or null.</summary>
    string? ConnectedDeviceId { get; }

    /// <summary>Sends an envelope on the current connection (SenderId is filled in). False when not connected.</summary>
    Task<bool> SendEnvelopeAsync(WireEnvelope envelope);
}
