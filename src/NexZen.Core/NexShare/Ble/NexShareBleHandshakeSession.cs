using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Ble;

/// <summary>
/// Transport-agnostic BLE handshake, a port of Android NexShareBleHandshakeSession:
/// ecdh_hello &lt;-&gt; ecdh_hello, SAS confirm on both sides (sas_confirm / sas_reject),
/// then the group owner sends encrypted p2p_credentials. Thread-safe (single lock).
/// </summary>
public sealed class NexShareBleHandshakeSession
{
    private readonly object _gate = new();
    private readonly INexShareCrypto _crypto;
    private readonly string _myDeviceId;
    private readonly bool _isInitiator;
    private readonly Action<byte[]> _sendMessage;
    private readonly Action _closeLink;
    private readonly IReadOnlyList<string> _localCaps;
    private readonly Func<string, bool> _isTrusted;
    private readonly NexShareBleFraming.Reassembler _reassembler = new();

    private readonly ECDiffieHellman _key;
    private readonly byte[] _myPub;
    private readonly byte[] _myNonce = RandomNumberGenerator.GetBytes(16);

    private bool _helloSent, _localConfirmed, _remoteConfirmed, _verified, _finished, _linkSent;

    public string? PeerId { get; private set; }
    public string? PeerIdHint { get; set; }
    public IReadOnlyList<string> PeerCaps { get; private set; } = Array.Empty<string>();
    public byte[]? SessionKey { get; private set; }
    public string? SessionId { get; private set; }
    public string? SasCode { get; private set; }
    /// <summary>Key for ble_url (DeriveBleLinkKey), distinct from the WFD session key.</summary>
    public byte[]? BleLinkKey { get; private set; }
    /// <summary>Peer ecdh_hello intent ("link" = it only delivers a link over BLE).</summary>
    public string? PeerIntent { get; private set; }
    /// <summary>Link to deliver over BLE after verification (set before <see cref="Start"/>). Sent only if the peer has ble_link.</summary>
    public WireBleUrl? PendingLink { get; set; }
    /// <summary>
    /// Pull initiator: we connected because the peer advertised a link for us. Wait for ble_url even if its hello
    /// omits intent=link (stale GATT session on the phone). Set before <see cref="Start"/>.
    /// </summary>
    public bool ExpectIncomingLink { get; set; }
    /// <summary>True when this session ends with ble_url/ble_url_ack instead of Wi-Fi Direct credentials.</summary>
    public bool IsLinkOnly =>
        (PendingLink != null && PeerCaps.Contains(NexShareCaps.BleLink))
        || ((PeerIntent == NexShareHelloIntent.Link || ExpectIncomingLink) && _localCaps.Contains(NexShareCaps.BleLink));
    /// <summary>Delay before closing after ble_url_ack so the ack can flush. Keep short so the next send can rediscover.</summary>
    public TimeSpan AckFlushDelay { get; set; } = TimeSpan.FromMilliseconds(80);
    public bool IsInitiator => _isInitiator;
    public bool IsFinished { get { lock (_gate) return _finished; } }
    public bool IsVerified { get { lock (_gate) return _verified; } }

    /// <summary>(peerId, sasCode, confirm, reject).</summary>
    public event Action<string, string, Action, Action>? SasRequested;
    public event Action<NexShareBleHandshakeSession>? Verified;
    public event Action<NexShareBleHandshakeSession, WireP2pCredentials>? CredentialsReceived;
    /// <summary>Receiver side: a link arrived over BLE (already acked).</summary>
    public event Action<NexShareBleHandshakeSession, WireBleUrl>? LinkReceived;
    /// <summary>Sender side: the peer acked our ble_url.</summary>
    public event Action<NexShareBleHandshakeSession>? LinkDelivered;
    /// <summary>(peerId or hint, reason).</summary>
    public event Action<string?, string>? Failed;
    /// <summary>Raised once when the session ends for any reason.</summary>
    public event Action<NexShareBleHandshakeSession>? Finished;

