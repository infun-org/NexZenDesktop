using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using NexZen.Core.NexShare.Interfaces;

namespace NexZen.Core.NexShare.Mirror;

/// <summary>
/// Phone screen mirroring over an existing NexShare connection (protocol v1, phase 1: view only).
///
/// Every session is authenticated on its own: NexShare's trust list holds only device ids, so a
/// fresh P-256 ECDH runs per session and both screens show the same 6-digit SAS (the NexShare
/// construction). The phone owner compares the code and allows the capture; the PC owner can stop
/// if it differs. Video is end-to-end encrypted with a key derived from that exchange.
///
/// 1. PC → phone  mirror_request {s, pubKey, nonce, deviceName}
/// 2. phone → PC  mirror_hello   {s, pubKey, nonce}            (no consent yet; both now show the SAS)
/// 3. phone       user compares the code, allows, Android asks for screen-capture permission
///    phone → PC  mirror_reject  {s, reason}                    (if declined)
/// 4. phone → PC  mirror_frame   {s, n, ct} …                   (encrypted, see <see cref="MirrorFrame"/>)
/// 5. PC → phone  mirror_keyframe {s}                          (decoder needs a sync frame)
/// 6. either      mirror_stop    {s, reason}
/// </summary>
public static class MirrorMessageTypes
{
    public const string Request = "mirror_request";
    public const string Hello = "mirror_hello";
    public const string Reject = "mirror_reject";
    public const string Frame = "mirror_frame";
    public const string KeyFrame = "mirror_keyframe";
    public const string Stop = "mirror_stop";

    public static bool IsMirror(string? type) => type != null && type.StartsWith("mirror_", StringComparison.Ordinal);
}

public sealed class WireMirrorRequest
{
    [JsonPropertyName("s")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("pubKey")] public string PubKey { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
}

public sealed class WireMirrorHello
{
    [JsonPropertyName("s")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("pubKey")] public string PubKey { get; set; } = string.Empty;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = string.Empty;
}

/// <summary>mirror_reject / mirror_stop / mirror_keyframe.</summary>
public sealed class WireMirrorControl
{
    [JsonPropertyName("s")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

public sealed class WireMirrorFrame
{
    [JsonPropertyName("s")] public string SessionId { get; set; } = string.Empty;
    [JsonPropertyName("n")] public string NonceB64 { get; set; } = string.Empty;
    [JsonPropertyName("ct")] public string CipherB64 { get; set; } = string.Empty;
}

[Flags]
public enum MirrorFrameFlags : byte
{
    None = 0,
    /// <summary>Contains an IDR picture (SPS/PPS are prepended by the phone).</summary>
    KeyFrame = 1,
    /// <summary>Payload is UTF-8 JSON <see cref="MirrorStreamConfig"/> instead of video.</summary>
    Config = 2,
}

/// <summary>Sent (as a Config frame) before the first picture and whenever the size changes (rotation).</summary>
public sealed class MirrorStreamConfig
{
    [JsonPropertyName("codec")] public string Codec { get; set; } = "h264";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

/// <summary>Decrypted frame: plaintext layout = seq (8 bytes BE) | flags (1 byte) | payload.</summary>
public readonly record struct MirrorFrame(long Seq, MirrorFrameFlags Flags, byte[] Payload);

/// <summary>Key exchange and frame crypto shared by both sides (Android NexShareMirror.kt).</summary>
public sealed class MirrorSessionCrypto
{
    public const string KeyPrefix = "nexzen_mirror_v1:";

    public string Sas { get; }
    public string Tag { get; }
    private readonly byte[] _key;
    private readonly INexShareCrypto _crypto;
    private long _lastSeq = -1;

    private MirrorSessionCrypto(INexShareCrypto crypto, byte[] key, string tag, string sas)
    {
        _crypto = crypto;
        _key = key;
        Tag = tag;
        Sas = sas;
    }

    /// <summary>ECDH + SAS + key, exactly as on the phone.</summary>
    public static MirrorSessionCrypto Derive(INexShareCrypto crypto, ECDiffieHellman myKey, byte[] myPub, byte[] myNonce,
        byte[] peerPub, byte[] peerNonce, string sessionId)
    {
        var secret = crypto.DeriveSharedSecret(myKey, peerPub);
        var sas = crypto.DeriveWfdSas(myPub, peerPub, myNonce, peerNonce, secret);
        var sid = crypto.WfdSessionId(myPub, peerPub);
        var key = DeriveKey(secret, sid);
        CryptographicOperations.ZeroMemory(secret);
        // The tag binds frames to this session's keys and to the request id.
        return new MirrorSessionCrypto(crypto, key, "mirror:" + sessionId + ":" + sid, sas);
    }

    /// <summary>PRK = HMAC(secret, "nexzen_mirror_v1:"+sid); key = HMAC(PRK, "info"||0x01).</summary>
    public static byte[] DeriveKey(byte[] sharedSecret, string sid)
    {
        var prk = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(KeyPrefix + sid));
        var info = new byte[] { (byte)'i', (byte)'n', (byte)'f', (byte)'o', 0x01 };
        return HMACSHA256.HashData(prk, info);
    }

    /// <summary>Decrypts and checks ordering; null for forged, corrupt or replayed frames.</summary>
    public MirrorFrame? Open(WireMirrorFrame wire)
    {
        byte[] nonce, ct;
        try
        {
            nonce = Convert.FromBase64String(wire.NonceB64);
            ct = Convert.FromBase64String(wire.CipherB64);
        }
        catch (FormatException) { return null; }
        var plain = _crypto.TryDecrypt(nonce, ct, _key, Tag);
        if (plain == null || plain.Length < 9) return null;
        var seq = BinaryPrimitives.ReadInt64BigEndian(plain);
        if (seq <= _lastSeq) return null;
        _lastSeq = seq;
        return new MirrorFrame(seq, (MirrorFrameFlags)plain[8], plain.AsSpan(9).ToArray());
    }

    /// <summary>Phone side (used by tests to produce frames the way NexShareMirror.kt does).</summary>
    public WireMirrorFrame Seal(string sessionId, long seq, MirrorFrameFlags flags, ReadOnlySpan<byte> payload)
    {
        var plain = new byte[9 + payload.Length];
        BinaryPrimitives.WriteInt64BigEndian(plain, seq);
        plain[8] = (byte)flags;
        payload.CopyTo(plain.AsSpan(9));
        var (n, ct) = _crypto.Encrypt(plain, _key, Tag);
        return new WireMirrorFrame { SessionId = sessionId, NonceB64 = Convert.ToBase64String(n), CipherB64 = Convert.ToBase64String(ct) };
    }
}
