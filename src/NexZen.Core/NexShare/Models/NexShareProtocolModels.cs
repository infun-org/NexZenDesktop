using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NexZen.Core.NexShare.Models;

// Payload models carried in WireEnvelope.content. Field names match Android NexShareWireModels.kt (Gson, no naming policy).
// Nullable members are omitted when null, as Gson does by default.

/// <summary>Capability tokens (Android NexShareCaps). Exchanged in BLE info string "id|wsPort|caps" and ecdh_hello.caps.</summary>
public static class NexShareCaps
{
    public const string WfdLegacy = "wfd_legacy";
    public const string WfdE2e = "wfd_e2e";
    /// <summary>Can receive/send a single link (url_offer) directly over the BLE 0xDDEE handshake (ble_url / ble_url_ack).</summary>
    public const string BleLink = "ble_link";
    /// <summary>
    /// PC honours "link pull" adverts: a phone with a link for us advertises service data "NEXSHARE"+first 4 chars of
    /// our device id; we connect to it (PC as central, the direction that works reliably) and receive ble_url.
    /// </summary>
    public const string BleLinkPull = "ble_link_pull";
}

/// <summary>Value of <see cref="WireEcdhHello.Intent"/> when the sender only wants to deliver a link over BLE.</summary>
public static class NexShareHelloIntent
{
    public const string Link = "link";
}

/// <summary>Plaintext of the encrypted ble_url payload (Android WireBleUrl).</summary>
public class WireBleUrl
{
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
}

public class WireEcdhHello
{
    /// <summary>Base64 (standard, padded) of the P-256 public key as X.509 SubjectPublicKeyInfo DER (91 bytes).</summary>
    [JsonPropertyName("pubKey")] public string PubKey { get; set; } = string.Empty;
    /// <summary>Base64 of a 16-byte random nonce.</summary>
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("caps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Caps { get; set; }
    /// <summary>"link": sender only delivers one link over BLE (no Wi-Fi Direct group needed). Omitted otherwise.</summary>
    [JsonPropertyName("intent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intent { get; set; }
}

public class WireP2pCredentials
{
    [JsonPropertyName("ssid")] public string Ssid { get; set; } = string.Empty;
    [JsonPropertyName("passphrase")] public string Passphrase { get; set; } = string.Empty;
    [JsonPropertyName("goIp")] public string GoIp { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; } = 25556;
}

public class WireFileChunkReq
{
    [JsonPropertyName("transferId")] public string TransferId { get; set; } = string.Empty;
    [JsonPropertyName("chunkSize")] public int ChunkSize { get; set; }
    [JsonPropertyName("startOffset")] public long StartOffset { get; set; }
    [JsonPropertyName("sha256Hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256Hex { get; set; }
}

/// <summary>
/// Android WireFileChunk. Plain: dataB64 = base64(bytes). E2E: e2e=1, nB64 = base64(12-byte nonce), dataB64 = base64(ct||tag).
/// </summary>
public class WireFileChunk
{
    [JsonPropertyName("transferId")] public string TransferId { get; set; } = string.Empty;
    [JsonPropertyName("offset")] public long Offset { get; set; }
    [JsonPropertyName("dataB64")] public string DataB64 { get; set; } = string.Empty;
    [JsonPropertyName("e2e")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? E2e { get; set; }
    [JsonPropertyName("nB64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NB64 { get; set; }
    [JsonPropertyName("isLast")] public bool IsLast { get; set; }
    [JsonPropertyName("sha256Hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256Hex { get; set; }
}

public class WireFileChunkAck
{
    [JsonPropertyName("transferId")] public string TransferId { get; set; } = string.Empty;
    [JsonPropertyName("receivedUpTo")] public long ReceivedUpTo { get; set; }
}

public class WireFileDone
{
    [JsonPropertyName("transferId")] public string TransferId { get; set; } = string.Empty;
    [JsonPropertyName("sha256Hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256Hex { get; set; }
}

public class WireSessionJoin
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
    [JsonPropertyName("joiner")] public WireDevice Joiner { get; set; } = new();
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Capabilities { get; set; }
    [JsonPropertyName("cryptoVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CryptoVersion { get; set; } = 1;
}

public class WireSessionDecision
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WireDevice? Host { get; set; }
    [JsonPropertyName("fingerprintShort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FingerprintShort { get; set; }
}

public class WireSessionAck
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
}

public class WireLanHints
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("wsPort")] public int WsPort { get; set; }
    [JsonPropertyName("httpPort")] public int HttpPort { get; set; }
    [JsonPropertyName("addresses")] public List<string> Addresses { get; set; } = new();
}

/// <summary>E2E text content: {"e2e":1,"n":"&lt;b64 nonce&gt;","ct":"&lt;b64 ct||tag&gt;"}.</summary>
public class WireE2eContent
{
    [JsonPropertyName("e2e")] public int E2e { get; set; } = 1;
    [JsonPropertyName("n")] public string N { get; set; } = string.Empty;
    [JsonPropertyName("ct")] public string Ct { get; set; } = string.Empty;
}
