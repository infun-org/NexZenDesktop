using System;
using System.IO.MemoryMappedFiles;
using System.Threading.Tasks;

namespace Xilium.CefGlue.Common.Helpers
{
    /// <summary>
    /// Render surface that supports rendering into the browser contents into and image.
    /// </summary>
    internal abstract class OffScreenRenderSurface : IDisposable
    {
        protected const int DefaultDpi = 96;

        private int _width;
        private int _height;

        private readonly object _renderLock = new object();

        private MemoryMappedFile _mappedFile;
        private MemoryMappedViewAccessor _viewAccessor;

        public virtual void Dispose()
        {
            ReleaseMemoryMap();
            GC.SuppressFinalize(this);
        }

        public abstract bool AllowsTransparency { get; }

        public int Width => _width;

        public int Height => _height;

        public int ScaledWidth => (int)Math.Ceiling(DeviceScaleFactor * _width);

        public int ScaledHeight => (int)Math.Ceiling(DeviceScaleFactor * _height);

        public float DeviceScaleFactor { get; set; } = 1;

        protected float Dpi => DeviceScaleFactor * DefaultDpi;

        private void ReleaseMemoryMap()
        {
            lock (_renderLock)
            {
                _viewAccessor?.Dispose();
                _viewAccessor = null;
                _mappedFile?.Dispose();
                _mappedFile = null;
            }
        }

        protected abstract int BytesPerPixel { get; }

        protected abstract int RenderedWidth { get; }
        protected abstract int RenderedHeight { get; }

        protected abstract void CreateBitmap(int width, int height);

        protected abstract Task ExecuteInUIThread(Action action);

        protected abstract void UpdateBitmap(IntPtr sourceBuffer, int sourceBufferSize, int stride, CefRectangle updateRegion);

        protected virtual bool UpdateDirtyRegionsOnly => false;

        protected abstract Action BeginBitmapUpdate();

        // At most one UI-thread update is queued. CEF paints at the frame rate without waiting
        // for the UI thread; queuing one update per frame let a slow UI thread (large windows,
        // WebGL pages) fall further and further behind, so the page lagged more and more.
        // Frames arriving while an update is pending only refresh the buffer it will show.
        private bool _uiUpdatePending;

        // NEXZEN_RENDER_TRACE=1: once a second, frames painted by CEF vs shown, and copy times.
        private static readonly bool s_renderTrace = Environment.GetEnvironmentVariable("NEXZEN_RENDER_TRACE") == "1";
        private readonly System.Diagnostics.Stopwatch _traceClock = System.Diagnostics.Stopwatch.StartNew();
        private int _tracePainted, _traceShown;
        private double _traceCefCopyMs, _traceUiCopyMs, _traceUiWaitMs;
        private long _traceQueuedAt;

        private void TraceSecond()
        {
            if (_traceClock.ElapsedMilliseconds < 1000)
                return;
            Console.WriteLine($"[RenderTrace] {_bufferWidth}x{_bufferHeight} painted={_tracePainted}/s shown={_traceShown}/s " +
                $"cefCopy={(_tracePainted > 0 ? _traceCefCopyMs / _tracePainted : 0):0.0}ms " +
                $"uiCopy={(_traceShown > 0 ? _traceUiCopyMs / _traceShown : 0):0.0}ms " +
                $"uiWait={(_traceShown > 0 ? _traceUiWaitMs / _traceShown : 0):0.0}ms");
            _tracePainted = _traceShown = 0;
            _traceCefCopyMs = _traceUiCopyMs = _traceUiWaitMs = 0;
            _traceClock.Restart();
        }
        private int _bufferWidth;
        private int _bufferHeight;

