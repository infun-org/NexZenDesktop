using System;
using System.Runtime.InteropServices;

namespace NexZen.Desktop.Mirror;

/// <summary>
/// H.264 decoder on FFmpeg's libavcodec + libswscale (the system libraries), called directly: each
/// access unit is decoded and converted to BGRA as soon as it arrives. (Piping the stream through the
/// ffmpeg command line held pictures back until the next key frame because of its stream probing.)
///
/// Only the first, long-stable fields of AVPacket/AVFrame are touched (data/size, data[]/linesize[]/
/// width/height/format), and functions are looked up at run time, so any FFmpeg 5–9 build works.
/// </summary>
internal sealed unsafe class LibAvH264Decoder : IDisposable
{
    private const int AV_CODEC_ID_H264 = 27;
    private const int AV_PIX_FMT_BGRA = 28;
    private const int SWS_BILINEAR = 2;
    private const int AVERROR_EAGAIN = -11;
    private const int InputPadding = 64; // AV_INPUT_BUFFER_PADDING_SIZE

    // AVPacket: buf(8) pts(8) dts(8) data(8) size(4)
    private const int PacketDataOffset = 24;
    private const int PacketSizeOffset = 32;
    // AVFrame: data[8](64) linesize[8](32) extended_data(8) width(4) height(4) nb_samples(4) format(4)
    private const int FrameLinesizeOffset = 64;
    private const int FrameWidthOffset = 104;
    private const int FrameHeightOffset = 108;
    private const int FrameFormatOffset = 116;

    private static readonly object LoadLock = new();
    private static Api? s_api;
    private static string? s_loadError;

    private readonly Api _api;
    private IntPtr _ctx;
    private IntPtr _packet;
    private IntPtr _frame;
    private IntPtr _sws;
    private int _swsW, _swsH, _swsFmt;
    private byte[] _bgra = Array.Empty<byte>();

    public int Width { get; private set; }
    public int Height { get; private set; }

    private LibAvH264Decoder(Api api)
    {
        _api = api;
        var codec = api.avcodec_find_decoder(AV_CODEC_ID_H264);
        if (codec == IntPtr.Zero) throw new InvalidOperationException("libavcodec has no H.264 decoder");
        _ctx = api.avcodec_alloc_context3(codec);
        // Show every picture as soon as it is decoded; frame threading would hold several back.
        api.av_opt_set(_ctx, "flags", "low_delay", 0);
        api.av_opt_set_int(_ctx, "threads", 1, 0);
        if (api.avcodec_open2(_ctx, codec, IntPtr.Zero) < 0) throw new InvalidOperationException("avcodec_open2 failed");
        _packet = api.av_packet_alloc();
        _frame = api.av_frame_alloc();
    }

