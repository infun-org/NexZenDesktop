using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xilium.CefGlue.Common.Helpers;

namespace Xilium.CefGlue.Avalonia
{
    /// <summary>
    /// The Avalonia builtin surface.
    /// </summary>
    internal class AvaloniaRenderSurface : OffScreenRenderSurface
    {
        private WriteableBitmap _bitmap;
        private IntPtr _destinationBuffer;

        public AvaloniaRenderSurface(Image image)
        {
            Image = image;
        }

        public override void Dispose()
        {
            base.Dispose();
            var bitmap = _bitmap;
            _bitmap = null;
            if (bitmap == null)
                return;

            // Detach before disposing: if the Image still shows the bitmap when the next frame is
            // composed, Image.Render throws ObjectDisposedException and takes the app down
            // (seen when closing the main window right after its browsers were disposed).
            void Release()
            {
                if (ReferenceEquals(Image.Source, bitmap))
                    Image.Source = null;
                bitmap.Dispose();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Release();
            else
                Dispatcher.UIThread.Post(Release);
        }

        private Image Image { get; }

        public override bool AllowsTransparency => false;

        protected override int BytesPerPixel => 4;

        protected override int RenderedHeight => _bitmap?.PixelSize.Height ?? 0;

        protected override int RenderedWidth => _bitmap?.PixelSize.Width ?? 0;

        protected override Task ExecuteInUIThread(Action action)
        {
            return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        }

        protected override void CreateBitmap(int width, int height)
        {
            // TODO handle transparency
            // Show the new bitmap before disposing the old one, so the Image never renders a disposed bitmap.
            var previous = _bitmap;
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(DefaultDpi, DefaultDpi), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            Image.Source = _bitmap;
            previous?.Dispose();
        }

        protected override Action BeginBitmapUpdate()
        {
            var lockedBuffer = _bitmap.Lock();
            _destinationBuffer = lockedBuffer.Address;
            return () =>
            {
                _destinationBuffer = IntPtr.Zero;
                lockedBuffer.Dispose();
                Image.InvalidateVisual();
            };
        }

        protected override void UpdateBitmap(IntPtr sourceBuffer, int sourceBufferSize, int stride, CefRectangle updateRegion)
        {
            unsafe
            {
                Buffer.MemoryCopy(sourceBuffer.ToPointer(), _destinationBuffer.ToPointer(), sourceBufferSize, sourceBufferSize);
            }
        }
    }
}
