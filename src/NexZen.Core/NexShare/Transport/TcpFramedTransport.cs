using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Crypto;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Transport;

/// <summary>
/// Client side of Android's WifiDirectTransport (TCP 25556 on the GO):
/// frames are a 4-byte big-endian length + UTF-8 JSON WireEnvelope (max 100 MB).
/// On every link both sides send ecdh_hello first; the WFD session key is derived exactly like Android
/// (deriveWfdSessionKey with sid = hex SHA256(sorted pubs)) and text / file_chunk payloads are
/// encrypted with it when the peer advertises wfd_e2e.
/// Verification (mirrors Android): peer pre-verified over BLE (consumed once) or already trusted
/// => send sas_confirm without UI; otherwise raise <see cref="OnSasRequested"/>.
/// Receive policy for text / file_chunk (mirrors Android decryptIncoming):
///   encrypted with our key -> decrypted; encrypted with another key -> passed through;
///   plaintext from a wfd_e2e peer -> dropped; plaintext from a legacy peer -> only if trusted.
/// </summary>
public sealed class TcpFramedTransport : INexShareTransport
{
    public const int DefaultPort = 25556;
    public const int MaxFrameBytes = 100 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly INexShareCrypto _crypto;
    private readonly string _myDeviceId;
    private readonly IReadOnlyList<string> _caps;
    private readonly ECDiffieHellman _myKey;
    private readonly byte[] _myPub;
    private readonly byte[] _myNonce = RandomNumberGenerator.GetBytes(16);
    private readonly Channel<(byte[] Frame, TaskCompletionSource Done)> _out =
        Channel.CreateUnbounded<(byte[], TaskCompletionSource)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcp;
    private Stream? _stream;
    private Task? _readerTask;
    private int _closed;
    private int _readerStarted;

    private volatile EnvelopeCipher? _cipher;

    public TcpFramedTransport(INexShareCrypto crypto, string myDeviceId, IReadOnlyList<string>? caps = null)
    {
        _crypto = crypto;
        _myDeviceId = myDeviceId;
        _caps = caps ?? new[] { NexShareCaps.WfdLegacy, NexShareCaps.WfdE2e };
        (_myKey, _myPub) = crypto.GenerateKeyPair();
    }

    public event EventHandler<WireEnvelope>? OnMessageReceived;
    public event EventHandler? OnConnected;
    public event EventHandler? OnDisconnected;
    /// <summary>SAS needed (peer neither pre-verified nor trusted). Confirm sends sas_confirm, Reject sends sas_reject + closes.</summary>
    public event EventHandler<SasPrompt>? OnSasRequested;
    /// <summary>Raised after each ecdh_hello is processed (peerId, sessionId).</summary>
    public event EventHandler<string>? OnSessionEstablished;
    public event EventHandler<string>? OnLog;

    /// <summary>Consume-once BLE pre-verification check (Android WfdPreVerified.consume).</summary>
    public Func<string, bool>? ConsumePreVerified { get; set; }
    public Func<string, bool>? IsTrusted { get; set; }
    public Action<string>? MarkTrusted { get; set; }

