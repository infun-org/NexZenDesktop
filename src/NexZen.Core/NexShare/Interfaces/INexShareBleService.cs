using System;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Interfaces;

/// <summary>
/// BLE discovery + handshake (Android-compatible 0xAABB service). Discovery results arrive through
/// <see cref="INexShareDiscovery.OnDeviceDiscovered"/> with <see cref="DiscoveryMessage.ViaBle"/> set.
/// </summary>
public interface INexShareBleService : INexShareDiscovery
{
    /// <summary>False on non-Linux / when BlueZ is missing or the adapter is off.</summary>
    bool IsAvailable { get; }

    void Configure(string myDeviceId, string myDeviceName, int wsPort);

    /// <summary>Peers for which the local SAS confirm is automatic (trusted store). Null => never.</summary>
    Func<string, bool>? TrustPredicate { get; set; }

    /// <summary>Initiator: connect to <paramref name="bleAddress"/> and run ecdh_hello / SAS / credentials.</summary>
    Task<bool> StartHandshakeAsync(string bleAddress, string peerId);

    /// <summary>
    /// Initiator: handshake (SAS unless trusted) then deliver <paramref name="link"/> as encrypted ble_url and wait
    /// for ble_url_ack. If the peer lacks ble_link the session falls back to Wi-Fi Direct credentials.
    /// </summary>
    Task<BleLinkResult> SendLinkAsync(string bleAddress, string peerId, WireBleUrl link);

    /// <summary>A peer delivered a link over BLE (we were the GATT server / responder).</summary>
    event EventHandler<BleLinkReceivedEventArgs>? OnLinkReceived;

    event EventHandler<SasPrompt>? OnSasRequested;
    event EventHandler<BleVerifiedEventArgs>? OnVerified;
    event EventHandler<BleCredentialsEventArgs>? OnCredentials;
    event EventHandler<BleHandshakeFailedEventArgs>? OnHandshakeFailed;
    event EventHandler<string>? OnStatus;
}
