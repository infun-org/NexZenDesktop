namespace Xilium.CefGlue
{
    using System;
    using Xilium.CefGlue.Interop;

    /// <summary>One plane of a Linux dmabuf frame from OnAcceleratedPaint.</summary>
    public readonly struct CefAcceleratedPaintPlane
    {
        public CefAcceleratedPaintPlane(int fd, uint stride, ulong offset, ulong size)
        {
            Fd = fd;
            Stride = stride;
            Offset = offset;
            Size = size;
        }

        /// <summary>dmabuf file descriptor; only valid during the OnAcceleratedPaint callback (dup it to keep it).</summary>
        public int Fd { get; }
        public uint Stride { get; }
        public ulong Offset { get; }
        public ulong Size { get; }
    }

    /// <summary>
    /// Managed copy of cef_accelerated_paint_info_t on Linux (dmabuf planes, format, size).
    /// Read it inside OnAcceleratedPaint: the native struct and its fds are only valid there.
    /// </summary>
    public sealed class CefAcceleratedPaintInfo
    {
        public CefAcceleratedPaintPlane[] Planes { get; private set; } = Array.Empty<CefAcceleratedPaintPlane>();
        public ulong Modifier { get; private set; }
        public CefColorType Format { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        public static unsafe CefAcceleratedPaintInfo FromNative(IntPtr info)
        {
            var result = new CefAcceleratedPaintInfo();
            if (info == IntPtr.Zero || CefRuntime.Platform != CefRuntimePlatform.Linux)
                return result;
            var n = (cef_accelerated_paint_info_t*)info;
            var count = Math.Clamp(n->plane_count, 0, 4);
            var planes = new CefAcceleratedPaintPlane[count];
            for (var i = 0; i < count; i++)
            {
                var p = i switch { 0 => n->plane0, 1 => n->plane1, 2 => n->plane2, _ => n->plane3 };
                planes[i] = new CefAcceleratedPaintPlane(p.fd, p.stride, p.offset, p.size);
            }
            result.Planes = planes;
            result.Modifier = n->modifier;
            result.Format = n->format;
            result.Width = n->extra.coded_size.width;
            result.Height = n->extra.coded_size.height;
            return result;
        }
    }
}