    public TimeSpan ConnectWindow { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private long _lastActivityMs = Environment.TickCount64;
    /// <summary>Environment.TickCount64 of the last non-keepalive message sent or received (WFD idle timeout).</summary>
    public long LastActivityMs => Interlocked.Read(ref _lastActivityMs);
    private void Touch(string? type)
    {
        var t = type ?? "";
        if (t.Contains("ping", StringComparison.OrdinalIgnoreCase) || t.Contains("pong", StringComparison.OrdinalIgnoreCase)
            || t.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || t.Contains("keepalive", StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Exchange(ref _lastActivityMs, Environment.TickCount64);
    }

    public bool IsConnected => _stream != null && Volatile.Read(ref _closed) == 0;
    public string RemoteIpAddress { get; private set; } = string.Empty;
    public string? PeerId { get; private set; }
    public string? SessionId { get; private set; }
    public bool PeerSupportsE2e { get; private set; }
    public bool UseE2e => PeerSupportsE2e && _cipher != null;

    /// <summary>Connects with retries (the GO may not be listening yet) and starts the writer. Call StartReceiveLoopAsync next.</summary>
    public async Task ConnectAsync(string ip, int port)
    {
        if (port <= 0) port = DefaultPort;
        var deadline = DateTime.UtcNow + ConnectWindow;
        var attempt = 0;
        while (true)
        {
            attempt++;
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                Log($"connecting to {ip}:{port} (attempt {attempt})");
                using var to = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                to.CancelAfter(AttemptTimeout);
                await tcp.ConnectAsync(ip, port, to.Token).ConfigureAwait(false);
                _tcp = tcp;
                Attach(tcp.GetStream(), ip);
                return;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                tcp.Dispose();
                if (_cts.IsCancellationRequested) throw new IOException("Transport closed while connecting");
                if (DateTime.UtcNow + TimeSpan.FromSeconds(1) > deadline)
                    throw new IOException($"GO {ip}:{port} not reachable after {attempt} attempts: {ex.Message}", ex);
                Log($"GO not reachable yet ({ex.Message}), retrying");
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Use an already-connected stream (tests / in-memory pipes).</summary>
    public void Attach(Stream stream, string remote)
    {
        _stream = stream;
        RemoteIpAddress = remote;
        _ = Task.Factory.StartNew(WriterLoopAsync, TaskCreationOptions.LongRunning).Unwrap();
    }

    /// <summary>Starts the background reader and sends our ecdh_hello. Returns the reader task.</summary>
    public Task StartReceiveLoopAsync()
    {
        if (_stream == null) throw new InvalidOperationException("Not connected");
        if (Interlocked.Exchange(ref _readerStarted, 1) == 1) return _readerTask ?? Task.CompletedTask;
        _readerTask = Task.Factory.StartNew(ReaderLoopAsync, TaskCreationOptions.LongRunning).Unwrap();
        _ = SendRawAsync(new WireEnvelope
        {
            Type = NexShareMessageTypes.EcdhHello,
            SenderId = _myDeviceId,
            Payload = JsonSerializer.Serialize(new WireEcdhHello
            {
                PubKey = Convert.ToBase64String(_myPub),
                Nonce = Convert.ToBase64String(_myNonce),
                Caps = new List<string>(_caps)
            })
        });
        OnConnected?.Invoke(this, EventArgs.Empty);
        return _readerTask;
    }

    public Task SendMessageAsync(WireEnvelope envelope)
    {
        Touch(envelope.Type);
        var c = _cipher;
        if (c != null && PeerSupportsE2e &&
            (envelope.Type == NexShareMessageTypes.Text || envelope.Type == NexShareMessageTypes.FileChunk))
            envelope = c.Encrypt(envelope);
        return SendRawAsync(envelope);
    }

    public void ConfirmSas() => _ = SendRawAsync(Simple(NexShareMessageTypes.SasConfirm));

    public void RejectSas()
    {
        _ = SendRawAsync(Simple(NexShareMessageTypes.SasReject)).ContinueWith(_ => Close("SAS rejected"));
    }

    private WireEnvelope Simple(string type) => new() { Type = type, SenderId = _myDeviceId, Payload = "{}" };

    private Task SendRawAsync(WireEnvelope env)
    {
        if (Volatile.Read(ref _closed) != 0) return Task.FromException(new IOException("Transport closed"));
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(env));
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_out.Writer.TryWrite((frame, tcs))) tcs.TrySetException(new IOException("Transport closed"));
        return tcs.Task;
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var (frame, done) in _out.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await _stream!.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                    Close("write failed: " + ex.Message);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        while (_out.Reader.TryRead(out var left)) left.Done.TrySetException(new IOException("Transport closed"));
    }

    private async Task ReaderLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _stream!.ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);
                var len = BinaryPrimitives.ReadInt32BigEndian(header);
                if (len < 0 || len > MaxFrameBytes) throw new InvalidDataException($"Invalid payload size: {len}");
                var buf = new byte[len];
                await _stream.ReadExactlyAsync(buf, _cts.Token).ConfigureAwait(false);
                WireEnvelope? raw;
                try { raw = JsonSerializer.Deserialize<WireEnvelope>(Encoding.UTF8.GetString(buf), ReadOpts); }
                catch { continue; }
                if (raw != null) Touch(raw.Type);
                if (raw == null || string.IsNullOrEmpty(raw.Type)) continue;
                Handle(raw);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Close(ex is OperationCanceledException ? "closed" : ex.Message);
        }
    }

