using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Crypto;
using NexZen.Core.NexShare.Models;
using NexZen.Core.NexShare.Transport;
using NexZen.Infrastructure.NexShare.Crypto;
using Xunit;

namespace NexZen.Tests;

internal sealed class DuplexStream : Stream
{
    private readonly Stream _r, _w;
    public DuplexStream(Stream r, Stream w) { _r = r; _w = w; }
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() => _w.Flush();
    public override Task FlushAsync(CancellationToken ct) => _w.FlushAsync(ct);
    public override int Read(byte[] b, int o, int c) => _r.Read(b, o, c);
    public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => _r.ReadAsync(m, ct);
    public override void Write(byte[] b, int o, int c) => _w.Write(b, o, c);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) => _w.WriteAsync(m, ct);
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) { _r.Dispose(); _w.Dispose(); } base.Dispose(d); }

    public static (Stream A, Stream B) Pair()
    {
        var ab = new Pipe(); var ba = new Pipe();
        return (new DuplexStream(ba.Reader.AsStream(), ab.Writer.AsStream()),
                new DuplexStream(ab.Reader.AsStream(), ba.Writer.AsStream()));
    }
}

/// <summary>C# simulation of Android WifiDirectTransport (GO side) on a stream.</summary>
internal sealed class AndroidWfdSim
{
    private readonly NexShareCrypto _c = new();
    private readonly Stream _s;
    private readonly ECDiffieHellman _key; private readonly byte[] _pub; private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(16);
    public readonly string Id = "android-" + Guid.NewGuid().ToString("N")[..8];
    public bool PreVerified = true; public bool SupportsE2e = true;
    public EnvelopeCipher? Cipher; public string? Sas; public string? PeerId;
    public readonly BlockingCollection<WireEnvelope> Received = new();
    public AndroidWfdSim(Stream s) { _s = s; (_key, _pub) = _c.GenerateKeyPair(); }

    public async Task SendAsync(WireEnvelope e)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e));
        var f = new byte[4 + body.Length]; BinaryPrimitives.WriteInt32BigEndian(f, body.Length); body.CopyTo(f, 4);
        await _s.WriteAsync(f); await _s.FlushAsync();
    }
    public Task SendTextAsync(string text, bool encrypt)
    {
        var e = new WireEnvelope { Type = NexShareMessageTypes.Text, SenderId = Id, Payload = text };
        return SendAsync(encrypt ? Cipher!.Encrypt(e) : e);
    }

    public async Task RunAsync()
    {
        var caps = SupportsE2e ? new() { NexShareCaps.WfdLegacy, NexShareCaps.WfdE2e } : new System.Collections.Generic.List<string> { NexShareCaps.WfdLegacy };
        await SendAsync(new WireEnvelope { Type = NexShareMessageTypes.EcdhHello, SenderId = Id,
            Payload = JsonSerializer.Serialize(new WireEcdhHello { PubKey = Convert.ToBase64String(_pub), Nonce = Convert.ToBase64String(_nonce), Caps = caps }) });
        var hdr = new byte[4];
        try
        {
            while (true)
            {
                await _s.ReadExactlyAsync(hdr);
                var buf = new byte[BinaryPrimitives.ReadInt32BigEndian(hdr)];
                await _s.ReadExactlyAsync(buf);
                var env = JsonSerializer.Deserialize<WireEnvelope>(buf)!;
                if (env.Type == NexShareMessageTypes.EcdhHello)
                {
                    var h = JsonSerializer.Deserialize<WireEcdhHello>(env.Payload)!;
                    var pp = Convert.FromBase64String(h.PubKey); var pn = Convert.FromBase64String(h.Nonce);
                    var sec = _c.DeriveSharedSecret(_key, pp);
                    Sas = _c.DeriveWfdSas(_pub, pp, _nonce, pn, sec);
                    var sid = _c.WfdSessionId(_pub, pp);
                    Cipher = new EnvelopeCipher(_c, _c.DeriveWfdSessionKey(sec, sid), sid);
                    PeerId = env.SenderId;
                    if (PreVerified)
                    {
                        await SendAsync(new WireEnvelope { Type = NexShareMessageTypes.SasConfirm, SenderId = Id, Payload = "{}" });
                        await SendAsync(new WireEnvelope { Type = NexShareMessageTypes.Hello, SenderId = Id,
                            Payload = "{\"deviceId\":\"" + Id + "\",\"deviceName\":\"OPPO\",\"deviceType\":\"PHONE\"}" });
                    }
                    continue;
                }
                Received.Add(EnvelopeCipher.IsEncrypted(env) && Cipher != null ? (Cipher.Decrypt(env) ?? env) : env);
            }
        }
        catch { Received.CompleteAdding(); }
    }
}

public class TcpFramedTransportTests
{
    private static (TcpFramedTransport pc, AndroidWfdSim phone, BlockingCollection<WireEnvelope> pcRx, ConcurrentBag<string> trusted) Setup(
        bool preVerified = true, bool phoneE2e = true)
    {
        var (a, b) = DuplexStream.Pair();
        var trusted = new ConcurrentBag<string>();
        var phone = new AndroidWfdSim(b) { SupportsE2e = phoneE2e };
        var pre = preVerified ? phone.Id : null;
        var pc = new TcpFramedTransport(new NexShareCrypto(), "pc-device")
        {
            ConsumePreVerified = id => id == pre && Interlocked.Exchange(ref pre, null) != null,
            IsTrusted = id => trusted.Contains(id),
            MarkTrusted = id => trusted.Add(id),
        };
        var rx = new BlockingCollection<WireEnvelope>();
        pc.OnMessageReceived += (_, e) => rx.Add(e);
        pc.Attach(a, "192.168.49.1");
        _ = phone.RunAsync();
        _ = pc.StartReceiveLoopAsync();
        return (pc, phone, rx, trusted);
    }

