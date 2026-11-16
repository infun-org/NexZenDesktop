using System;
using System.Runtime.InteropServices;

namespace NexZen.Desktop.Mirror;

/// <summary>
/// Decodes the phone's audio (Opus packets or ADTS AAC) with libavcodec into interleaved float32
/// samples. Uses the same run-time-loaded FFmpeg functions as <see cref="LibAvH264Decoder"/>.
/// </summary>
internal sealed unsafe class LibAvAudioDecoder : IDisposable
{
    private const int AV_CODEC_ID_AAC = 86018;
    private const int AV_CODEC_ID_OPUS = 86076;
    private const int AV_SAMPLE_FMT_S16 = 1, AV_SAMPLE_FMT_FLT = 3, AV_SAMPLE_FMT_S16P = 6, AV_SAMPLE_FMT_FLTP = 8;
    private const int AVERROR_EAGAIN = -11;
    private const int InputPadding = 64;
    private const int FrameLinesizeOffset = 64;
    private const int FrameNbSamplesOffset = 112;
    private const int FrameFormatOffset = 116;

    private readonly LibAvH264Decoder.Api _api;
    private IntPtr _ctx, _packet, _frame;
    private float[] _out = Array.Empty<float>();
    public int Channels { get; }
    public int Rate { get; }

    private LibAvAudioDecoder(LibAvH264Decoder.Api api, string codec, int rate, int channels)
    {
        _api = api;
        Channels = channels;
        Rate = rate;
        var c = api.avcodec_find_decoder(codec == "aac" ? AV_CODEC_ID_AAC : AV_CODEC_ID_OPUS);
        if (c == IntPtr.Zero) throw new InvalidOperationException("no " + codec + " decoder in libavcodec");
        _ctx = api.avcodec_alloc_context3(c);
        api.av_opt_set(_ctx, "ch_layout", channels == 1 ? "mono" : "stereo", 0);
        api.av_opt_set_int(_ctx, "sample_rate", rate, 0);
        if (api.avcodec_open2(_ctx, c, IntPtr.Zero) < 0) throw new InvalidOperationException("avcodec_open2 failed");
        _packet = api.av_packet_alloc();
        _frame = api.av_frame_alloc();
    }

    public static LibAvAudioDecoder? TryCreate(MirrorAudio config, out string? error)
    {
        var api = LibAvH264Decoder.LoadApi(out error);
        if (api == null) return null;
        try { return new LibAvAudioDecoder(api, config.Codec, config.Rate, config.Channels); }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Decodes one packet; <paramref name="onSamples"/> gets interleaved float32 (buffer reused).</summary>
    public void Decode(byte[] packet, Action<float[], int> onSamples)
    {
        if (_ctx == IntPtr.Zero || packet.Length == 0) return;
        var buf = _api.av_malloc((nuint)(packet.Length + InputPadding));
        if (buf == IntPtr.Zero) return;
        Marshal.Copy(packet, 0, buf, packet.Length);
        new Span<byte>((byte*)buf + packet.Length, InputPadding).Clear();
        if (_api.av_packet_from_data(_packet, buf, packet.Length) < 0)
        {
            _api.av_free(buf);
            return;
        }
        _api.avcodec_send_packet(_ctx, _packet);
        _api.av_packet_unref(_packet);
        while (_api.avcodec_receive_frame(_ctx, _frame) >= 0)
        {
            Interleave(onSamples);
            _api.av_frame_unref(_frame);
        }
    }

    private void Interleave(Action<float[], int> onSamples)
    {
        var f = (byte*)_frame;
        var n = *(int*)(f + FrameNbSamplesOffset);
        var fmt = *(int*)(f + FrameFormatOffset);
        if (n <= 0 || n > 48000) return;
        var ch = Channels;
        var total = n * ch;
        if (_out.Length < total) _out = new float[total];
        var data = (IntPtr*)f;
        switch (fmt)
        {
            case AV_SAMPLE_FMT_FLTP:
                for (var c = 0; c < ch; c++)
                {
                    var plane = (float*)data[c];
                    for (var i = 0; i < n; i++) _out[i * ch + c] = plane[i];
                }
                break;
            case AV_SAMPLE_FMT_FLT:
                new ReadOnlySpan<float>((float*)data[0], total).CopyTo(_out);
                break;
            case AV_SAMPLE_FMT_S16P:
                for (var c = 0; c < ch; c++)
                {
                    var plane = (short*)data[c];
                    for (var i = 0; i < n; i++) _out[i * ch + c] = plane[i] / 32768f;
                }
                break;
            case AV_SAMPLE_FMT_S16:
                var s = (short*)data[0];
                for (var i = 0; i < total; i++) _out[i] = s[i] / 32768f;
                break;
            default:
                return;
        }
        onSamples(_out, total);
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        var ctx = _ctx; _api.avcodec_free_context(ref ctx); _ctx = IntPtr.Zero;
        var p = _packet; _api.av_packet_free(ref p);
        var fr = _frame; _api.av_frame_free(ref fr);
    }
}

/// <summary>Audio stream format as the window needs it.</summary>
internal sealed record MirrorAudio(string Codec, int Rate, int Channels);
