//
// This file manually written from cef/include/internal/cef_types_osr.h
// and cef/include/internal/cef_types_linux.h.
// Linux dmabuf planes. Windows and macOS use a different native layout.
//
namespace Xilium.CefGlue.Interop
{
    using System;
    using System.Runtime.InteropServices;

    [StructLayout(LayoutKind.Sequential, Pack = libcef.ALIGN)]
    internal unsafe struct cef_accelerated_paint_native_pixmap_plane_t
    {
        public uint stride;
        public ulong offset;
        public ulong size;
        public int fd;
    }

    [StructLayout(LayoutKind.Sequential, Pack = libcef.ALIGN)]
    internal unsafe struct cef_accelerated_paint_info_common_t
    {
        public UIntPtr size;
        public ulong timestamp;
        public cef_size_t coded_size;
        public cef_rect_t visible_rect;
        public cef_rect_t content_rect;
        public cef_size_t source_size;
        public cef_rect_t capture_update_rect;
        public cef_rect_t region_capture_rect;
        public ulong capture_counter;
        public byte has_capture_update_rect;
        public byte has_region_capture_rect;
        public byte has_source_size;
        public byte has_capture_counter;
    }

    [StructLayout(LayoutKind.Sequential, Pack = libcef.ALIGN)]
    internal unsafe struct cef_accelerated_paint_info_t
    {
        public UIntPtr size;
        public cef_accelerated_paint_native_pixmap_plane_t plane0;
        public cef_accelerated_paint_native_pixmap_plane_t plane1;
        public cef_accelerated_paint_native_pixmap_plane_t plane2;
        public cef_accelerated_paint_native_pixmap_plane_t plane3;
        public int plane_count;
        public ulong modifier;
        public CefColorType format;
        public cef_accelerated_paint_info_common_t extra;
    }
}