    private static WireEnvelope Take(BlockingCollection<WireEnvelope> c, string type)
    {
        while (c.TryTake(out var e, TimeSpan.FromSeconds(5))) if (e.Type == type) return e;
        throw new TimeoutException("no " + type);
    }

    [Fact]
    public async Task PreVerified_handshake_skips_sas_and_encrypts_both_ways()
    {
        var (pc, phone, rx, trusted) = Setup();
        var sasAsked = false; pc.OnSasRequested += (_, _) => sasAsked = true;
        Take(rx, NexShareMessageTypes.Hello);                      // phone HELLO reaches orchestrator
        var sc = Take(phone.Received, NexShareMessageTypes.SasConfirm);
        Assert.Equal("pc-device", sc.SenderId);
        Assert.False(sasAsked);
        Assert.True(pc.UseE2e);
        Assert.Contains(phone.Id, trusted);
        Assert.NotNull(phone.Cipher);

        await phone.SendTextAsync("hi from phone", encrypt: true);
        Assert.Equal("hi from phone", Take(rx, NexShareMessageTypes.Text).Payload);

        await pc.SendMessageAsync(new WireEnvelope { Type = NexShareMessageTypes.Text, SenderId = "pc-device", Payload = "hi from pc" });
        Assert.Equal("hi from pc", Take(phone.Received, NexShareMessageTypes.Text).Payload);
        pc.Dispose();
    }

    [Fact]
    public async Task Plaintext_from_e2e_peer_is_dropped()
    {
        var (pc, phone, rx, _) = Setup();
        Take(rx, NexShareMessageTypes.Hello);
        await phone.SendTextAsync("plain", encrypt: false);
        await phone.SendTextAsync("secret", encrypt: true);
        Assert.Equal("secret", Take(rx, NexShareMessageTypes.Text).Payload);
        pc.Dispose();
    }

    [Fact]
    public async Task Not_preverified_raises_sas_with_same_code_as_phone()
    {
        var (a, b) = DuplexStream.Pair();
        var phone = new AndroidWfdSim(b) { PreVerified = false };
        var pc = new TcpFramedTransport(new NexShareCrypto(), "pc-device");
        var got = new TaskCompletionSource<SasPrompt>();
        pc.OnSasRequested += (_, p) => got.TrySetResult(p);
        pc.Attach(a, "x");
        _ = phone.RunAsync();
        _ = pc.StartReceiveLoopAsync();
        Assert.True(await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task);
        var prompt = await got.Task;
        // The phone side derives its code on its own task; on a busy machine the PC can
        // raise the prompt first, so wait for it (up to 5 s) before comparing.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref phone.Sas) == null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(Volatile.Read(ref phone.Sas), prompt.Code);
        prompt.Confirm();
        Take(phone.Received, NexShareMessageTypes.SasConfirm);
        pc.Dispose();
    }

    [Fact]
    public void Legacy_plaintext_peer_only_accepted_when_trusted()
    {
        var (pc, phone, rx, trusted) = Setup(preVerified: false, phoneE2e: false);
        // no auto-confirm: pc is not trusted -> SAS; phone not pre-verified -> no HELLO either.
        trusted.Add(phone.Id);
        Thread.Sleep(300);
        Assert.False(pc.UseE2e);
        Assert.NotNull(pc.DecryptIncoming(new WireEnvelope { Type = NexShareMessageTypes.Text, Payload = "x" }));
        pc.Dispose();
    }

    [Fact]
    public async Task Oversized_frame_closes_link()
    {
        var (a, b) = DuplexStream.Pair();
        var pc = new TcpFramedTransport(new NexShareCrypto(), "pc");
        var closed = new TaskCompletionSource(); pc.OnDisconnected += (_, _) => closed.TrySetResult();
        pc.Attach(a, "x"); _ = pc.StartReceiveLoopAsync();
        var hdr = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(hdr, TcpFramedTransport.MaxFrameBytes + 1);
        await b.WriteAsync(hdr); await b.FlushAsync();
        Assert.True(await Task.WhenAny(closed.Task, Task.Delay(5000)) == closed.Task);
    }

    [Fact]
    public async Task Connect_retries_until_go_listens()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var pc = new TcpFramedTransport(new NexShareCrypto(), "pc") { ConnectWindow = TimeSpan.FromSeconds(10), AttemptTimeout = TimeSpan.FromSeconds(1) };
        var connect = pc.ConnectAsync("127.0.0.1", port);
        await Task.Delay(1500);
        var l = new TcpListener(IPAddress.Loopback, port); l.Start();
        using var s = await l.AcceptTcpClientAsync();
        await connect;
        Assert.True(pc.IsConnected);
        pc.Dispose(); l.Stop();
    }

    [Fact]
    public void LocalStore_persists_id_and_trust()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nxstore-" + Guid.NewGuid().ToString("N"));
        var s1 = new NexZen.Core.NexShare.NexShareLocalStore(dir);
        var id = s1.DeviceId; s1.AddTrusted("peerA");
        var s2 = new NexZen.Core.NexShare.NexShareLocalStore(dir);
        Assert.Equal(id, s2.DeviceId);
        Assert.True(s2.IsTrusted("peerA"));
        Assert.False(s2.IsTrusted("peerB"));
        Directory.Delete(dir, true);
    }
}
