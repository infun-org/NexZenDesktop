using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace NexZen.Desktop.Mirror;

/// <summary>
/// Plays float32 interleaved samples through PulseAudio (PipeWire's pulse server on most Linux
/// desktops) with libpulse-simple. For NexZen Mirror it keeps the delay low: the server buffer
/// targets ~60 ms, and when more than ~200 ms is waiting, the oldest audio is dropped so it never
/// falls behind the picture. Read aloud keeps every sample instead (no queue limit) and can pause.
/// </summary>
internal sealed unsafe class PulseAudioPlayer : IDisposable
{
    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_SAMPLE_FLOAT32LE = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr { public uint MaxLength, TLength, PreBuf, MinReq, FragSize; }

    private static readonly IntPtr Lib = NativeLibrary.TryLoad("libpulse-simple.so.0", out var h) ? h : IntPtr.Zero;
    private static readonly delegate* unmanaged<byte*, byte*, int, byte*, byte*, SampleSpec*, IntPtr, BufferAttr*, int*, IntPtr> PaNew =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<byte*, byte*, int, byte*, byte*, SampleSpec*, IntPtr, BufferAttr*, int*, IntPtr>)NativeLibrary.GetExport(Lib, "pa_simple_new");
    private static readonly delegate* unmanaged<IntPtr, void*, nuint, int*, int> PaWrite =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, void*, nuint, int*, int>)NativeLibrary.GetExport(Lib, "pa_simple_write");
    private static readonly delegate* unmanaged<IntPtr, int*, int> PaFlush =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, int*, int>)NativeLibrary.GetExport(Lib, "pa_simple_flush");
    private static readonly delegate* unmanaged<IntPtr, void> PaFree =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(Lib, "pa_simple_free");

    private readonly IntPtr _pa;
    private readonly BlockingCollection<float[]> _queue = new(new ConcurrentQueue<float[]>());
    private readonly Thread _thread;
    private readonly int _rate, _channels;
    private readonly int? _maxQueuedMs;
    private readonly ManualResetEventSlim _playing = new(true);
    private long _queuedSamples;
    private volatile bool _muted;
    private int _disposed;

    public bool Muted { get => _muted; set => _muted = value; }

    /// <summary>Holds the queued audio (nothing is dropped meanwhile) until set back to false.</summary>
    public bool Paused
    {
        get => !_playing.IsSet;
        set
        {
            if (value) _playing.Reset();
            else _playing.Set();
        }
    }

    /// <summary>Samples queued and not yet handed to PulseAudio.</summary>
    public long QueuedSamples => Interlocked.Read(ref _queuedSamples);

    public int Rate => _rate;

    private PulseAudioPlayer(IntPtr pa, int rate, int channels, int? maxQueuedMs)
    {
        _pa = pa;
        _rate = rate;
        _channels = channels;
        _maxQueuedMs = maxQueuedMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "NexZenAudioOut" };
        _thread.Start();
    }

    /// <param name="streamName">Name shown in the system mixer.</param>
    /// <param name="maxQueuedMs">Oldest audio beyond this is dropped (live audio); null keeps everything.</param>
    public static PulseAudioPlayer? TryCreate(int rate, int channels, out string? error,
        string streamName = "Phone audio (NexZen Mirror)", int? maxQueuedMs = 200)
    {
        error = null;
        if (PaNew == null || PaWrite == null)
        {
            error = "libpulse-simple not found";
            return null;
        }
        var spec = new SampleSpec { Format = PA_SAMPLE_FLOAT32LE, Rate = (uint)rate, Channels = (byte)channels };
        var bytesPerMs = (uint)(rate * channels * 4 / 1000);
        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = bytesPerMs * 60,
            PreBuf = bytesPerMs * 20,
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };
        var name = Marshal.StringToCoTaskMemUTF8("NexZen");
        var stream = Marshal.StringToCoTaskMemUTF8(streamName);
        try
        {
            int err;
            var pa = PaNew(null, (byte*)name, PA_STREAM_PLAYBACK, null, (byte*)stream, &spec, IntPtr.Zero, &attr, &err);
            if (pa == IntPtr.Zero)
            {
                error = "pa_simple_new failed (" + err + ")";
                return null;
            }
            return new PulseAudioPlayer(pa, rate, channels, maxQueuedMs);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(stream);
        }
    }

    /// <summary>Queues samples (copied). With a queue limit, drops the oldest audio beyond it.</summary>
    public void Enqueue(float[] samples, int count)
    {
        if (Volatile.Read(ref _disposed) != 0 || count <= 0) return;
        var copy = new float[count];
        Array.Copy(samples, copy, count);
        _queue.Add(copy);
        var queued = Interlocked.Add(ref _queuedSamples, count);
        if (_maxQueuedMs is not { } ms) return;
        var limit = (long)_rate * _channels * ms / 1000;
        while (queued > limit && _queue.TryTake(out var old))
            queued = Interlocked.Add(ref _queuedSamples, -old.Length);
    }

    /// <summary>Drops everything still queued (the ~60 ms PulseAudio holds plays out).</summary>
    public void Clear()
    {
        while (_queue.TryTake(out var old))
            Interlocked.Add(ref _queuedSamples, -old.Length);
    }

    private void Run()
    {
        try
        {
            foreach (var block in _queue.GetConsumingEnumerable())
            {
                _playing.Wait();
                if (Volatile.Read(ref _disposed) != 0) return;
                Interlocked.Add(ref _queuedSamples, -block.Length);
                if (_muted) Array.Clear(block);
                int err;
                fixed (float* p = block)
                    if (PaWrite(_pa, p, (nuint)(block.Length * 4), &err) < 0) return;
            }
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        _playing.Set();
        _thread.Join(500);
        int err;
        if (PaFlush != null) PaFlush(_pa, &err);
        PaFree(_pa);
    }
}
