using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Mirror;
using NexZen.Core.NexShare.Models;

namespace NexZen.Application.Services;

public enum PhoneMirrorState
{
    /// <summary>mirror_request sent, waiting for the phone's mirror_hello.</summary>
    Requesting,
    /// <summary>Both sides show the SAS; the phone owner decides.</summary>
    AwaitingApproval,
    Streaming,
    Ended,
}

/// <summary>PC side of one screen-mirroring session (see <see cref="MirrorMessageTypes"/>).</summary>
public sealed class PhoneMirrorClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly INexShareOrchestrator _orchestrator;
    private readonly INexShareCrypto _crypto;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private ECDiffieHellman? _myKey;
    private byte[] _myPub = Array.Empty<byte>();
    private readonly byte[] _myNonce = RandomNumberGenerator.GetBytes(16);
    private string? _peerId;
    private MirrorSessionCrypto? _session;
    private int _ended;
    private Timer? _timeout;
    private Timer? _ackTimer;
    private long _lastAckMs;
    private long _lastReceivedSeq;
    private long _lastAckedSeq;

    private void SendAck()
    {
        var seq = Interlocked.Read(ref _lastReceivedSeq);
        if (seq <= Interlocked.Exchange(ref _lastAckedSeq, seq) || State == PhoneMirrorState.Ended) return;
        _lastAckMs = Environment.TickCount64;
        _ = SendAsync(MirrorMessageTypes.Ack, new WireMirrorAck { SessionId = _sessionId, Seq = seq });
    }

    public PhoneMirrorState State { get; private set; } = PhoneMirrorState.Requesting;
    public string? Sas => _session?.Sas;

    /// <summary>State changes with a short user-facing message.</summary>
    public event Action<PhoneMirrorState, string>? StateChanged;
    public event Action<MirrorStreamConfig>? ConfigReceived;
    /// <summary>One H.264 access unit (Annex-B), raised on the transport's thread.</summary>
    public event Action<byte[], bool>? VideoReceived;
    /// <summary>Phone screen state: (screen on, locked).</summary>
    public event Action<bool, bool>? PhoneStatusChanged;

    public PhoneMirrorClient(INexShareOrchestrator orchestrator, INexShareCrypto crypto)
    {
        _orchestrator = orchestrator;
        _crypto = crypto;
    }

    public async Task<bool> StartAsync(string pcName)
    {
        _peerId = _orchestrator.ConnectedDeviceId;
        if (string.IsNullOrEmpty(_peerId))
        {
            End("No phone is connected over NexShare.");
            return false;
        }
        (_myKey, _myPub) = _crypto.GenerateKeyPair();
        _orchestrator.OnMirrorMessage += OnMirrorMessage;
        _orchestrator.OnPeerConnectionChanged += OnPeerChanged;

        var request = new WireMirrorRequest
        {
            SessionId = _sessionId,
            PubKey = Convert.ToBase64String(_myPub),
            Nonce = Convert.ToBase64String(_myNonce),
            DeviceName = pcName,
        };
        Set(PhoneMirrorState.Requesting, "Asking the phone...");
        if (!await SendAsync(MirrorMessageTypes.Request, request))
        {
            End("Could not reach the phone.");
            return false;
        }
        // The phone answers mirror_hello right away; then the owner has time to decide.
        _timeout = new Timer(_ =>
        {
            if (State == PhoneMirrorState.Requesting)
                End("The phone did not answer. Update NexZen on the phone and try again.");
            else if (State == PhoneMirrorState.AwaitingApproval)
                End("No answer on the phone in time.");
        }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
        return true;
    }

    /// <summary>True once the phone said the owner allowed control (accessibility service on).</summary>
    public bool CanControl { get; private set; }

    /// <summary>Sends one input event, encrypted for this session. Ignored unless control is allowed.</summary>
    public Task<bool> SendInputAsync(MirrorInputEvent input)
    {
        if (!CanControl || _session == null || State != PhoneMirrorState.Streaming) return Task.FromResult(false);
        var wire = _session.SealInput(_sessionId, JsonSerializer.SerializeToUtf8Bytes(input));
        return SendAsync(MirrorMessageTypes.Input, wire);
    }

    public Task RequestKeyFrameAsync() =>
        State == PhoneMirrorState.Streaming
            ? SendAsync(MirrorMessageTypes.KeyFrame, new WireMirrorControl { SessionId = _sessionId })
            : Task.FromResult(false);

    public async Task StopAsync(string reason = "pc_closed")
    {
        if (State == PhoneMirrorState.Ended) return;
        await SendAsync(MirrorMessageTypes.Stop, new WireMirrorControl { SessionId = _sessionId, Reason = reason });
        End("Mirroring stopped.");
    }

    private void OnPeerChanged(object? sender, PeerLinkState state)
    {
        if (!state.IsConnected || (state.DeviceId != null && state.DeviceId != _peerId))
            End("Lost the connection to the phone.");
    }

    private void OnMirrorMessage(object? sender, WireEnvelope env)
    {
        if (State == PhoneMirrorState.Ended) return;
        if (!string.IsNullOrEmpty(env.SenderId) && env.SenderId != _peerId) return;
        try
        {
            switch (env.Type)
            {
                case MirrorMessageTypes.Hello:
                    OnHello(JsonSerializer.Deserialize<WireMirrorHello>(env.Payload, Json));
                    break;
                case MirrorMessageTypes.Frame:
                    OnFrame(JsonSerializer.Deserialize<WireMirrorFrame>(env.Payload, Json));
                    break;
                case MirrorMessageTypes.Status:
                {
                    var st = JsonSerializer.Deserialize<WireMirrorStatus>(env.Payload, Json);
                    if (st?.SessionId == _sessionId)
                        PhoneStatusChanged?.Invoke(st.Screen != "off", st.Locked);
                    break;
                }
                case MirrorMessageTypes.Reject:
                {
                    var c = JsonSerializer.Deserialize<WireMirrorControl>(env.Payload, Json);
                    if (c?.SessionId == _sessionId)
                        End(c.Reason == "busy" ? "The phone is already mirroring to another device." : "Declined on the phone.");
                    break;
                }
                case MirrorMessageTypes.Stop:
                {
                    var c = JsonSerializer.Deserialize<WireMirrorControl>(env.Payload, Json);
                    if (c?.SessionId == _sessionId)
                        End("Mirroring was stopped on the phone.");
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[Mirror] bad {env.Type}: {ex.Message}");
        }
    }

    private void OnHello(WireMirrorHello? hello)
    {
        if (hello == null || hello.SessionId != _sessionId || _session != null || _myKey == null) return;
        try
        {
            _session = MirrorSessionCrypto.Derive(_crypto, _myKey, _myPub, _myNonce,
                Convert.FromBase64String(hello.PubKey), Convert.FromBase64String(hello.Nonce), _sessionId);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            End("The phone sent an invalid key.");
            return;
        }
        _timeout?.Change(TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        Set(PhoneMirrorState.AwaitingApproval,
            $"Code: {_session.Sas}. Check that the phone shows the same code, then tap Allow on the phone.");
    }

    private void OnFrame(WireMirrorFrame? wire)
    {
        if (wire == null || wire.SessionId != _sessionId || _session == null) return;
        var frame = _session.Open(wire);
        if (frame == null)
        {
            Console.WriteLine("[Mirror] dropped a frame that failed authentication or arrived out of order");
            return;
        }
        var f = frame.Value;
        // Tell the phone how far we got (it backs off when frames pile up somewhere on the way):
        // at most every 100 ms, plus a trailing ack so the last frame before a pause is confirmed too.
        Interlocked.Exchange(ref _lastReceivedSeq, f.Seq);
        var now = Environment.TickCount64;
        if (now - _lastAckMs >= 100) SendAck();
        else _ackTimer ??= new Timer(_ => SendAck(), null, Timeout.Infinite, Timeout.Infinite);
        _ackTimer?.Change(120, Timeout.Infinite);
        if (State == PhoneMirrorState.AwaitingApproval)
        {
            _timeout?.Change(Timeout.Infinite, Timeout.Infinite);
            Set(PhoneMirrorState.Streaming, "Mirroring");
        }
        if ((f.Flags & MirrorFrameFlags.Config) != 0)
        {
            var config = JsonSerializer.Deserialize<MirrorStreamConfig>(Encoding.UTF8.GetString(f.Payload), Json);
            if (config != null && config.Width > 0 && config.Height > 0 && config.Width <= 8192 && config.Height <= 8192)
            {
                CanControl = config.Control;
                ConfigReceived?.Invoke(config);
            }
            return;
        }
        VideoReceived?.Invoke(f.Payload, (f.Flags & MirrorFrameFlags.KeyFrame) != 0);
    }

    private Task<bool> SendAsync<T>(string type, T payload) =>
        _orchestrator.SendEnvelopeAsync(new WireEnvelope { Type = type, Payload = JsonSerializer.Serialize(payload) });

    private void Set(PhoneMirrorState state, string message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    private void End(string message)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        _timeout?.Dispose();
        _ackTimer?.Dispose();
        _orchestrator.OnMirrorMessage -= OnMirrorMessage;
        _orchestrator.OnPeerConnectionChanged -= OnPeerChanged;
        _myKey?.Dispose();
        Set(PhoneMirrorState.Ended, message);
    }

    public void Dispose() => End("Closed.");
}
