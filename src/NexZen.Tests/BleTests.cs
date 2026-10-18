using System.Text;
using NexZen.Core.NexShare.Ble;
using NexZen.Core.NexShare.Models;
using NexZen.Infrastructure.NexShare.Crypto;
using Xunit;

namespace NexZen.Tests;

public class BleFramingTests
{
    [Fact]
    public void SingleFragment_HasFlags03()
    {
        var frames = NexShareBleFraming.Split(Encoding.UTF8.GetBytes("hi"), 23);
        Assert.Single(frames);
        Assert.Equal(new byte[] { 0x03, (byte)'h', (byte)'i' }, frames[0]);
    }

    [Fact]
    public void EmptyMessage_IsOneFrame()
    {
        var frames = NexShareBleFraming.Split(Array.Empty<byte>(), 23);
        Assert.Equal(new byte[] { 0x03 }, Assert.Single(frames));
    }

    [Theory]
    [InlineData(23, 1000)]
    [InlineData(185, 4000)]
    [InlineData(517, 513)]
    [InlineData(517, 514)]
    public void Split_UsesMtuMinus4_AndReassembles(int mtu, int size)
    {
        var msg = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var frames = NexShareBleFraming.Split(msg, mtu);
        var maxFrame = Math.Min(mtu - 3, NexShareBleFraming.MaxAttValue);
        Assert.All(frames, f => Assert.True(f.Length <= maxFrame));
        Assert.Equal(0x01, frames[0][0] & 0x01);
        Assert.Equal(0x02, frames[^1][0] & 0x02);
        Assert.All(frames.Skip(1).SkipLast(1), f => Assert.Equal(0, f[0]));
        var pl = maxFrame - 1;
        Assert.Equal((size + pl - 1) / pl, frames.Count);
        var r = new NexShareBleFraming.Reassembler();
        byte[]? got = null;
        foreach (var f in frames) got = r.Feed(f) ?? got;
        Assert.Equal(msg, got);
    }

    [Fact]
    public void Reassembler_DropsFramesWithoutFirst_AndResetsOnFirst()
    {
        var r = new NexShareBleFraming.Reassembler();
        Assert.Null(r.Feed(new byte[] { 0x02, 1, 2 }));     // LAST without FIRST
        Assert.Null(r.Feed(new byte[] { 0x01, 9 }));        // start, then restart
        Assert.Equal(new byte[] { 7, 8 }, r.Feed(new byte[] { 0x03, 7, 8 }));
        Assert.Null(r.Feed(Array.Empty<byte>()));
    }

    [Fact]
    public void Reassembler_RejectsOver64KiB()
    {
        var r = new NexShareBleFraming.Reassembler();
        var chunk = new byte[1001];
        chunk[0] = 0x01;
        Assert.Null(r.Feed(chunk));
        chunk[0] = 0x00;
        for (var i = 0; i < 70; i++) Assert.Null(r.Feed(chunk));
        Assert.Null(r.Feed(new byte[] { 0x02, 1 })); // message was discarded
    }
}

public class BleInfoTests
{
    // Values printed by the JDK: s.hashCode()
    [Theory]
    [InlineData("hello", 99162322)]
    [InlineData("2ac23007-a255-4213-bd93-88133edfb838", 1325831494)]
    [InlineData("", 0)]
    [InlineData("Xin chào 👋", 1164810999)]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015", -1765470272)]
    public void JavaHashCode_MatchesJdk(string s, int expected) =>
        Assert.Equal(expected, NexShareBleInfoCodec.JavaHashCode(s));

    [Fact]
    public void ManufacturerBytes_AreBigEndian()
    {
        Assert.Equal(new byte[] { 0x05, 0xE9, 0x18, 0xD2 }, NexShareBleInfoCodec.ManufacturerBytes("hello")); // 99162322
        var neg = NexShareBleInfoCodec.ManufacturerBytes("9f86d081884c7d659a2feaa0c55ad015");
        Assert.Equal(-1765470272, (neg[0] << 24) | (neg[1] << 16) | (neg[2] << 8) | neg[3]);
    }

    [Fact]
    public void Parse_LegacyAndCaps()
    {
        var legacy = NexShareBleInfoCodec.Parse("abc|43201")!;
        Assert.Equal("abc", legacy.DeviceId);
        Assert.Equal(43201, legacy.WsPort);
        Assert.Empty(legacy.Caps);

        var full = NexShareBleInfoCodec.Parse("abc|0|wfd_legacy, wfd_e2e,,wfd_go")!;
        Assert.Equal(new[] { "wfd_e2e", "wfd_go", "wfd_legacy" }, full.Caps.OrderBy(c => c));
        Assert.Equal(0, NexShareBleInfoCodec.Parse("abc|x|")!.WsPort);
        Assert.Null(NexShareBleInfoCodec.Parse("justid"));
        Assert.Null(NexShareBleInfoCodec.Parse(null));
    }

    [Fact]
    public void Build_MatchesAndroidFormat_AndHasNoGoCap()
    {
        var s = NexShareBleInfoCodec.Build("pc1", 43000, NexShareBleInfoCodec.LocalCaps);
        Assert.Equal("pc1|43000|wfd_legacy,wfd_e2e,ble_link,ble_link_pull", s);
        Assert.DoesNotContain("wfd_go", s);
    }

