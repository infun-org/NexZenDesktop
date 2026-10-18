using System;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xilium.CefGlue.Common.Platform;

namespace Xilium.CefGlue.Avalonia.Platform
{
    /// <summary>
    /// The Avalonia popup control wrapper.
    /// </summary>
    internal class AvaloniaPopup : AvaloniaOffScreenControlHost, IOffScreenPopupHost
    {
        private readonly ExtendedAvaloniaPopup _popup;

        public AvaloniaPopup(ExtendedAvaloniaPopup popup, IAvaloniaList<Visual> visualChildren) : 
            base(popup, visualChildren)
        {
            _popup = popup;
        }

        protected override Visual MousePositionReferential => _popup.PlacementTarget;

        public int Width => (int)_popup.Width;

        public int Height => (int)_popup.Height;

        public int OffsetX => (int)_popup.HorizontalOffset;

        public int OffsetY => (int)_popup.VerticalOffset;

        public void MoveAndResize(int x, int y, int width, int height)
        {
            Console.WriteLine($"[CEF-POPUP] MoveAndResize: x={x} y={y} w={width} h={height}");
            Dispatcher.UIThread.Post(() =>
            {
                // Use a valid rect (height=1) just above the target Y.
                // BottomEdgeAlignedLeft will then place the popup's top-left exactly at (x, y).
                _popup.PlacementRect = new Rect(x, y - 1, width, 1);
                _popup.Placement = global::Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft;
                _popup.HorizontalOffset = 0;
                _popup.VerticalOffset = 0;
                _popup.Width = width;
                _popup.Height = height;
                Console.WriteLine($"[CEF-POPUP] MoveAndResize (UI): Rect={_popup.PlacementRect} Size={width}x{height}");
            });
        }

        public void Open()
        {
            Console.WriteLine($"[CEF-POPUP] Open() called");
            Dispatcher.UIThread.Post(() =>
            {
                Console.WriteLine($"[CEF-POPUP] Open() UI executing");
                _popup.IsOpen = true;
            });
        }

        public void Close()
        {
            Dispatcher.UIThread.Post(() => _popup.IsOpen = false);
        }
    }
}