    private void Handle(WireEnvelope raw)
    {
        if (raw.Type == NexShareMessageTypes.EcdhHello)
        {
            HandleEcdhHello(raw);
            return;
        }
        var env = DecryptIncoming(raw);
        if (env == null) return;
        if (env.Type == NexShareMessageTypes.SasConfirm)
        {
            if (PeerId != null) MarkTrusted?.Invoke(PeerId);
            return;
        }
        if (env.Type == NexShareMessageTypes.SasReject)
        {
            Close("SAS verification rejected by peer");
            return;
        }
        OnMessageReceived?.Invoke(this, env);
    }

    private void HandleEcdhHello(WireEnvelope raw)
    {
        WireEcdhHello? h;
        try { h = JsonSerializer.Deserialize<WireEcdhHello>(raw.Payload, ReadOpts); } catch { return; }
        if (h == null) return;
        byte[] peerPub, peerNonce, secret;
        try
        {
            peerPub = Convert.FromBase64String(h.PubKey);
            peerNonce = Convert.FromBase64String(h.Nonce);
            secret = _crypto.DeriveSharedSecret(_myKey, peerPub);
        }
        catch (Exception ex) { Log("bad ecdh_hello: " + ex.Message); return; }

        var sas = _crypto.DeriveWfdSas(_myPub, peerPub, _myNonce, peerNonce, secret);
        var sid = _crypto.WfdSessionId(_myPub, peerPub);
        _cipher = new EnvelopeCipher(_crypto, _crypto.DeriveWfdSessionKey(secret, sid), sid);
        SessionId = sid;
        PeerSupportsE2e = h.Caps?.Contains(NexShareCaps.WfdE2e) == true;
        var senderId = raw.SenderId ?? string.Empty;
        PeerId = senderId;
        Log($"WFD session established sid={sid[..8]} peerE2e={PeerSupportsE2e}");
        OnSessionEstablished?.Invoke(this, sid);

        if (ConsumePreVerified?.Invoke(senderId) == true)
        {
            Log($"{senderId} was SAS-verified over BLE, skipping SAS UI");
            MarkTrusted?.Invoke(senderId);
            ConfirmSas();
        }
        else if (IsTrusted?.Invoke(senderId) == true)
        {
            Log($"{senderId} is already trusted, skipping SAS UI");
            ConfirmSas();
        }
        else
        {
            Log("Requesting SAS verification");
            var prompt = new SasPrompt(senderId, null, sas, ConfirmSas, RejectSas);
            if (OnSasRequested != null) OnSasRequested.Invoke(this, prompt);
            else prompt.Reject();
        }
    }

    public WireEnvelope? DecryptIncoming(WireEnvelope env)
    {
        if (env.Type != NexShareMessageTypes.Text && env.Type != NexShareMessageTypes.FileChunk) return env;
        if (EnvelopeCipher.IsEncrypted(env))
        {
            var c = _cipher;
            var dec = c?.Decrypt(env);
            return dec ?? env; // other key (e.g. QR E2E) -> pass through unchanged
        }
        if (PeerSupportsE2e)
        {
            Log($"dropping plaintext {env.Type} from wfd_e2e peer (downgrade protection)");
            return null;
        }
        if (PeerId != null && IsTrusted?.Invoke(PeerId) == true) return env;
        Log($"dropping plaintext {env.Type} from untrusted legacy peer");
        return null;
    }

    public Task DisconnectAsync()
    {
        Close("local disconnect");
        return Task.CompletedTask;
    }

    private void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Log("closing: " + reason);
        _out.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        OnDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void Log(string m) => OnLog?.Invoke(this, m);

    public void Dispose()
    {
        Close("disposed");
        _myKey.Dispose();
    }
}
