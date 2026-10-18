using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexZen.Core.NexShare.Crypto;
using NexZen.Core.NexShare.Models;
using NexZen.Infrastructure.NexShare.Crypto;
using Xunit;

namespace NexZen.Tests;

/// <summary>
/// Vectors produced by vectors/Vectors.java (JDK, same algorithms as Android NexShareCrypto.kt).
/// Regenerate: /opt/android-studio/jbr/bin/java vectors/Vectors.java > vectors/vectors.json
/// </summary>
public class CryptoVectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "vectors.json"))).RootElement;
    private static string S(string k) => V.GetProperty(k).GetString()!;
    private static byte[] B(string k) => Convert.FromBase64String(S(k));
    private static byte[] H(string k) => Convert.FromHexString(S(k));
    private static ECDiffieHellman Priv(string k)
    {
        var e = ECDiffieHellman.Create();
        e.ImportPkcs8PrivateKey(B(k), out _);
        return e;
    }

    private readonly NexShareCrypto _c = new();

    [Fact]
    public void BleLinkKey_AndBleUrlCiphertext_MatchJava()
    {
        var bk = _c.DeriveBleLinkKey(H("secret"), S("sessionId"));
        Assert.Equal(H("bleLinkKey"), bk);
        Assert.NotEqual(H("wfdKey"), bk);
        var (_, ct) = _c.Encrypt(Encoding.UTF8.GetBytes(S("bleUrlPlain")), bk, S("sessionId"), B("gcmNonce"));
        Assert.Equal(B("ctBleUrl"), ct);
        var url = JsonSerializer.Deserialize<WireBleUrl>(S("bleUrlPlain"))!;
        Assert.Equal("https://example.com/x", url.Url);
        Assert.Equal("CPH2481", url.DeviceName);
    }

    [Fact]
    public void SharedSecret_MatchesJava_BothDirections()
    {
        using var a = Priv("privA_pkcs8");
        using var b = Priv("privB_pkcs8");
        Assert.Equal(H("secret"), _c.DeriveSharedSecret(a, B("pubB")));
        Assert.Equal(H("secret"), _c.DeriveSharedSecret(b, B("pubA")));
        Assert.Equal(B("pubA"), a.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(91, B("pubA").Length);
    }

    [Fact]
    public void Sas_MatchesJava_AndIsSymmetric()
    {
        var sec = H("secret");
        Assert.Equal(S("sas"), _c.DeriveWfdSas(B("pubA"), B("pubB"), B("nonceA"), B("nonceB"), sec));
        Assert.Equal(S("sas"), _c.DeriveWfdSas(B("pubB"), B("pubA"), B("nonceB"), B("nonceA"), sec));
    }

    [Fact]
    public void SessionId_AndWfdKey_MatchJava()
    {
        Assert.Equal(S("sessionId"), _c.WfdSessionId(B("pubA"), B("pubB")));
        Assert.Equal(S("sessionId"), _c.WfdSessionId(B("pubB"), B("pubA")));
        Assert.Equal(H("wfdKey"), _c.DeriveWfdSessionKey(H("secret"), S("sessionId")));
    }

    [Fact]
    public void LanKey_MatchesJava()
    {
        Assert.Equal(H("lanKey"), _c.DeriveLanSessionKey(S("lanSessionId"), S("lanToken")));
        Assert.Throws<ArgumentException>(() => _c.DeriveLanSessionKey("s", "t", 1));
    }

    [Fact]
    public void Ciphertexts_MatchJava()
    {
        var wk = H("wfdKey");
        var sid = S("sessionId");
        var n = B("gcmNonce");
        Assert.Equal(B("ctText"), _c.Encrypt(Encoding.UTF8.GetBytes(S("text")), wk, sid, n).CipherTextWithTag);
        Assert.Equal(B("ctChunk"), _c.Encrypt(B("chunkPlain"), wk, sid, n).CipherTextWithTag);
        Assert.Equal(B("ctLanText"), _c.Encrypt(Encoding.UTF8.GetBytes(S("text")), H("lanKey"), S("lanSessionId"), n).CipherTextWithTag);
        Assert.Equal(S("text"), Encoding.UTF8.GetString(_c.TryDecrypt(n, B("ctText"), wk, sid)!));
    }

    [Fact]
    public void DecryptsAndroidStyleTextJson()
    {
        var json = "{\"e2e\":1,\"n\":\"" + S("gcmNonce") + "\",\"ct\":\"" + S("ctText") + "\"}";
        Assert.Equal(S("text"), _c.TryDecryptTextFromJson(json, H("wfdKey"), S("sessionId")));
        Assert.Null(_c.TryDecryptTextFromJson(json, H("wfdKey"), "wrong-sid"));
        Assert.Null(_c.TryDecryptTextFromJson("hello", H("wfdKey"), S("sessionId")));
    }

    [Fact]
    public void EnvelopeCipher_TextAndChunk_RoundTrip_AndMatchJava()
    {
        var cipher = new EnvelopeCipher(_c, H("wfdKey"), S("sessionId"));
        var text = new WireEnvelope { Type = NexShareMessageTypes.Text, Payload = S("text"), Seq = 7 };
        var encText = cipher.Encrypt(text, B("gcmNonce"));
        Assert.True(EnvelopeCipher.IsEncrypted(encText));
        var obj = JsonDocument.Parse(encText.Payload).RootElement;
        Assert.Equal(1, obj.GetProperty("e2e").GetInt32());
        Assert.Equal(S("ctText"), obj.GetProperty("ct").GetString());
        Assert.Equal(S("text"), cipher.Decrypt(encText)!.Payload);
        Assert.Equal(7, encText.Seq);
        Assert.Equal(S("text"), text.Payload); // input not mutated

        var chunk = new WireFileChunk { TransferId = "t1", Offset = 0, DataB64 = S("chunkPlain"), IsLast = true, Sha256Hex = "ab" };
        var env = new WireEnvelope { Type = NexShareMessageTypes.FileChunk, Payload = JsonSerializer.Serialize(chunk) };
        var enc = cipher.Encrypt(env, B("gcmNonce"));
        var ec = JsonSerializer.Deserialize<WireFileChunk>(enc.Payload)!;
        Assert.Equal(1, ec.E2e);
        Assert.Equal(S("gcmNonce"), ec.NB64);
        Assert.Equal(S("ctChunk"), ec.DataB64);
        var dec = JsonSerializer.Deserialize<WireFileChunk>(cipher.Decrypt(enc)!.Payload)!;
        Assert.Equal(S("chunkPlain"), dec.DataB64);
        Assert.Null(dec.E2e);
        Assert.DoesNotContain("nB64", cipher.Decrypt(enc)!.Payload);

        var wrong = new EnvelopeCipher(_c, H("lanKey"), S("sessionId"));
        Assert.Null(wrong.Decrypt(enc));
        Assert.Null(wrong.Decrypt(encText));
        var ping = new WireEnvelope { Type = NexShareMessageTypes.Ping, Payload = "x" };
        Assert.Same(ping, cipher.Encrypt(ping));
    }

    [Fact]
    public void FreshKeys_EndToEnd()
    {
        var (a, pa) = _c.GenerateKeyPair();
        var (b, pb) = _c.GenerateKeyPair();
        using (a) using (b)
        {
            var sa = _c.DeriveSharedSecret(a, pb);
            Assert.Equal(sa, _c.DeriveSharedSecret(b, pa));
            var na = RandomNumberGenerator.GetBytes(16);
            var nb = RandomNumberGenerator.GetBytes(16);
            Assert.Equal(_c.DeriveWfdSas(pa, pb, na, nb, sa), _c.DeriveWfdSas(pb, pa, nb, na, sa));
            var sid = _c.WfdSessionId(pa, pb);
            var key = _c.DeriveWfdSessionKey(sa, sid);
            var json = _c.EncryptTextToJson("hi", key, sid);
            Assert.Equal("hi", _c.TryDecryptTextFromJson(json, key, sid));
        }
    }

    [Fact]
    public void CompareUnsigned_Semantics()
    {
        Assert.True(NexShareCrypto.CompareUnsigned(new byte[] { 0x7F }, new byte[] { 0x80 }) < 0);
        Assert.True(NexShareCrypto.CompareUnsigned(new byte[] { 1 }, new byte[] { 1, 0 }) < 0);
        Assert.Equal(0, NexShareCrypto.CompareUnsigned(new byte[] { 9, 9 }, new byte[] { 9, 9 }));
    }

    [Fact]
    public void WireEnvelope_Json_HasVersionAndOptionalSeq()
    {
        var j = JsonSerializer.Serialize(new WireEnvelope { Type = "text", Payload = "x" });
        Assert.Contains("\"version\":1", j);
        Assert.DoesNotContain("\"seq\"", j);
        var back = JsonSerializer.Deserialize<WireEnvelope>("{\"type\":\"text\",\"version\":1,\"seq\":42,\"content\":\"x\"}")!;
        Assert.Equal(42, back.Seq);
        var legacy = JsonSerializer.Deserialize<WireEnvelope>("{\"type\":\"text\",\"content\":\"x\"}")!;
        Assert.Null(legacy.Seq);
    }
}
