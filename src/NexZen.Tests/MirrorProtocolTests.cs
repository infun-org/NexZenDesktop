using System.Security.Cryptography;
using System.Text;
using NexZen.Core.NexShare.Mirror;
using NexZen.Infrastructure.NexShare.Crypto;
using Xunit;

namespace NexZen.Tests;

public class MirrorProtocolTests
{
    private static (MirrorSessionCrypto Pc, MirrorSessionCrypto Phone) Pair(string sid = "s1")
    {
        var crypto = new NexShareCrypto();
        var (pcKey, pcPub) = crypto.GenerateKeyPair();
        var (phoneKey, phonePub) = crypto.GenerateKeyPair();
        var pcNonce = RandomNumberGenerator.GetBytes(16);
        var phoneNonce = RandomNumberGenerator.GetBytes(16);
        return (MirrorSessionCrypto.Derive(crypto, pcKey, pcPub, pcNonce, phonePub, phoneNonce, sid),
                MirrorSessionCrypto.Derive(crypto, phoneKey, phonePub, phoneNonce, pcPub, pcNonce, sid));
    }

    [Fact]
    public void Both_sides_show_the_same_code_and_frames_open()
    {
        var (pc, phone) = Pair();
        Assert.Equal(pc.Sas, phone.Sas);
        Assert.Matches("^[0-9]{6}$", pc.Sas);

        var frame = pc.Open(phone.Seal("s1", 1, MirrorFrameFlags.KeyFrame, new byte[] { 0, 0, 0, 1, 0x65 }));
        Assert.NotNull(frame);
        Assert.Equal(MirrorFrameFlags.KeyFrame, frame!.Value.Flags);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65 }, frame.Value.Payload);
    }

    [Fact]
    public void Replayed_forged_and_foreign_frames_are_dropped()
    {
        var (pc, phone) = Pair();
        var wire = phone.Seal("s1", 5, MirrorFrameFlags.None, Encoding.UTF8.GetBytes("x"));
        Assert.NotNull(pc.Open(wire));
        Assert.Null(pc.Open(wire)); // replay
        Assert.Null(pc.Open(phone.Seal("s1", 4, MirrorFrameFlags.None, new byte[1]))); // older

        var tampered = phone.Seal("s1", 6, MirrorFrameFlags.None, new byte[8]);
        var ct = System.Convert.FromBase64String(tampered.CipherB64);
        ct[3] ^= 1;
        tampered.CipherB64 = System.Convert.ToBase64String(ct);
        Assert.Null(pc.Open(tampered));

        var (_, otherPhone) = Pair(); // a different key exchange (e.g. a man in the middle)
        Assert.Null(pc.Open(otherPhone.Seal("s1", 7, MirrorFrameFlags.None, new byte[1])));
    }

    [Fact]
    public void Key_derivation_is_stable()
    {
        // Same construction as NexShareMirror.kt deriveKey (HMAC chain, prefix nexzen_mirror_v1:).
        var key = MirrorSessionCrypto.DeriveKey(new byte[32], "abc");
        Assert.Equal("0e7b909e", System.Convert.ToHexString(key)[..8].ToLowerInvariant());
    }
}

public class MirrorInputTests
{
    [Fact]
    public void Input_is_sealed_for_the_phone_and_cannot_be_replayed_or_passed_off_as_a_frame()
    {
        var crypto = new NexShareCrypto();
        var (pcKey, pcPub) = crypto.GenerateKeyPair();
        var (phoneKey, phonePub) = crypto.GenerateKeyPair();
        var n1 = RandomNumberGenerator.GetBytes(16);
        var n2 = RandomNumberGenerator.GetBytes(16);
        var pc = MirrorSessionCrypto.Derive(crypto, pcKey, pcPub, n1, phonePub, n2, "s");
        var phone = MirrorSessionCrypto.Derive(crypto, phoneKey, phonePub, n2, pcPub, n1, "s");

        var wire = pc.SealInput("s", Encoding.UTF8.GetBytes("{\"t\":\"key\",\"k\":\"home\"}"));
        long last = 0;
        Assert.Equal("{\"t\":\"key\",\"k\":\"home\"}", Encoding.UTF8.GetString(phone.OpenInput(wire, ref last)!));
        Assert.Null(phone.OpenInput(wire, ref last)); // replay
        Assert.Null(phone.Open(wire));                // not valid as a video frame (different tag)
    }
}
