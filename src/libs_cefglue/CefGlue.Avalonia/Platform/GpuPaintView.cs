using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Xilium.CefGlue.Avalonia.Platform
{
    /// <summary>
    /// Shows the browser's frames straight from the GPU (GpuPaint): CEF hands each frame
    /// over as a Linux dmabuf, which is imported with EGL into a texture of Avalonia's own Skia GPU
    /// context and drawn as part of the normal render pass — no copy to system memory and no
    /// separate GL surface. (An OpenGlControlBase was tried first; Avalonia composited it at ~6 fps
    /// for a full-screen window.) Requires Avalonia to render through EGL.
    /// </summary>
    public static class GpuPaintPlatform
    {
        /// <summary>
        /// Call once Avalonia's platform is set up and before browsers are created: GPU painting
        /// imports frames with EGL, so it is switched off when Avalonia renders another way.
        /// </summary>
        public static void Verify()
        {
            if (!Xilium.CefGlue.Common.GpuPaint.Enabled)
                return;
            try
            {
                var asm = typeof(Visual).Assembly;
                var locatorType = asm.GetType("Avalonia.AvaloniaLocator");
                var graphicsType = asm.GetType("Avalonia.Platform.IPlatformGraphics");
                var current = locatorType?.GetProperty("Current",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                var getService = current?.GetType().GetMethod("GetService", new[] { typeof(Type) });
                if (graphicsType == null || getService == null)
                    return; // cannot tell here; the view checks again when it draws
                var graphics = getService.Invoke(current, new object[] { graphicsType });
                if (graphics == null)
                    Xilium.CefGlue.Common.GpuPaint.Disable("Avalonia renders in software");
                else if (!graphics.GetType().Name.Contains("Egl", StringComparison.OrdinalIgnoreCase))
                    Xilium.CefGlue.Common.GpuPaint.Disable($"Avalonia renders with {graphics.GetType().Name}, not EGL");
                else
                    Console.WriteLine($"[GpuPaint] on ({graphics.GetType().Name})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GpuPaint] platform check skipped: {ex.Message}");
            }
        }
    }

    internal sealed class GpuPaintView : Control
    {
        private readonly object _gate = new();
        private Frame _pending;      // newest frame not drawn yet (fds are our own dups)
        private bool _invalidateQueued;
        private readonly DrawOperation _operation;

        internal sealed class Frame
        {
            public int Fd;
            public int Width, Height;
            public uint Stride;
            public ulong Offset, Modifier;
            public uint Fourcc;
        }

        public GpuPaintView()
        {
            _operation = new DrawOperation(this);
        }

        /// <summary>Takes the frame of OnAcceleratedPaint (CEF UI thread); the fd is duplicated.</summary>
        public void Submit(CefAcceleratedPaintInfo info)
        {
            if (_operation.Disabled || info.Planes.Length != 1 || info.Width <= 0 || info.Height <= 0)
                return; // single-plane BGRA/RGBA is what CEF sends for web content
            var fd = Native.dup(info.Planes[0].Fd);
            if (fd < 0)
                return;
            var frame = new Frame
            {
                Fd = fd,
                Width = info.Width,
                Height = info.Height,
                Stride = info.Planes[0].Stride,
                Offset = info.Planes[0].Offset,
                Modifier = info.Modifier,
                Fourcc = info.Format == CefColorType.Rgba8888 ? 0x34324241u /* AB24 */ : 0x34325241u /* AR24 */
            };
            bool queue;
            lock (_gate)
            {
                _operation.TraceSubmitted();
                if (_pending != null)
                    Native.close(_pending.Fd); // superseded before it was drawn
                _pending = frame;
                queue = !_invalidateQueued;
                _invalidateQueued = true;
            }
            if (queue)
                Dispatcher.UIThread.Post(() =>
                {
                    lock (_gate)
                        _invalidateQueued = false;
                    InvalidateVisual();
                }, DispatcherPriority.Render);
        }

        /// <summary>The newest frame, if any arrived since the last draw (render thread).</summary>
        internal Frame TakePending()
        {
            lock (_gate)
            {
                var frame = _pending;
                _pending = null;
                return frame;
            }
        }

        public override void Render(DrawingContext context)
        {
            _operation.Bounds = new Rect(Bounds.Size);
            context.Custom(_operation);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            var frame = TakePending();
            if (frame != null)
                Native.close(frame.Fd);
        }

        /// <summary>Runs on Avalonia's render thread inside the compositor's Skia GPU context.</summary>
        private sealed unsafe class DrawOperation : ICustomDrawOperation
        {
            private readonly GpuPaintView _owner;
            public bool Disabled;
            private bool _initialized;
            private int _texture;
            private int _width, _height;
            private bool _hasFrame;

            private delegate* unmanaged<int, int*, void> _glGenTextures;
            private delegate* unmanaged<int, int, void> _glBindTexture;
            private delegate* unmanaged<int, int, int, void> _glTexParameteri;
            private delegate* unmanaged<int, IntPtr, void> _glEGLImageTargetTexture2DOES;
            private delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr> _eglCreateImageKHR;
            private delegate* unmanaged<IntPtr, IntPtr, int> _eglDestroyImageKHR;

            private const int GL_TEXTURE_2D = 0x0DE1, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800,
                GL_LINEAR = 0x2601, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F,
                GL_RGBA8 = 0x8058;

            // NEXZEN_RENDER_TRACE=1: once a second, frames delivered by CEF vs drawn, import time.
            private static readonly bool s_trace = Environment.GetEnvironmentVariable("NEXZEN_RENDER_TRACE") == "1";
            private readonly System.Diagnostics.Stopwatch _traceClock = System.Diagnostics.Stopwatch.StartNew();
            private int _traceSubmitted, _traceDrawn, _traceImported;
            private double _traceImportMs, _traceRenderMs;

            public DrawOperation(GpuPaintView owner) => _owner = owner;

            public Rect Bounds { get; set; }

            public void TraceSubmitted()
            {
                if (s_trace)
                    System.Threading.Interlocked.Increment(ref _traceSubmitted);
            }

            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation other) => false; // always redraw
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (Disabled)
                    return;
                var start = s_trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (leaseFeature == null)
                {
                    Disable("no Skia lease (software rendering?)");
                    return;
                }
                using var lease = leaseFeature.Lease();
                var grContext = lease.GrContext;
                if (grContext == null)
                {
                    Disable("Skia is not GPU accelerated");
                    return;
                }

                var frame = _owner.TakePending();
                if (frame != null)
                {
                    var importStart = s_trace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                    try
                    {
                        using var platform = lease.TryLeasePlatformGraphicsApi();
                        if (platform?.Context is not IGlContext gl)
                        {
                            Disable("the renderer does not use OpenGL");
                            return;
                        }
                        if (!_initialized)
                            Initialize(gl);
                        if (!Disabled)
                            Import(frame);
                    }
                    finally
                    {
                        Native.close(frame.Fd); // EGL holds its own reference to the buffer
                        grContext.ResetContext(); // we changed GL state behind Skia's back
                    }
                    if (s_trace)
                    {
                        _traceImported++;
                        _traceImportMs += System.Diagnostics.Stopwatch.GetElapsedTime(importStart).TotalMilliseconds;
                    }
                }

                var canvas = lease.SkCanvas;
                if (!_hasFrame)
                {
                    using var white = new SKPaint { Color = SKColors.White };
                    canvas.DrawRect(SKRect.Create((float)Bounds.Width, (float)Bounds.Height), white);
                }
                else
                {
                    var backend = new GRBackendTexture(_width, _height, false, new GRGlTextureInfo(GL_TEXTURE_2D, (uint)_texture, GL_RGBA8));
                    using var image = SKImage.FromTexture(grContext, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888, SKAlphaType.Premul);
                    if (image != null)
                    {
                        using var paint = new SKPaint();
                        canvas.DrawImage(image, SKRect.Create(_width, _height),
                            SKRect.Create((float)Bounds.Width, (float)Bounds.Height),
                            new SKSamplingOptions(SKFilterMode.Linear), paint);
                    }
                }

                if (s_trace)
                {
                    _traceDrawn++;
                    _traceRenderMs += System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (_traceClock.ElapsedMilliseconds >= 1000)
                    {
                        var submitted = System.Threading.Interlocked.Exchange(ref _traceSubmitted, 0);
                        Console.WriteLine($"[GpuTrace] {_width}x{_height} cefFrames={submitted}/s drawn={_traceDrawn}/s " +
                            $"newFrames={_traceImported}/s import={(_traceImported > 0 ? _traceImportMs / _traceImported : 0):0.00}ms " +
                            $"render={(_traceDrawn > 0 ? _traceRenderMs / _traceDrawn : 0):0.00}ms");
                        _traceDrawn = _traceImported = 0;
                        _traceImportMs = _traceRenderMs = 0;
                        _traceClock.Restart();
                    }
                }
            }

            private void Disable(string reason)
            {
                if (Disabled)
                    return;
                Disabled = true;
                Console.WriteLine($"[GpuPaint] GPU view disabled: {reason}");
                Xilium.CefGlue.Common.GpuPaint.Disable(reason);
            }

            private void Initialize(IGlContext gl)
            {
                _initialized = true;
                try
                {
                    IntPtr P(string name)
                    {
                        var p = gl.GlInterface.GetProcAddress(name);
                        if (p == IntPtr.Zero)
                            throw new InvalidOperationException(name + " missing");
                        return p;
                    }
                    _glGenTextures = (delegate* unmanaged<int, int*, void>)P("glGenTextures");
                    _glBindTexture = (delegate* unmanaged<int, int, void>)P("glBindTexture");
                    _glTexParameteri = (delegate* unmanaged<int, int, int, void>)P("glTexParameteri");
                    _glEGLImageTargetTexture2DOES = (delegate* unmanaged<int, IntPtr, void>)P("glEGLImageTargetTexture2DOES");
                    var create = Native.eglGetProcAddress("eglCreateImageKHR");
                    var destroy = Native.eglGetProcAddress("eglDestroyImageKHR");
                    if (create == IntPtr.Zero || destroy == IntPtr.Zero || Native.eglGetCurrentDisplay() == IntPtr.Zero)
                        throw new InvalidOperationException("EGL image import is not available (is Avalonia rendering with EGL?)");
                    _eglCreateImageKHR = (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, int*, IntPtr>)create;
                    _eglDestroyImageKHR = (delegate* unmanaged<IntPtr, IntPtr, int>)destroy;

                    int texture;
                    _glGenTextures(1, &texture);
                    _texture = texture;
                    _glBindTexture(GL_TEXTURE_2D, _texture);
                    _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                    _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                    _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                    _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
                    Console.WriteLine("[GpuPaint] drawing frames in the compositor's Skia GL context");
                }
                catch (Exception ex)
                {
                    Disable(ex.Message);
                }
            }

            private void Import(Frame frame)
            {
                var display = Native.eglGetCurrentDisplay();
                const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3056, EGL_LINUX_DRM_FOURCC_EXT = 0x3271,
                    EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272, EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273,
                    EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274, EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443,
                    EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444, EGL_NONE = 0x3038, EGL_LINUX_DMA_BUF_EXT = 0x3270;
                const ulong DRM_FORMAT_MOD_INVALID = 0x00FFFFFFFFFFFFFF;
                var attribs = stackalloc int[17];
                var i = 0;
                attribs[i++] = EGL_WIDTH; attribs[i++] = frame.Width;
                attribs[i++] = EGL_HEIGHT; attribs[i++] = frame.Height;
                attribs[i++] = EGL_LINUX_DRM_FOURCC_EXT; attribs[i++] = (int)frame.Fourcc;
                attribs[i++] = EGL_DMA_BUF_PLANE0_FD_EXT; attribs[i++] = frame.Fd;
                attribs[i++] = EGL_DMA_BUF_PLANE0_OFFSET_EXT; attribs[i++] = (int)frame.Offset;
                attribs[i++] = EGL_DMA_BUF_PLANE0_PITCH_EXT; attribs[i++] = (int)frame.Stride;
                if (frame.Modifier != DRM_FORMAT_MOD_INVALID)
                {
                    attribs[i++] = EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT; attribs[i++] = (int)(frame.Modifier & 0xFFFFFFFF);
                    attribs[i++] = EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT; attribs[i++] = (int)(frame.Modifier >> 32);
                }
                attribs[i] = EGL_NONE;

                var image = _eglCreateImageKHR(display, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribs);
                if (image == IntPtr.Zero)
                {
                    Console.WriteLine($"[GpuPaint] eglCreateImageKHR failed (0x{Native.eglGetError():X})");
                    return;
                }
                _glBindTexture(GL_TEXTURE_2D, _texture);
                _glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, image);
                _eglDestroyImageKHR(display, image); // the texture keeps the storage
                _width = frame.Width;
                _height = frame.Height;
                _hasFrame = true;
            }
        }

        private static class Native
        {
            [DllImport("libc", SetLastError = true)] public static extern int dup(int fd);
            [DllImport("libc", SetLastError = true)] public static extern int close(int fd);
            [DllImport("libEGL.so.1")] public static extern IntPtr eglGetCurrentDisplay();
            [DllImport("libEGL.so.1")] public static extern IntPtr eglGetProcAddress(string name);
            [DllImport("libEGL.so.1")] public static extern int eglGetError();
        }
    }
}