        public Task Render(IntPtr buffer, int width, int height, CefRectangle[] dirtyRects)
        {
            // When browser size changed - we just skip frame updating.
            // This is dirty precheck to do not do Invoke whenever is possible.
            if (width != ScaledWidth || height != ScaledHeight)
            {
                return Task.CompletedTask;
            }

            lock (_renderLock)
            {
                var pixels = width * height;
                var bytesPerPixel = BytesPerPixel;
                var byteCount = pixels * bytesPerPixel;

                if (_viewAccessor == null || _viewAccessor.Capacity < byteCount)
                {
                    ReleaseMemoryMap();

                    // needs new buffer or buffer size must increase
                    _mappedFile = MemoryMappedFile.CreateNew(null, byteCount, MemoryMappedFileAccess.ReadWrite);
                    _viewAccessor = _mappedFile.CreateViewAccessor();
                }

                var imageBuffer = _viewAccessor.SafeMemoryMappedViewHandle;

                var copyStart = s_renderTrace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                // copy pixels to our buffer, which we will then use to update the bitmap (on the UI thread)
                unsafe
                {
                    Buffer.MemoryCopy(buffer.ToPointer(), imageBuffer.DangerousGetHandle().ToPointer(), (long)imageBuffer.ByteLength, byteCount);
                }
                _bufferWidth = width;
                _bufferHeight = height;
                if (s_renderTrace)
                {
                    _tracePainted++;
                    _traceCefCopyMs += System.Diagnostics.Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                    TraceSecond();
                }

                if (_uiUpdatePending)
                {
                    return Task.CompletedTask; // the queued update will show this newer frame
                }
                _uiUpdatePending = true;
                if (s_renderTrace)
                    _traceQueuedAt = System.Diagnostics.Stopwatch.GetTimestamp();

                return ExecuteInUIThread(() =>
                {
                    lock (_renderLock)
                    {
                        _uiUpdatePending = false;
                        var frameWidth = _bufferWidth;
                        var frameHeight = _bufferHeight;

                        // quick size check - actual browser size changed?
                        if (frameWidth != ScaledWidth || frameHeight != ScaledHeight)
                        {
                            return;
                        }

                        var latestBuffer = _viewAccessor?.SafeMemoryMappedViewHandle;
                        if (latestBuffer == null || latestBuffer.IsInvalid || latestBuffer.IsClosed)
                        {
                            // buffer is not valid anymore, bail-out
                            return;
                        }

                        if (RenderedWidth != frameWidth || RenderedHeight != frameHeight)
                        {
                            CreateBitmap(frameWidth, frameHeight);
                        }

                        var uiStart = s_renderTrace ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        if (s_renderTrace)
                            _traceUiWaitMs += System.Diagnostics.Stopwatch.GetElapsedTime(_traceQueuedAt, uiStart).TotalMilliseconds;
                        // Frames may have been merged: copy the whole frame, not just this one's dirty rects.
                        InnerRender(frameWidth, frameHeight, BytesPerPixel,
                            new[] { new CefRectangle(0, 0, frameWidth, frameHeight) }, latestBuffer.DangerousGetHandle());
                        if (s_renderTrace)
                        {
                            _traceShown++;
                            _traceUiCopyMs += System.Diagnostics.Stopwatch.GetElapsedTime(uiStart).TotalMilliseconds;
                        }
                    }
                });
            }
        }

        private void InnerRender(int browserWidth, int browserHeight, int bytesPerPixel, CefRectangle[] dirtyRects, IntPtr sourceBuffer)
        {
            int stride = browserWidth * bytesPerPixel;
            int sourceBufferSize = stride * browserHeight;

            if (browserWidth == 0 || browserHeight == 0)
            {
                return;
            }

            var endBitmapUpdate = BeginBitmapUpdate();
            try
            {
                if (UpdateDirtyRegionsOnly)
                {
                    foreach (var dirtyRect in dirtyRects)
                    {

                        if (dirtyRect.Width == 0 || dirtyRect.Height == 0)
                        {
                            continue;
                        }

                        UpdateBitmap(sourceBuffer, sourceBufferSize, stride, dirtyRect);
                    }
                }
                else
                {
                    UpdateBitmap(sourceBuffer, sourceBufferSize, stride, new CefRectangle(0, 0, browserWidth, browserHeight));
                }
            }
            finally
            {
                endBitmapUpdate();
            }
        }

        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
        }
    }
}
