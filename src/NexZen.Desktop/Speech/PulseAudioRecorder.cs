using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace NexZen.Desktop.Speech;

/// <summary>Records mono float32 audio from the default input (microphone) with libpulse-simple.</summary>
internal static unsafe class PulseAudioRecorder
{
    private const int PA_STREAM_RECORD = 2;
    private const int PA_SAMPLE_FLOAT32LE = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    private static readonly IntPtr Lib = NativeLibrary.TryLoad("libpulse-simple.so.0", out var h) ? h : IntPtr.Zero;
    private static readonly delegate* unmanaged<byte*, byte*, int, byte*, byte*, SampleSpec*, IntPtr, IntPtr, int*, IntPtr> PaNew =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<byte*, byte*, int, byte*, byte*, SampleSpec*, IntPtr, IntPtr, int*, IntPtr>)NativeLibrary.GetExport(Lib, "pa_simple_new");
    private static readonly delegate* unmanaged<IntPtr, void*, nuint, int*, int> PaRead =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, void*, nuint, int*, int>)NativeLibrary.GetExport(Lib, "pa_simple_read");
    private static readonly delegate* unmanaged<IntPtr, void> PaFree =
        Lib == IntPtr.Zero ? null : (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(Lib, "pa_simple_free");

    /// <summary>
    /// Records up to <paramref name="seconds"/> (less if cancelled: what was recorded is kept).
    /// <paramref name="onLevel"/> gets the peak of each 100 ms block, for a level meter.
    /// </summary>
    public static float[] Record(int rate, double seconds, Action<float>? onLevel, CancellationToken cancellationToken)
    {
        if (PaNew == null || PaRead == null)
            throw new InvalidOperationException("libpulse-simple not found");
        var spec = new SampleSpec { Format = PA_SAMPLE_FLOAT32LE, Rate = (uint)rate, Channels = 1 };
        var name = Marshal.StringToCoTaskMemUTF8("NexZen");
        var stream = Marshal.StringToCoTaskMemUTF8("Voice recording (NexZen)");
        IntPtr pa;
        int err;
        try
        {
            pa = PaNew(null, (byte*)name, PA_STREAM_RECORD, null, (byte*)stream, &spec, IntPtr.Zero, IntPtr.Zero, &err);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(stream);
        }
        if (pa == IntPtr.Zero)
            throw new InvalidOperationException("Cannot open the microphone (PulseAudio error " + err + ")");
        try
        {
            var block = new float[rate / 10];
            var total = (int)(rate * seconds);
            var samples = new List<float>(total);
            while (samples.Count < total && !cancellationToken.IsCancellationRequested)
            {
                fixed (float* p = block)
                    if (PaRead(pa, p, (nuint)(block.Length * 4), &err) < 0)
                        throw new InvalidOperationException("Recording failed (PulseAudio error " + err + ")");
                var peak = 0f;
                foreach (var v in block)
                    peak = Math.Max(peak, Math.Abs(v));
                onLevel?.Invoke(peak);
                samples.AddRange(block);
            }
            return samples.ToArray();
        }
        finally
        {
            PaFree(pa);
        }
    }
}

/// <summary>16-bit PCM WAV for saved speech and voice recordings.</summary>
internal static class WavFile
{
    public static byte[] Pcm16(float[] samples, int count, int rate)
    {
        var data = count * 2;
        var bytes = new byte[44 + data];
        void Ascii(int at, string s) { for (var i = 0; i < 4; i++) bytes[at + i] = (byte)s[i]; }
        void U32(int at, int v) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), v);
        void U16(int at, short v) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 2), v);
        Ascii(0, "RIFF"); U32(4, 36 + data); Ascii(8, "WAVE");
        Ascii(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1); U32(24, rate); U32(28, rate * 2); U16(32, 2); U16(34, 16);
        Ascii(36, "data"); U32(40, data);
        for (var i = 0; i < count; i++)
        {
            var v = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(44 + i * 2, 2), v);
        }
        return bytes;
    }
}
