using System.Security.Cryptography;

namespace NexZen.Core.NexShare.Interfaces;

/// <summary>
/// NexShare crypto, byte-for-byte compatible with Android NexShareCrypto.kt / WifiDirectTransport.kt.
/// Keys: P-256, public keys as X.509 SubjectPublicKeyInfo DER. Shared secret: raw ECDH X coordinate (32 bytes).
/// </summary>
public interface INexShareCrypto
{
    /// <summary>Generates a new P-256 key pair; PublicKey is SPKI DER (what Android puts, base64'd, in ecdh_hello.pubKey).</summary>
    (ECDiffieHellman PrivateKey, byte[] PublicKey) GenerateKeyPair();

    /// <summary>Raw ECDH shared secret (X coordinate, 32 bytes) = Java KeyAgreement("ECDH").generateSecret().</summary>
    byte[] DeriveSharedSecret(ECDiffieHellman privateKey, byte[] peerPublicKey);

    /// <summary>6-digit SAS: SHA256(pubA||pubB||nonceA||nonceB||secret), pairs ordered by unsigned pubkey bytes; first 3 bytes BE mod 1e6, "D6".</summary>
    string DeriveWfdSas(byte[] myPubKey, byte[] peerPubKey, byte[] myNonce, byte[] peerNonce, byte[] sharedSecret);

    /// <summary>Lowercase hex SHA256(sortedPubA||sortedPubB).</summary>
    string WfdSessionId(byte[] myPubKey, byte[] peerPubKey);

    /// <summary>PRK = HMAC(secret, "nexshare_wfd_v1:"+sid); key = HMAC(PRK, "info"||0x01). 32 bytes.</summary>
    byte[] DeriveWfdSessionKey(byte[] sharedSecret, string sessionId);

    /// <summary>BLE link key: same construction as the WFD key with prefix "nexshare_ble_link_v1:". 32 bytes.</summary>
    byte[] DeriveBleLinkKey(byte[] sharedSecret, string sessionId);

    /// <summary>QR/LAN E2E key: HKDF-SHA256(IKM=token, salt=sessionId, info="nexshare_e2e_v2", L=32). cryptoVersion &lt; 2 is rejected like Android.</summary>
    byte[] DeriveLanSessionKey(string sessionId, string token, int cryptoVersion = 2);

    /// <summary>AES-256-GCM, 12-byte nonce, returns ct||tag(16); AAD = "nexshare_e2e_v1:"+aadTag. Random nonce when null.</summary>
    (byte[] Nonce, byte[] CipherTextWithTag) Encrypt(byte[] plaintext, byte[] key, string aadTag, byte[]? nonce = null);

    /// <summary>Returns null on any failure (auth tag mismatch, bad lengths).</summary>
    byte[]? TryDecrypt(byte[] nonce, byte[] cipherTextWithTag, byte[] key, string aadTag);

    /// <summary>Text → {"e2e":1,"n":b64,"ct":b64}.</summary>
    string EncryptTextToJson(string plain, byte[] key, string aadTag, byte[]? nonce = null);

    /// <summary>Inverse of <see cref="EncryptTextToJson"/>; null if not E2E JSON or decryption fails.</summary>
    string? TryDecryptTextFromJson(string content, byte[] key, string aadTag);

    /// <summary>HMAC-SHA256(key=utf8(secretKey), utf8(data)) as base64 (unchanged legacy helper).</summary>
    string GenerateHmacSha256(string data, string secretKey);
}
