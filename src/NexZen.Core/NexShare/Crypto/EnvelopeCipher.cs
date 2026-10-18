using System;
using System.Text.Json;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Crypto;

/// <summary>
/// Encrypts / decrypts the payload of "text" and "file_chunk" envelopes exactly like Android
/// WifiDirectTransport.encryptOutgoing / decryptIncoming and the LAN E2E path. Other types pass through.
/// aadTag = WFD sessionId (hex) on Wi-Fi Direct, or the QR/LAN sessionId for LAN E2E.
/// </summary>
public sealed class EnvelopeCipher
{
    private readonly INexShareCrypto _crypto;
    private readonly byte[] _key;
    private readonly string _aadTag;

    public EnvelopeCipher(INexShareCrypto crypto, byte[] key, string aadTag)
    {
        _crypto = crypto;
        _key = key;
        _aadTag = aadTag;
    }

    public static bool LooksEncryptedText(string? content) =>
        content != null && content.Contains("\"e2e\"") && content.Contains("\"ct\"");

    /// <summary>Returns a new envelope with an encrypted payload (already-encrypted payloads are left as is).</summary>
    public WireEnvelope Encrypt(WireEnvelope env, byte[]? fixedNonce = null)
    {
        if (env.Type == NexShareMessageTypes.Text)
        {
            if (LooksEncryptedText(env.Payload)) return env;
            return env.With(_crypto.EncryptTextToJson(env.Payload ?? string.Empty, _key, _aadTag, fixedNonce));
        }
        if (env.Type == NexShareMessageTypes.FileChunk)
        {
            var chunk = TryParseChunk(env.Payload);
            if (chunk == null || chunk.E2e == 1) return env;
            var plain = string.IsNullOrEmpty(chunk.DataB64) ? Array.Empty<byte>() : Convert.FromBase64String(chunk.DataB64);
            var (n, ct) = _crypto.Encrypt(plain, _key, _aadTag, fixedNonce);
            chunk.DataB64 = Convert.ToBase64String(ct);
            chunk.NB64 = Convert.ToBase64String(n);
            chunk.E2e = 1;
            return env.With(JsonSerializer.Serialize(chunk));
        }
        return env;
    }

    /// <summary>
    /// Decrypts an encrypted text/file_chunk payload. Returns the decrypted envelope; returns null when the
    /// payload is encrypted but cannot be decrypted with this key. Plaintext and other types are returned unchanged
    /// (plaintext policy is the caller's decision; see <see cref="IsEncrypted"/>).
    /// </summary>
    public WireEnvelope? Decrypt(WireEnvelope env)
    {
        if (env.Type == NexShareMessageTypes.Text)
        {
            if (!LooksEncryptedText(env.Payload)) return env;
            var plain = _crypto.TryDecryptTextFromJson(env.Payload, _key, _aadTag);
            return plain == null ? null : env.With(plain);
        }
        if (env.Type == NexShareMessageTypes.FileChunk)
        {
            var chunk = TryParseChunk(env.Payload);
            if (chunk == null || chunk.E2e != 1 || string.IsNullOrEmpty(chunk.NB64)) return env;
            byte[]? pt;
            try { pt = _crypto.TryDecrypt(Convert.FromBase64String(chunk.NB64), Convert.FromBase64String(chunk.DataB64), _key, _aadTag); }
            catch (FormatException) { pt = null; }
            if (pt == null) return null;
            chunk.DataB64 = Convert.ToBase64String(pt);
            chunk.E2e = null;
            chunk.NB64 = null;
            return env.With(JsonSerializer.Serialize(chunk));
        }
        return env;
    }

    /// <summary>True when a text/file_chunk payload is in E2E form.</summary>
    public static bool IsEncrypted(WireEnvelope env)
    {
        if (env.Type == NexShareMessageTypes.Text) return LooksEncryptedText(env.Payload);
        if (env.Type == NexShareMessageTypes.FileChunk) return TryParseChunk(env.Payload)?.E2e == 1;
        return false;
    }

    private static WireFileChunk? TryParseChunk(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<WireFileChunk>(json); }
        catch (JsonException) { return null; }
    }
}