    public NexShareBleHandshakeSession(
        INexShareCrypto crypto, string myDeviceId, bool isInitiator,
        Action<byte[]> sendMessage, Action closeLink,
        IReadOnlyList<string>? localCaps = null, Func<string, bool>? isTrusted = null)
    {
        _crypto = crypto;
        _myDeviceId = myDeviceId;
        _isInitiator = isInitiator;
        _sendMessage = sendMessage;
        _closeLink = closeLink;
        _localCaps = localCaps ?? NexShareBleInfoCodec.LocalCaps;
        _isTrusted = isTrusted ?? (_ => false);
        (_key, _myPub) = crypto.GenerateKeyPair();
    }

    public void Start()
    {
        lock (_gate) { if (_isInitiator) SendHello(); }
    }

    public void OnFrame(byte[] frame)
    {
        byte[]? msg;
        lock (_gate) msg = _reassembler.Feed(frame);
        if (msg == null) return;
        Handle(Encoding.UTF8.GetString(msg));
    }

    private void SendEnvelope(string type, string content)
    {
        var env = new WireEnvelope
        {
            Type = type,
            MessageId = Guid.NewGuid().ToString(),
            SenderId = _myDeviceId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Payload = content
        };
        _sendMessage(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env)));
    }

    private void SendHello()
    {
        if (_helloSent) return;
        _helloSent = true;
        var hello = new WireEcdhHello
        {
            PubKey = Convert.ToBase64String(_myPub),
            Nonce = Convert.ToBase64String(_myNonce),
            Caps = _localCaps.ToList(),
            Intent = PendingLink != null ? NexShareHelloIntent.Link : null
        };
        SendEnvelope(NexShareMessageTypes.EcdhHello, JsonSerializer.Serialize(hello));
    }

    private void Handle(string json)
    {
        WireEnvelope? env;
        try { env = JsonSerializer.Deserialize<WireEnvelope>(json); }
        catch (JsonException) { return; }
        if (env == null) return;
        lock (_gate) { if (_finished) return; }
        switch (env.Type)
        {
            case NexShareMessageTypes.EcdhHello: OnPeerHello(env); break;
            case NexShareMessageTypes.SasConfirm:
                lock (_gate) _remoteConfirmed = true;
                CheckVerified();
                break;
            case NexShareMessageTypes.SasReject: Fail("SAS rejected by peer"); break;
            case NexShareMessageTypes.P2pCredentials: OnCredentials(env); break;
            case NexShareMessageTypes.BleUrl: OnBleUrl(env); break;
            case NexShareMessageTypes.BleUrlAck: OnBleUrlAck(); break;
        }
    }

    private void OnPeerHello(WireEnvelope env)
    {
        string pid, sas;
        lock (_gate)
        {
            if (SessionKey != null) return;
            WireEcdhHello? hello;
            try { hello = JsonSerializer.Deserialize<WireEcdhHello>(env.Payload ?? ""); }
            catch (JsonException) { hello = null; }
            if (hello == null || string.IsNullOrEmpty(hello.PubKey) || string.IsNullOrEmpty(hello.Nonce))
            { FailLocked("bad ecdh_hello"); return; }
            pid = env.SenderId ?? "";
            if (string.IsNullOrWhiteSpace(pid)) { FailLocked("ecdh_hello without senderId"); return; }
            PeerId = pid;
            PeerCaps = hello.Caps ?? new List<string>();
            PeerIntent = hello.Intent;
            try
            {
                var peerPub = Convert.FromBase64String(hello.PubKey);
                var peerNonce = Convert.FromBase64String(hello.Nonce);
                var secret = _crypto.DeriveSharedSecret(_key, peerPub);
                var sid = _crypto.WfdSessionId(_myPub, peerPub);
                SessionId = sid;
                SessionKey = _crypto.DeriveWfdSessionKey(secret, sid);
                BleLinkKey = _crypto.DeriveBleLinkKey(secret, sid);
                SendHello();
                sas = _crypto.DeriveWfdSas(_myPub, peerPub, _myNonce, peerNonce, secret);
                SasCode = sas;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                FailLocked("ECDH failed: " + ex.Message);
                return;
            }
        }
        if (_isTrusted(pid)) ConfirmLocal();
        else SasRequested?.Invoke(pid, sas, ConfirmLocal, RejectLocal);
    }

    public void ConfirmLocal()
    {
        lock (_gate)
        {
            if (_finished || _localConfirmed) return;
            _localConfirmed = true;
            SendEnvelope(NexShareMessageTypes.SasConfirm, "{}");
        }
        CheckVerified();
    }

    public void RejectLocal()
    {
        lock (_gate)
        {
            if (_finished) return;
            SendEnvelope(NexShareMessageTypes.SasReject, "{}");
        }
        Fail("SAS rejected locally");
    }

    private void CheckVerified()
    {
        lock (_gate)
        {
            if (_verified || !_localConfirmed || !_remoteConfirmed || PeerId == null) return;
            _verified = true;
        }
        Verified?.Invoke(this);
        WireBleUrl? link;
        lock (_gate) link = PendingLink != null && PeerCaps.Contains(NexShareCaps.BleLink) ? PendingLink : null;
        if (link != null) SendLink(link);
    }

    private void SendLink(WireBleUrl link)
    {
        lock (_gate)
        {
            if (_finished || _linkSent || !_verified || BleLinkKey == null || SessionId == null) return;
            _linkSent = true;
            var enc = _crypto.EncryptTextToJson(JsonSerializer.Serialize(link), BleLinkKey, SessionId);
            SendEnvelope(NexShareMessageTypes.BleUrl, enc);
        }
    }

    private void OnBleUrl(WireEnvelope env)
    {
        WireBleUrl? link = null;
        CheckVerified(); // our own confirm may still be completing (peer verified first and sent ble_url)
        lock (_gate)
        {
            if (!_verified || BleLinkKey == null || SessionId == null || PeerId == null) { FailLocked("ble_url before verification"); return; }
            var plain = _crypto.TryDecryptTextFromJson(env.Payload ?? "", BleLinkKey, SessionId);
            if (plain == null) { FailLocked("cannot decrypt ble_url"); return; }
            try { link = JsonSerializer.Deserialize<WireBleUrl>(plain); } catch (JsonException) { }
            if (link == null || string.IsNullOrWhiteSpace(link.Url)) { FailLocked("bad ble_url"); return; }
            SendEnvelope(NexShareMessageTypes.BleUrlAck, "{}");
        }
        try { LinkReceived?.Invoke(this, link); } catch { }
        var delay = AckFlushDelay;
        if (delay <= TimeSpan.Zero) Finish();
        else _ = System.Threading.Tasks.Task.Delay(delay).ContinueWith(_ => Finish());
    }

    private void OnBleUrlAck()
    {
        lock (_gate) { if (!_linkSent) return; }
        try { LinkDelivered?.Invoke(this); } catch { }
        Finish();
    }

    /// <summary>Group-owner side: send encrypted credentials. (The PC is never GO; kept for parity/tests.)</summary>
    public void SendCredentials(WireP2pCredentials creds)
    {
        lock (_gate)
        {
            if (!_verified || SessionKey == null || SessionId == null) { FailLocked("credentials before verification"); return; }
            var enc = _crypto.EncryptTextToJson(JsonSerializer.Serialize(creds), SessionKey, SessionId);
            SendEnvelope(NexShareMessageTypes.P2pCredentials, enc);
        }
    }

    private void OnCredentials(WireEnvelope env)
    {
        WireP2pCredentials? creds = null;
        lock (_gate)
        {
            if (!_verified || SessionKey == null || SessionId == null || PeerId == null) { FailLocked("credentials before verification"); return; }
            var plain = _crypto.TryDecryptTextFromJson(env.Payload ?? "", SessionKey, SessionId);
            if (plain == null) { FailLocked("cannot decrypt p2p_credentials"); return; }
            try { creds = JsonSerializer.Deserialize<WireP2pCredentials>(plain); } catch (JsonException) { }
            if (creds == null || string.IsNullOrEmpty(creds.Ssid)) { FailLocked("bad p2p_credentials"); return; }
        }
        CredentialsReceived?.Invoke(this, creds);
        Finish();
    }

    public void Fail(string reason)
    {
        lock (_gate) FailLocked(reason);
    }

    private void FailLocked(string reason)
    {
        if (_finished) return;
        _finished = true;
        var who = PeerId ?? PeerIdHint;
        // Raise outside of caller expectations but still synchronously; handlers must not block.
        try { Failed?.Invoke(who, reason); } catch { }
        try { _closeLink(); } catch { }
        try { Finished?.Invoke(this); } catch { }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
        }
        try { _closeLink(); } catch { }
        try { Finished?.Invoke(this); } catch { }
    }
}
