using System;
using System.Collections.Generic;
using System.Text.Json;
using NexZen.Core.NexShare;
using NexZen.Core.NexShare.Ble;
using NexZen.Core.NexShare.Models;
using NexZen.Infrastructure.NexShare.Crypto;
using Xunit;

namespace NexZen.Tests;

/// <summary>BLE-only link delivery (ecdh_hello intent=link, SAS, ble_url, ble_url_ack) without Wi-Fi Direct.</summary>
public class BleLinkTests
{
    private sealed class Pair
    {
        public NexShareBleHandshakeSession S = null!, R = null!;
        public Action? ConfirmS, ConfirmR;
        public WireBleUrl? Received;
        public bool Delivered, ClosedS, ClosedR;
        public WireP2pCredentials? CredsAtS;
        public bool VerifiedR;
        public string? FailS, FailR;
        public readonly List<string> TypesStoR = new();
    }

    private static string TypeOf(byte[] msg) =>
        JsonDocument.Parse(msg).RootElement.GetProperty("type").GetString() ?? "";

    /// <summary>S = sender/initiator (e.g. phone central or PC central), R = receiver/responder.</summary>
    private static Pair Build(IReadOnlyList<string> receiverCaps, bool trusted = false, int mtu = 23)
    {
        var c = new NexShareCrypto();
        var p = new Pair();
        p.S = new NexShareBleHandshakeSession(c, "sender-id", true,
            bytes => { p.TypesStoR.Add(TypeOf(bytes)); foreach (var f in NexShareBleFraming.Split(bytes, mtu)) p.R.OnFrame(f); },
            () => p.ClosedS = true,
            localCaps: new[] { "wfd_legacy", "wfd_e2e", "ble_link" }, isTrusted: _ => trusted);
        p.R = new NexShareBleHandshakeSession(c, "receiver-id", false,
            bytes => { foreach (var f in NexShareBleFraming.Split(bytes, mtu)) p.S.OnFrame(f); },
            () => p.ClosedR = true,
            localCaps: receiverCaps, isTrusted: _ => trusted)
        { AckFlushDelay = TimeSpan.Zero };
        p.S.SasRequested += (_, _, ok, _) => p.ConfirmS = ok;
        p.R.SasRequested += (_, _, ok, _) => p.ConfirmR = ok;
        p.S.LinkDelivered += _ => p.Delivered = true;
        p.R.LinkReceived += (_, l) => p.Received = l;
        p.R.Verified += _ => p.VerifiedR = true;
        p.S.CredentialsReceived += (_, cr) => p.CredsAtS = cr;
        p.S.Failed += (_, r) => p.FailS = r;
        p.R.Failed += (_, r) => p.FailR = r;
        p.S.PendingLink = new WireBleUrl { Url = "https://example.com/a?b=c&đ=1", Title = "Tiêu đề", DeviceName = "NexZen PC" };
        return p;
    }

    [Fact]
    public void LinkOverBle_AfterSas_DeliversAndAcks_NoCredentials()
    {
        var p = Build(new[] { "wfd_legacy", "wfd_e2e", "wfd_go", "ble_link" }, mtu: 23);
        p.S.Start();
        Assert.Equal("link", p.R.PeerIntent);
        Assert.True(p.R.IsLinkOnly);
        p.ConfirmR!();
        Assert.Null(p.Received);
        p.ConfirmS!();
        Assert.True(p.S.IsLinkOnly);
        Assert.NotNull(p.Received);
        Assert.Equal("https://example.com/a?b=c&đ=1", p.Received!.Url);
        Assert.Equal("Tiêu đề", p.Received.Title);
        Assert.Equal("NexZen PC", p.Received.DeviceName);
        Assert.True(p.Delivered);
        Assert.True(p.S.IsFinished);
        Assert.True(p.R.IsFinished);
        Assert.True(p.ClosedS && p.ClosedR);
        Assert.Null(p.CredsAtS);
        Assert.Null(p.FailS); Assert.Null(p.FailR);
        Assert.Contains(NexShareMessageTypes.BleUrl, p.TypesStoR);
        Assert.DoesNotContain(NexShareMessageTypes.P2pCredentials, p.TypesStoR);
    }

    /// <summary>
    /// Phone→PC pull: PC is GATT client (initiator) with no PendingLink; the phone (responder) holds the URL
    /// and sends ble_url after SAS. Mirrors NexShareBleDiscovery.startLinkPull + PC RunInitiatorAsync(expectIncomingLink).
    /// </summary>
    [Fact]
    public void LinkOverBle_PullPolarity_ResponderSends()
    {
        var c = new NexShareCrypto();
        var p = new Pair();
        p.S = new NexShareBleHandshakeSession(c, "pc-id", true,
            bytes => { p.TypesStoR.Add(TypeOf(bytes)); foreach (var f in NexShareBleFraming.Split(bytes, 185)) p.R.OnFrame(f); },
            () => p.ClosedS = true,
            localCaps: new[] { "wfd_legacy", "wfd_e2e", "ble_link", "ble_link_pull" }, isTrusted: _ => true)
        { ExpectIncomingLink = true, AckFlushDelay = TimeSpan.Zero };
        p.R = new NexShareBleHandshakeSession(c, "phone-id", false,
            bytes => { foreach (var f in NexShareBleFraming.Split(bytes, 185)) p.S.OnFrame(f); },
            () => p.ClosedR = true,
            localCaps: new[] { "wfd_legacy", "wfd_e2e", "wfd_go", "ble_link" }, isTrusted: _ => true)
        { AckFlushDelay = TimeSpan.Zero, PendingLink = new WireBleUrl { Url = "https://phone.example/x", Title = "From phone", DeviceName = "CPH2481" } };
        p.S.LinkReceived += (_, l) => p.Received = l;
        p.R.LinkDelivered += _ => p.Delivered = true;
        p.S.Failed += (_, r) => p.FailS = r;
        p.R.Failed += (_, r) => p.FailR = r;

        p.S.Start();
        Assert.Equal("link", p.S.PeerIntent);
        Assert.True(p.S.IsLinkOnly);
        Assert.True(p.R.IsLinkOnly);
        Assert.NotNull(p.Received);
        Assert.Equal("https://phone.example/x", p.Received!.Url);
        Assert.Equal("From phone", p.Received.Title);
        Assert.True(p.Delivered);
        Assert.True(p.S.IsFinished && p.R.IsFinished);
        Assert.Null(p.FailS); Assert.Null(p.FailR);
        Assert.DoesNotContain(NexShareMessageTypes.P2pCredentials, p.TypesStoR);
    }