    /// <summary>Null (with <paramref name="error"/>) when FFmpeg's libraries are not installed.</summary>
    public static LibAvH264Decoder? TryCreate(out string? error)
    {
        lock (LoadLock)
        {
            if (s_api == null && s_loadError == null)
            {
                try { s_api = Api.Load(); }
                catch (Exception ex) { s_loadError = ex.Message; }
            }
        }
        error = s_loadError;
        if (s_api == null) return null;
        try { return new LibAvH264Decoder(s_api); }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Decodes one access unit (Annex-B). For each picture, <paramref name="onFrame"/> gets a BGRA
    /// buffer (width*height*4, reused) on the calling thread. Returns false on a decoding error.
    /// </summary>
    /// <summary>Where converted pictures go when written directly (no intermediate copy).</summary>
    public interface IFrameSink
    {
        /// <summary>Returns a BGRA buffer of at least height*stride bytes for a picture of this size.</summary>
        bool Begin(int width, int height, out IntPtr destination, out int stride);
        void End(int width, int height);
    }

    private IFrameSink? _sink;

    /// <summary>Decodes one access unit and converts each picture straight into <paramref name="sink"/>.</summary>
    public bool Decode(byte[] accessUnit, IFrameSink sink)
    {
        _sink = sink;
        try { return Decode(accessUnit, static (_, _, _) => { }); }
        finally { _sink = null; }
    }

    public bool Decode(byte[] accessUnit, Action<byte[], int, int> onFrame)
    {
        if (_ctx == IntPtr.Zero) return false;
        var buf = _api.av_malloc((nuint)(accessUnit.Length + InputPadding));
        if (buf == IntPtr.Zero) return false;
        Marshal.Copy(accessUnit, 0, buf, accessUnit.Length);
        new Span<byte>((byte*)buf + accessUnit.Length, InputPadding).Clear();
        if (_api.av_packet_from_data(_packet, buf, accessUnit.Length) < 0)
        {
            _api.av_free(buf);
            return false;
        }
        var sent = _api.avcodec_send_packet(_ctx, _packet);
        _api.av_packet_unref(_packet);
        if (sent < 0 && sent != AVERROR_EAGAIN) return false;

        while (true)
        {
            var r = _api.avcodec_receive_frame(_ctx, _frame);
            if (r == AVERROR_EAGAIN || r < 0) return r == AVERROR_EAGAIN || sent >= 0;
            Convert(onFrame);
            _api.av_frame_unref(_frame);
        }
    }

    private void Convert(Action<byte[], int, int> onFrame)
    {
        var f = (byte*)_frame;
        var w = *(int*)(f + FrameWidthOffset);
        var h = *(int*)(f + FrameHeightOffset);
        var fmt = *(int*)(f + FrameFormatOffset);
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return;
        if (_sws == IntPtr.Zero || w != _swsW || h != _swsH || fmt != _swsFmt)
        {
            _sws = _api.sws_getCachedContext(_sws, w, h, fmt, w, h, AV_PIX_FMT_BGRA, SWS_BILINEAR,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            _swsW = w; _swsH = h; _swsFmt = fmt;
            if (_sws == IntPtr.Zero) return;
        }
        Width = w;
        Height = h;
        if (_sink != null)
        {
            if (!_sink.Begin(w, h, out var target, out var targetStride)) return;
            var tData = stackalloc IntPtr[4];
            var tStride = stackalloc int[4];
            tData[0] = target;
            tStride[0] = targetStride;
            _api.sws_scale(_sws, (IntPtr*)f, (int*)(f + FrameLinesizeOffset), 0, h, tData, tStride);
            _sink.End(w, h);
            return;
        }
        var size = w * h * 4;
        if (_bgra.Length != size) _bgra = new byte[size];
        fixed (byte* dst = _bgra)
        {
            var dstData = stackalloc IntPtr[4];
            var dstStride = stackalloc int[4];
            dstData[0] = (IntPtr)dst;
            dstStride[0] = w * 4;
            _api.sws_scale(_sws, (IntPtr*)f, (int*)(f + FrameLinesizeOffset), 0, h, dstData, dstStride);
        }
        onFrame(_bgra, w, h);
    }

    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        var ctx = _ctx;
        _api.avcodec_free_context(ref ctx);
        _ctx = IntPtr.Zero;
        var p = _packet;
        _api.av_packet_free(ref p);
        var fr = _frame;
        _api.av_frame_free(ref fr);
        if (_sws != IntPtr.Zero) _api.sws_freeContext(_sws);
        _sws = IntPtr.Zero;
    }

    /// <summary>Loads FFmpeg's libraries once (shared with <see cref="LibAvAudioDecoder"/>).</summary>
    internal static Api? LoadApi(out string? error)
    {
        lock (LoadLock)
        {
            if (s_api == null && s_loadError == null)
            {
                try { s_api = Api.Load(); }
                catch (Exception ex) { s_loadError = ex.Message; }
            }
        }
        error = s_loadError;
        return s_api;
    }

    internal sealed class Api
    {
        public delegate* unmanaged<int, IntPtr> avcodec_find_decoder;
        public delegate* unmanaged<IntPtr, IntPtr> avcodec_alloc_context3;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> avcodec_open2;
        public delegate* unmanaged<IntPtr*, void> avcodec_free_context_raw;
        public delegate* unmanaged<IntPtr, IntPtr, int> avcodec_send_packet;
        public delegate* unmanaged<IntPtr, IntPtr, int> avcodec_receive_frame;
        public delegate* unmanaged<IntPtr> av_packet_alloc;
        public delegate* unmanaged<IntPtr*, void> av_packet_free_raw;
        public delegate* unmanaged<IntPtr, IntPtr, int, int> av_packet_from_data;
        public delegate* unmanaged<IntPtr, void> av_packet_unref;
        public delegate* unmanaged<IntPtr> av_frame_alloc;
        public delegate* unmanaged<IntPtr*, void> av_frame_free_raw;
        public delegate* unmanaged<IntPtr, void> av_frame_unref;
        public delegate* unmanaged<nuint, IntPtr> av_malloc;
        public delegate* unmanaged<IntPtr, void> av_free;
        public delegate* unmanaged<IntPtr, byte*, byte*, int, int> av_opt_set_raw;
        public delegate* unmanaged<IntPtr, byte*, long, int, int> av_opt_set_int_raw;
        public delegate* unmanaged<IntPtr, int, int, int, int, int, int, int, IntPtr, IntPtr, IntPtr, IntPtr> sws_getCachedContext_raw;
        public delegate* unmanaged<IntPtr, IntPtr*, int*, int, int, IntPtr*, int*, int> sws_scale_raw;
        public delegate* unmanaged<IntPtr, void> sws_freeContext;

        public void avcodec_free_context(ref IntPtr ctx) { fixed (IntPtr* p = &ctx) avcodec_free_context_raw(p); }
        public void av_packet_free(ref IntPtr pkt) { fixed (IntPtr* p = &pkt) av_packet_free_raw(p); }
        public void av_frame_free(ref IntPtr frame) { fixed (IntPtr* p = &frame) av_frame_free_raw(p); }

        public int av_opt_set(IntPtr obj, string name, string value, int flags)
        {
            var n = Marshal.StringToCoTaskMemUTF8(name);
            var v = Marshal.StringToCoTaskMemUTF8(value);
            try { return av_opt_set_raw(obj, (byte*)n, (byte*)v, flags); }
            finally { Marshal.FreeCoTaskMem(n); Marshal.FreeCoTaskMem(v); }
        }

        public int av_opt_set_int(IntPtr obj, string name, long value, int flags)
        {
            var n = Marshal.StringToCoTaskMemUTF8(name);
            try { return av_opt_set_int_raw(obj, (byte*)n, value, flags); }
            finally { Marshal.FreeCoTaskMem(n); }
        }

        public IntPtr sws_getCachedContext(IntPtr ctx, int sw, int sh, int sfmt, int dw, int dh, int dfmt, int flags,
            IntPtr srcFilter, IntPtr dstFilter, IntPtr param) =>
            sws_getCachedContext_raw(ctx, sw, sh, sfmt, dw, dh, dfmt, flags, srcFilter, dstFilter, param);

        public int sws_scale(IntPtr ctx, IntPtr* src, int* srcStride, int y, int h, IntPtr* dst, int* dstStride) =>
            sws_scale_raw(ctx, src, srcStride, y, h, dst, dstStride);

        public static Api Load()
        {
            // The three libraries must come from the same FFmpeg release: try the default names first,
            // then known major versions together.
            var sets = OperatingSystem.IsWindows()
                ? new[] { ("avcodec-62.dll", "avutil-60.dll", "swscale-9.dll"), ("avcodec-61.dll", "avutil-59.dll", "swscale-8.dll"),
                          ("avcodec-60.dll", "avutil-58.dll", "swscale-7.dll"), ("avcodec-63.dll", "avutil-61.dll", "swscale-10.dll") }
                : OperatingSystem.IsMacOS()
                    ? new[] { ("libavcodec.dylib", "libavutil.dylib", "libswscale.dylib") }
                    : new[] { ("libavcodec.so", "libavutil.so", "libswscale.so"),
                              ("libavcodec.so.63", "libavutil.so.61", "libswscale.so.10"),
                              ("libavcodec.so.62", "libavutil.so.60", "libswscale.so.9"),
                              ("libavcodec.so.61", "libavutil.so.59", "libswscale.so.8"),
                              ("libavcodec.so.60", "libavutil.so.58", "libswscale.so.7"),
                              ("libavcodec.so.59", "libavutil.so.57", "libswscale.so.6") };
            foreach (var (codecName, utilName, swsName) in sets)
            {
                if (!NativeLibrary.TryLoad(codecName, out var codec)) continue;
                if (!NativeLibrary.TryLoad(utilName, out var util) || !NativeLibrary.TryLoad(swsName, out var sws)) continue;
                IntPtr F(IntPtr lib, string name) => NativeLibrary.GetExport(lib, name);
                return new Api
                {
                    avcodec_find_decoder = (delegate* unmanaged<int, IntPtr>)F(codec, "avcodec_find_decoder"),
                    avcodec_alloc_context3 = (delegate* unmanaged<IntPtr, IntPtr>)F(codec, "avcodec_alloc_context3"),
                    avcodec_open2 = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)F(codec, "avcodec_open2"),
                    avcodec_free_context_raw = (delegate* unmanaged<IntPtr*, void>)F(codec, "avcodec_free_context"),
                    avcodec_send_packet = (delegate* unmanaged<IntPtr, IntPtr, int>)F(codec, "avcodec_send_packet"),
                    avcodec_receive_frame = (delegate* unmanaged<IntPtr, IntPtr, int>)F(codec, "avcodec_receive_frame"),
                    av_packet_alloc = (delegate* unmanaged<IntPtr>)F(codec, "av_packet_alloc"),
                    av_packet_free_raw = (delegate* unmanaged<IntPtr*, void>)F(codec, "av_packet_free"),
                    av_packet_from_data = (delegate* unmanaged<IntPtr, IntPtr, int, int>)F(codec, "av_packet_from_data"),
                    av_packet_unref = (delegate* unmanaged<IntPtr, void>)F(codec, "av_packet_unref"),
                    av_frame_alloc = (delegate* unmanaged<IntPtr>)F(util, "av_frame_alloc"),
                    av_frame_free_raw = (delegate* unmanaged<IntPtr*, void>)F(util, "av_frame_free"),
                    av_frame_unref = (delegate* unmanaged<IntPtr, void>)F(util, "av_frame_unref"),
                    av_malloc = (delegate* unmanaged<nuint, IntPtr>)F(util, "av_malloc"),
                    av_free = (delegate* unmanaged<IntPtr, void>)F(util, "av_free"),
                    av_opt_set_raw = (delegate* unmanaged<IntPtr, byte*, byte*, int, int>)F(util, "av_opt_set"),
                    av_opt_set_int_raw = (delegate* unmanaged<IntPtr, byte*, long, int, int>)F(util, "av_opt_set_int"),
                    sws_getCachedContext_raw = (delegate* unmanaged<IntPtr, int, int, int, int, int, int, int, IntPtr, IntPtr, IntPtr, IntPtr>)F(sws, "sws_getCachedContext"),
                    sws_scale_raw = (delegate* unmanaged<IntPtr, IntPtr*, int*, int, int, IntPtr*, int*, int>)F(sws, "sws_scale"),
                    sws_freeContext = (delegate* unmanaged<IntPtr, void>)F(sws, "sws_freeContext"),
                };
            }
            throw new DllNotFoundException("FFmpeg libraries (libavcodec, libavutil, libswscale) were not found");
        }
    }
}
