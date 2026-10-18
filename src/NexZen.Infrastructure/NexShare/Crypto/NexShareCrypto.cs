using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Infrastructure.NexShare.Crypto;

/// <summary>Port of Android NexShareCrypto.kt (+ ECDH from WifiDirectTransport.kt). See NEXSHARE_PC_INTEROP.md.</summary>
public class NexShareCrypto : INexShareCrypto
{
    public const string AadPrefix = "nexshare_e2e_v1:";
    public const string WfdKeyPrefix = "nexshare_wfd_v1:";
    public const string BleLinkKeyPrefix = "nexshare_ble_link_v1:";
    public const string LanHkdfInfo = "nexshare_e2e_v2";
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public (ECDiffieHellman PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (ecdh, ecdh.PublicKey.ExportSubjectPublicKeyInfo());
    }

    public byte[] DeriveSharedSecret(ECDiffieHellman privateKey, byte[] peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        if (peer.KeySize != 256) throw new CryptographicException("Peer key is not P-256");
        return privateKey.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>Unsigned lexicographic compare; shorter prefix sorts first (Android compareUnsigned).</summary>
    public static int CompareUnsigned(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i]) return a[i] - b[i];
        return a.Length - b.Length;
    }

    public string DeriveWfdSas(byte[] myPubKey, byte[] peerPubKey, byte[] myNonce, byte[] peerNonce, byte[] sharedSecret)
    {
        var mineFirst = CompareUnsigned(myPubKey, peerPubKey) <= 0;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(mineFirst ? myPubKey : peerPubKey);
        h.AppendData(mineFirst ? peerPubKey : myPubKey);
        h.AppendData(mineFirst ? myNonce : peerNonce);
        h.AppendData(mineFirst ? peerNonce : myNonce);
        h.AppendData(sharedSecret);
        var hash = h.GetHashAndReset();
        var n = (hash[0] << 16) | (hash[1] << 8) | hash[2];
        return (n % 1_000_000).ToString("D6");
    }

    public string WfdSessionId(byte[] myPubKey, byte[] peerPubKey)
    {
        var mineFirst = CompareUnsigned(myPubKey, peerPubKey) <= 0;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(mineFirst ? myPubKey : peerPubKey);
        h.AppendData(mineFirst ? peerPubKey : myPubKey);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    public byte[] DeriveWfdSessionKey(byte[] sharedSecret, string sessionId) => DerivePrefixed(sharedSecret, WfdKeyPrefix + sessionId);

    public byte[] DeriveBleLinkKey(byte[] sharedSecret, string sessionId) => DerivePrefixed(sharedSecret, BleLinkKeyPrefix + sessionId);

    private static byte[] DerivePrefixed(byte[] sharedSecret, string label)
    {
        // Not RFC HKDF: PRK = HMAC(key=secret, msg=label); OKM = HMAC(PRK, "info"||0x01).
        var prk = HMACSHA256.HashData(sharedSecret, Encoding.UTF8.GetBytes(label));
        var info = Encoding.UTF8.GetBytes("info");
        var msg = new byte[info.Length + 1];
        info.CopyTo(msg, 0);
        msg[^1] = 0x01;
        return HMACSHA256.HashData(prk, msg);
    }

    public byte[] DeriveLanSessionKey(string sessionId, string token, int cryptoVersion = 2)
    {
        if (cryptoVersion < 2) throw new ArgumentException("cryptoVersion < 2 not supported", nameof(cryptoVersion));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(token), 32,
            Encoding.UTF8.GetBytes(sessionId),
            Encoding.UTF8.GetBytes(LanHkdfInfo));
    }

    public (byte[] Nonce, byte[] CipherTextWithTag) Encrypt(byte[] plaintext, byte[] key, string aadTag, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceSize);
        if (nonce.Length != NonceSize) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
        var output = new byte[plaintext.Length + TagSize];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), Aad(aadTag));
        return (nonce, output);
    }

    public byte[]? TryDecrypt(byte[] nonce, byte[] cipherTextWithTag, byte[] key, string aadTag)
    {
        if (nonce.Length != NonceSize || cipherTextWithTag.Length < TagSize) return null;
        try
        {
            var ptLen = cipherTextWithTag.Length - TagSize;
            var pt = new byte[ptLen];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipherTextWithTag.AsSpan(0, ptLen), cipherTextWithTag.AsSpan(ptLen), pt, Aad(aadTag));
            return pt;
        }
        catch (CryptographicException) { return null; }
    }

    public string EncryptTextToJson(string plain, byte[] key, string aadTag, byte[]? nonce = null)
    {
        var (n, ct) = Encrypt(Encoding.UTF8.GetBytes(plain), key, aadTag, nonce);
        return JsonSerializer.Serialize(new WireE2eContent { E2e = 1, N = Convert.ToBase64String(n), Ct = Convert.ToBase64String(ct) });
    }

    public string? TryDecryptTextFromJson(string content, byte[] key, string aadTag)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("e2e", out var e) || e.ValueKind != JsonValueKind.Number || e.GetInt32() != 1) return null;
            if (!root.TryGetProperty("n", out var n) || !root.TryGetProperty("ct", out var ct)) return null;
            var nS = n.GetString();
            var ctS = ct.GetString();
            if (string.IsNullOrWhiteSpace(nS) || string.IsNullOrWhiteSpace(ctS)) return null;
            var pt = TryDecrypt(Convert.FromBase64String(nS), Convert.FromBase64String(ctS), key, aadTag);
            return pt == null ? null : Encoding.UTF8.GetString(pt);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    public string GenerateHmacSha256(string data, string secretKey)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    private static byte[] Aad(string tag) => Encoding.UTF8.GetBytes(AadPrefix + tag);
}