    [Fact]
    public void Merge_KeepsLanAndBleFacts()
    {
        var lan = new DiscoveryMessage { DeviceId = "d", DeviceName = "Phone", Host = "192.168.1.5", TcpPort = 43201 };
        var ble = new DiscoveryMessage { DeviceId = "d", DeviceName = "CPH2481", ViaBle = true, BleAddress = "AA:BB", Caps = new() { "wfd_legacy" }, TcpPort = 43201 };
        var m1 = DiscoveryMessage.Merge(lan, ble);
        Assert.Equal("192.168.1.5", m1.Host);
        Assert.Equal("Phone", m1.DeviceName);
        Assert.Equal("AA:BB", m1.BleAddress);
        Assert.True(m1.ViaBle);
        var m2 = DiscoveryMessage.Merge(m1, lan);
        Assert.Equal("AA:BB", m2.BleAddress);
        Assert.Contains("wfd_legacy", m2.Caps);
        Assert.Equal("192.168.1.5", m2.Host);
        Assert.Same(ble, DiscoveryMessage.Merge(null, ble));
    }
}

public class BleHandshakeSessionTests
{
    private sealed class Pair
    {
        public NexShareBleHandshakeSession A = null!, B = null!;
        public string? SasA, SasB;
        public Action? ConfirmA, ConfirmB, RejectA;
        public bool VerifiedA, VerifiedB;
        public WireP2pCredentials? CredsAtA;
        public string? FailA, FailB;
        public int MtuAtoB = 23, MtuBtoA = 185;
    }

    private static Pair Build()
    {
        var c = new NexShareCrypto();
        var p = new Pair();
        // A = PC initiator, B = phone responder; byte transport via framing at different MTUs.
        p.A = new NexShareBleHandshakeSession(c, "pc-id", true,
            bytes => { foreach (var f in NexShareBleFraming.Split(bytes, p.MtuAtoB)) p.B.OnFrame(f); }, () => { });
        p.B = new NexShareBleHandshakeSession(c, "phone-id", false,
            bytes => { foreach (var f in NexShareBleFraming.Split(bytes, p.MtuBtoA)) p.A.OnFrame(f); }, () => { },
            localCaps: new[] { "wfd_legacy", "wfd_e2e", "wfd_go" });
        p.A.SasRequested += (_, code, ok, no) => { p.SasA = code; p.ConfirmA = ok; p.RejectA = no; };
        p.B.SasRequested += (_, code, ok, _) => { p.SasB = code; p.ConfirmB = ok; };
        p.A.Verified += _ => p.VerifiedA = true;
        p.B.Verified += _ => p.VerifiedB = true;
        p.A.CredentialsReceived += (_, cr) => p.CredsAtA = cr;
        p.A.Failed += (_, r) => p.FailA = r;
        p.B.Failed += (_, r) => p.FailB = r;
        return p;
    }

    [Fact]
    public void FullHandshake_SameSas_VerifiesAndDeliversCredentials()
    {
        var p = Build();
        p.A.Start();
        Assert.NotNull(p.SasA);
        Assert.Equal(p.SasA, p.SasB);
        Assert.Matches("^[0-9]{6}$", p.SasA!);
        Assert.Equal(p.A.SessionId, p.B.SessionId);
        Assert.Equal(p.A.SessionKey, p.B.SessionKey);
        Assert.Contains("wfd_go", p.A.PeerCaps);
        Assert.Equal("phone-id", p.A.PeerId);
        Assert.Equal("pc-id", p.B.PeerId);

        p.ConfirmA!();
        Assert.False(p.VerifiedA);
        p.ConfirmB!();
        Assert.True(p.VerifiedA);
        Assert.True(p.VerifiedB);

        p.B.SendCredentials(new WireP2pCredentials { Ssid = "DIRECT-NX-1A2B", Passphrase = "secretpass", GoIp = "192.168.49.1", Port = 25556 });
        Assert.NotNull(p.CredsAtA);
        Assert.Equal("DIRECT-NX-1A2B", p.CredsAtA!.Ssid);
        Assert.Equal("secretpass", p.CredsAtA.Passphrase);
        Assert.Equal(25556, p.CredsAtA.Port);
        Assert.True(p.A.IsFinished);
        Assert.Null(p.FailA);
    }

    [Fact]
    public void Reject_FailsBothSides()
    {
        var p = Build();
        p.A.Start();
        p.RejectA!();
        Assert.Equal("SAS rejected locally", p.FailA);
        Assert.Equal("SAS rejected by peer", p.FailB);
    }

    [Fact]
    public void CredentialsBeforeVerification_Fail()
    {
        var p = Build();
        p.A.Start();
        p.B.SendCredentials(new WireP2pCredentials { Ssid = "x" });
        Assert.Equal("credentials before verification", p.FailB);
        Assert.Null(p.CredsAtA);
    }

    [Fact]
    public void SasPrompt_IsIdempotent()
    {
        int ok = 0, no = 0;
        var s = new SasPrompt("p", null, "233994", () => ok++, () => no++);
        Assert.Equal("233 994", s.DisplayCode);
        s.Confirm(); s.Reject(); s.Confirm();
        Assert.Equal(1, ok);
        Assert.Equal(0, no);
    }
}
