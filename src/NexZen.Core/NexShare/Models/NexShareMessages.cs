using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexZen.Core.NexShare.Models;

public static class NexShareMessageTypes
{
    public const string Discovery = "nexshare_discovery";
    public const string Hello = "hello";
    public const string Text = "text";
    public const string FileOffer = "file_offer";
    public const string FileAccept = "file_accept";
    public const string FileChunk = "file_chunk";
    public const string FileDone = "file_done";
    public const string FileCancel = "file_cancel";
    public const string Ack = "ack";
    public const string UrlOffer = "url_offer";
    // Android fallback: when direct url_offer fails, Android uses a handoff session
    public const string HandoffCreated = "handoff_created";
    public const string FileChunkReq = "file_chunk_req";
    public const string FileChunkAck = "file_chunk_ack";
    public const string BrowserAuth = "browser_auth";
    public const string SessionJoin = "session_join";
    public const string SessionDecision = "session_decision";
    public const string SessionAck = "session_ack";
    public const string LanHints = "lan_hints";
    /// <summary>Sent before a "Refresh": the receiver also does a full reset and must not auto-reconnect.</summary>
    public const string PeerRefresh = "peer_refresh";
    public const string System = "system";
    public const string Typing = "typing";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string EcdhHello = "ecdh_hello";
    public const string SasConfirm = "sas_confirm";
    public const string SasReject = "sas_reject";
    public const string P2pCredentials = "p2p_credentials";
    /// <summary>BLE-only link delivery: content = E2E JSON of WireBleUrl, key = DeriveBleLinkKey.</summary>
    public const string BleUrl = "ble_url";
    public const string BleUrlAck = "ble_url_ack";
}

public class DiscoveryMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty; // Added Host IP
    public int TcpPort { get; set; }
    public int HttpPort { get; set; }
    public long Timestamp { get; set; }
    public string Signature { get; set; } = string.Empty;

    /// <summary>Seen over BLE (0xAABB service). Not part of the LAN wire format.</summary>
    [JsonIgnore] public bool ViaBle { get; set; }
    /// <summary>BlueZ address of the peer when discovered over BLE.</summary>
    [JsonIgnore] public string? BleAddress { get; set; }
    /// <summary>Capabilities from the BLE info string ("wfd_legacy", "wfd_e2e", ...).</summary>
    [JsonIgnore] public List<string> Caps { get; set; } = new();

    /// <summary>
    /// Merges a new sighting into what is already known about the same deviceId: BLE sightings keep the LAN
    /// host/ports, LAN sightings keep the BLE address/caps.
    /// </summary>
    public static DiscoveryMessage Merge(DiscoveryMessage? known, DiscoveryMessage incoming)
    {
        if (known == null) return incoming;
        var incomingHasLan = !string.IsNullOrEmpty(incoming.Host);
        var knownHasLan = !string.IsNullOrEmpty(known.Host);
        return new DiscoveryMessage
        {
            DeviceId = incoming.DeviceId,
            DeviceName = incomingHasLan || !knownHasLan || string.IsNullOrEmpty(known.DeviceName) ? incoming.DeviceName : known.DeviceName,
            Host = incomingHasLan ? incoming.Host : known.Host,
            TcpPort = incomingHasLan || !knownHasLan ? incoming.TcpPort : known.TcpPort,
            HttpPort = incomingHasLan ? incoming.HttpPort : known.HttpPort,
            Timestamp = incoming.Timestamp,
            Signature = incomingHasLan ? incoming.Signature : known.Signature,
            ViaBle = incoming.ViaBle || known.ViaBle,
            BleAddress = incoming.BleAddress ?? known.BleAddress,
            Caps = incoming.Caps.Count > 0 ? incoming.Caps : known.Caps,
        };
    }
}

public class PeerLinkState
{
    public bool IsConnected { get; init; }
    public string? DeviceId { get; init; }
    public string? Host { get; init; }
}

public class WireDevice
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = "pc";
}

public class DiscoveryPacket
{
    public string Type { get; set; } = "nexshare_discovery";
    public int Version { get; set; } = 1;
    public WireDevice Device { get; set; } = new();
    public int WsPort { get; set; }
    public long Timestamp { get; set; }
    public string? Hmac { get; set; }
}

public class HelloMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string PublicKeyBase64 { get; set; } = string.Empty;
}

public class AckMessage
{
    public string MessageId { get; set; } = string.Empty;
}

public class TextMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty; // Android uses "content" instead of "Text"
}

public class FileOfferMessage
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public int HttpPort { get; set; }
}

public class FileAcceptMessage
{
    public string TransferId { get; set; } = string.Empty;
}

public class FileCancelMessage
{
    public string TransferId { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public class FileChunkMessage
{
    public string TransferId { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public string DataBase64 { get; set; } = string.Empty; // Encrypted chunk
}

public class UrlOfferMessage
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}

/// <summary>
/// Android fallback handoff: sent when direct url_offer WebSocket fails.
/// Android creates a "handoff session" and delivers it via the existing connection.
/// PC must treat this identically to UrlOfferMessage.
/// </summary>
public class HandoffCreatedMessage
{
    public string HandoffId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public long Timestamp { get; set; }
    public long ExpireAt { get; set; }
}
