using Avalonia.Input;
using System.Collections.Generic;
using Avalonia.Threading;

namespace Xilium.CefGlue.Avalonia
{
    /// <summary>
    /// Provides handles for mouse cursors.
    /// </summary>
    internal class CursorsProvider
    {
        private static readonly IDictionary<CefCursorType, StandardCursorType> _cefCursorTypeToAvaloniaMap = new Dictionary<CefCursorType, StandardCursorType>() {
            { CefCursorType.Pointer, StandardCursorType.Arrow },
            { CefCursorType.Cross, StandardCursorType.Cross },
            { CefCursorType.Hand, StandardCursorType.Hand },
            { CefCursorType.IBeam, StandardCursorType.Ibeam },
            { CefCursorType.Wait, StandardCursorType.Wait },
            { CefCursorType.Help, StandardCursorType.Help },
            { CefCursorType.EastResize, StandardCursorType.RightSide },
            { CefCursorType.NorthResize, StandardCursorType.TopSide },
            { CefCursorType.NorthEastResize, StandardCursorType.TopRightCorner },
            { CefCursorType.NorthWestResize, StandardCursorType.TopLeftCorner },
            { CefCursorType.SouthResize, StandardCursorType.BottomSide },
            { CefCursorType.SouthEastResize, StandardCursorType.BottomRightCorner },
            { CefCursorType.SouthWestResize, StandardCursorType.BottomLeftCorner },
            { CefCursorType.WestResize, StandardCursorType.LeftSide },
            { CefCursorType.NorthSouthResize, StandardCursorType.SizeNorthSouth },
            { CefCursorType.EastWestResize, StandardCursorType.SizeWestEast },
            { CefCursorType.NorthEastSouthWestResize, StandardCursorType.TopRightCorner },
            { CefCursorType.NorthWestSouthEastResize, StandardCursorType.TopLeftCorner },
            { CefCursorType.ColumnResize, StandardCursorType.SizeWestEast },
            { CefCursorType.RowResize, StandardCursorType.SizeNorthSouth },
            { CefCursorType.MiddlePanning, StandardCursorType.SizeAll },
            { CefCursorType.EastPanning, StandardCursorType.RightSide },
            { CefCursorType.NorthPanning, StandardCursorType.TopSide },
            { CefCursorType.NorthEastPanning, StandardCursorType.TopRightCorner },
            { CefCursorType.NorthWestPanning, StandardCursorType.TopLeftCorner },
            { CefCursorType.SouthPanning, StandardCursorType.BottomSide },
            { CefCursorType.SouthEastPanning, StandardCursorType.BottomRightCorner },
            { CefCursorType.SouthWestPanning, StandardCursorType.BottomLeftCorner },
            { CefCursorType.WestPanning, StandardCursorType.LeftSide },
            { CefCursorType.Move, StandardCursorType.SizeAll },
            { CefCursorType.VerticalText, StandardCursorType.Ibeam },
            { CefCursorType.Cell, StandardCursorType.Cross },
            { CefCursorType.ContextMenu, StandardCursorType.Arrow },
            { CefCursorType.Alias, StandardCursorType.DragLink },
            { CefCursorType.Progress, StandardCursorType.AppStarting },
            { CefCursorType.NoDrop, StandardCursorType.No },
            { CefCursorType.Copy, StandardCursorType.DragCopy },
            { CefCursorType.None, StandardCursorType.None },
            { CefCursorType.NotAllowed, StandardCursorType.No },
            { CefCursorType.ZoomIn, StandardCursorType.Arrow },
            { CefCursorType.ZoomOut, StandardCursorType.Arrow },
            { CefCursorType.Grab, StandardCursorType.Hand },
            { CefCursorType.Grabbing, StandardCursorType.Hand },
            { CefCursorType.MiddlePanningVertical, StandardCursorType.SizeNorthSouth },
            { CefCursorType.MiddlePanningHorizontal, StandardCursorType.SizeWestEast },
            { CefCursorType.Custom, StandardCursorType.Arrow },
            { CefCursorType.DragAndDropNone, StandardCursorType.No },
            { CefCursorType.DragAndDropMove, StandardCursorType.DragMove },
            { CefCursorType.DragAndDropCopy, StandardCursorType.DragCopy },
            { CefCursorType.DragAndDropLink, StandardCursorType.DragLink },
        };

        private static readonly IDictionary<CefDragOperationsMask, StandardCursorType> _cefDragOpsToAvaloniaMap = new Dictionary<CefDragOperationsMask, StandardCursorType>() {
            { CefDragOperationsMask.Copy, StandardCursorType.DragCopy },
            { CefDragOperationsMask.Link, StandardCursorType.DragLink },
            { CefDragOperationsMask.Move, StandardCursorType.DragMove }
        };

        // Avalonia cursors wrap native (X11) cursor handles: create each kind once, and only on the
        // UI thread. CEF reports cursor changes on its own thread; creating them there called into
        // the X11 connection from a second thread and left every window with the default arrow.
        private static readonly Dictionary<StandardCursorType, Cursor> _cache = new();

        private static Cursor Get(StandardCursorType type)
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!_cache.TryGetValue(type, out var cursor))
            {
                cursor = new Cursor(type);
                _cache[type] = cursor;
            }
            return cursor;
        }

        /// <summary>The cursor for a CEF cursor type. UI thread only.</summary>
        public static Cursor GetCursorFromCefType(CefCursorType cursorType)
            => Get(_cefCursorTypeToAvaloniaMap.TryGetValue(cursorType, out var avaloniaCursor)
                ? avaloniaCursor
                : StandardCursorType.Arrow);

        /// <summary>The drag cursor for the allowed drop operations. UI thread only.</summary>
        public static Cursor GetCursorFromCefType(CefDragOperationsMask ops)
            => Get(_cefDragOpsToAvaloniaMap.TryGetValue(ops, out var avaloniaCursor)
                ? avaloniaCursor
                : StandardCursorType.No);
    }
}