    [Fact]
    public void ExpectIncomingLink_IsLinkOnly_WithoutPeerIntent()
    {
        var c = new NexShareCrypto();
        var s = new NexShareBleHandshakeSession(c, "pc", true, _ => { }, () => { },
            localCaps: new[] { "ble_link" });
        Assert.False(s.IsLinkOnly);
        s.ExpectIncomingLink = true;
        Assert.True(s.IsLinkOnly);
    }

    [Fact]
    public void LinkOverBle_TrustedPeer_SkipsSas()
    {
        var p = Build(new[] { "wfd_legacy", "ble_link" }, trusted: true);
        p.S.Start();
        Assert.Null(p.ConfirmS); Assert.Null(p.ConfirmR);
        Assert.True(p.Delivered);
        Assert.Equal("https://example.com/a?b=c&đ=1", p.Received!.Url);
    }

    [Fact]
    public void PeerWithoutBleLink_FallsBackToCredentials()
    {
        var p = Build(new[] { "wfd_legacy", "wfd_e2e", "wfd_go" }, trusted: true);
        p.S.Start();
        Assert.True(p.VerifiedR);
        Assert.False(p.S.IsLinkOnly);
        Assert.False(p.R.IsLinkOnly);
        Assert.DoesNotContain(NexShareMessageTypes.BleUrl, p.TypesStoR);
        Assert.False(p.S.IsFinished);
        p.R.SendCredentials(new WireP2pCredentials { Ssid = "DIRECT-NX-1234", Passphrase = "pw12345678", GoIp = "192.168.49.1", Port = 25556 });
        Assert.Equal("DIRECT-NX-1234", p.CredsAtS!.Ssid);
        Assert.False(p.Delivered);
    }

    [Fact]
    public void BleUrl_UsesBleLinkKey_NotWfdKey()
    {
        var p = Build(new[] { "ble_link" }, trusted: true);
        p.S.PendingLink = null; // handshake only
        p.S.Start();
        Assert.NotNull(p.S.BleLinkKey);
        Assert.Equal(p.S.BleLinkKey, p.R.BleLinkKey);
        Assert.NotEqual(p.S.SessionKey, p.S.BleLinkKey);
    }

    [Fact]
    public void BleUrl_BeforeVerification_Fails()
    {
        var p = Build(new[] { "ble_link" });
        p.S.Start(); // SAS pending on both sides
        var c = new NexShareCrypto();
        var enc = c.EncryptTextToJson("{\"url\":\"https://x\"}", p.S.BleLinkKey!, p.S.SessionId!);
        var env = new WireEnvelope { Type = NexShareMessageTypes.BleUrl, SenderId = "sender-id", Payload = enc };
        foreach (var f in NexShareBleFraming.Split(JsonSerializer.SerializeToUtf8Bytes(env), 185)) p.R.OnFrame(f);
        Assert.Null(p.Received);
        Assert.NotNull(p.FailR);
    }

    [Fact]
    public void Hello_WithoutLink_HasNoIntentField()
    {
        var json = JsonSerializer.Serialize(new WireEcdhHello { PubKey = "a", Nonce = "b", Caps = new() { "ble_link" } });
        Assert.DoesNotContain("intent", json);
        var withIntent = JsonSerializer.Serialize(new WireEcdhHello { PubKey = "a", Nonce = "b", Intent = "link" });
        Assert.Contains("\"intent\":\"link\"", withIntent);
    }

    [Fact]
    public void LinkRouting_LanThenBleDirectThenWfd()
    {
        var lanBle = new DiscoveryMessage { DeviceId = "p", Host = "192.168.1.5", TcpPort = 43000, BleAddress = "AA:BB", Caps = new() { "wfd_legacy", "ble_link" } };
        Assert.Equal(NexShareRoute.Lan, NexShareRouting.ChooseForLink(lanBle, false, true, true));
        Assert.Equal(NexShareRoute.BleDirect, NexShareRouting.ChooseForLink(lanBle, true, true, true));
        var bleOnly = new DiscoveryMessage { DeviceId = "p", BleAddress = "AA:BB", Caps = new() { "wfd_legacy", "ble_link" } };
        Assert.Equal(NexShareRoute.BleDirect, NexShareRouting.ChooseForLink(bleOnly, false, false, true));
        var oldPhone = new DiscoveryMessage { DeviceId = "p", BleAddress = "AA:BB", Caps = new() { "wfd_legacy" } };
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.ChooseForLink(oldPhone, false, false, true));
        Assert.Equal(NexShareRoute.None, NexShareRouting.ChooseForLink(bleOnly, false, false, false));
        // Files/chat routing unchanged
        Assert.Equal(NexShareRoute.BleWifiDirect, NexShareRouting.Choose(bleOnly, false, false, true));
    }
}
