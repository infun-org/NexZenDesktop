using System;
using System.IO;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using System.Linq;
using Avalonia.Platform.Storage;

namespace Xilium.CefGlue.Avalonia
{
    internal static class InputExtensions
    {
        private static readonly DataFormat<string> UriListTextFormat = DataFormat.CreateStringPlatformFormat("text/uri-list");
        private static readonly DataFormat<byte[]> UriListBytesFormat = DataFormat.CreateBytesPlatformFormat("text/uri-list");
        private static readonly DataFormat<string> KdeUriListTextFormat = DataFormat.CreateStringPlatformFormat("application/x-kde4-urilist");
        private static readonly DataFormat<byte[]> KdeUriListBytesFormat = DataFormat.CreateBytesPlatformFormat("application/x-kde4-urilist");
        private static readonly string[] FileListMimeTypes =
        {
            "text/uri-list",
            "application/x-kde4-urilist",
            "application/vnd.portal.files",
            "application/vnd.portal.filetransfer"
        };

        public static bool HasFileListPayload(this IDataTransfer dataTransfer)
        {
            if (dataTransfer.TryGetFiles()?.Length > 0)
                return true;

            return dataTransfer.Formats.Any(format =>
                FileListMimeTypes.Contains(format.Identifier, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Convert a mouse event args into a cef mouse event.
        /// </summary>
        /// <param name="eventArgs">The mouse event args</param>
        /// <param name="mouseCoordinatesReferencial">The element used as the positioning referential</param>
        /// <returns></returns>
        public static CefMouseEvent AsCefMouseEvent(this PointerEventArgs eventArgs, Visual mousePositionReferential)
        {
            var cursorPos = mousePositionReferential.IsAttachedToVisualTree() ? eventArgs.GetPosition(mousePositionReferential) : new Point(0,0);

            return new CefMouseEvent((int) cursorPos.X, (int) cursorPos.Y, eventArgs.AsCefEventFlags() | eventArgs.KeyModifiers.AsCefKeyboardModifiers());
        }

        /// <summary>
        /// Convert a mouse button into a cef mouse button.
        /// </summary>
        /// <param name="button"></param>
        /// <returns></returns>
        public static CefMouseButtonType AsCefMouseButtonType(this PointerEventArgs eventArgs)
        {
            switch (eventArgs.GetCurrentPoint(null).Properties.PointerUpdateKind.GetMouseButton())
            {
                case MouseButton.Middle:
                    return CefMouseButtonType.Middle;
                case MouseButton.Right:
                    return CefMouseButtonType.Right;
                default:
                    return CefMouseButtonType.Left;
            }
        }

        /// <summary>
        /// Convert a key event into a cef key event.
        /// </summary>
        /// <param name="eventArgs"></param>
        /// <param name="isKeyUp"></param>
        /// <returns></returns>
        public static CefKeyEvent AsCefKeyEvent(this KeyEventArgs eventArgs, bool isKeyUp)
        {
            var modifiers = eventArgs.KeyModifiers.AsCefKeyboardModifiers();
            var windowsKeyCode = KeyInterop.VirtualKeyFromKey(eventArgs.Key);
            return new CefKeyEvent()
            {
                EventType = isKeyUp ? CefKeyEventType.KeyUp : CefKeyEventType.RawKeyDown,
                WindowsKeyCode = windowsKeyCode,
                NativeKeyCode = LinuxNativeKey.FromVirtualKey(windowsKeyCode),
                IsSystemKey = eventArgs.Key == Key.System,
                Modifiers = modifiers
            };
        }

        /// <summary>
        /// Convert mouse modifiers into cef flags.
        /// </summary>
        /// <param name="keyboardModifiers"></param>
        /// <returns></returns>
        public static CefEventFlags AsCefEventFlags(this PointerEventArgs eventArgs)
        {
            var flags = CefEventFlags.None;
            var properties = eventArgs.GetCurrentPoint(null).Properties;

            if (properties.IsLeftButtonPressed)
            {
                flags |= CefEventFlags.LeftMouseButton;
            }

            if (properties.IsMiddleButtonPressed)
            {
                flags |= CefEventFlags.MiddleMouseButton;
            }

            if (properties.IsRightButtonPressed)
            {
                flags |= CefEventFlags.RightMouseButton;
            }

            return flags;
        }

        /// <summary>
        /// Convert keyboard modifiers into cef flags.
        /// </summary>
        /// <param name="keyboardModifiers"></param>
        /// <returns></returns>
        public static CefEventFlags AsCefKeyboardModifiers(this KeyModifiers keyboardModifiers)
        {
            var modifiers = new CefEventFlags();

            if (keyboardModifiers.HasFlag(KeyModifiers.Alt))
            {
                modifiers |= CefEventFlags.AltDown;
            }

            if (keyboardModifiers.HasFlag(KeyModifiers.Control))
            {
                modifiers |= CefEventFlags.ControlDown;
            }

            if (keyboardModifiers.HasFlag(KeyModifiers.Shift))
            {
                modifiers |= CefEventFlags.ShiftDown;
            }

            if (keyboardModifiers.HasFlag(KeyModifiers.Meta))
            {
                modifiers |= CefEventFlags.CommandDown;
            }

            return modifiers;
        }

        /// <summary>
        /// Convert a drag event args into a cef mouse event.
        /// </summary>
        /// <param name="eventArgs">The drag event args</param>
        /// <param name="mouseCoordinatesReferencial">The element used as the positioning referential</param>
        /// <returns></returns>
        public static CefMouseEvent AsCefMouseEvent(this DragEventArgs eventArgs, Visual mouseCoordinatesReferencial)
        {
            var cursorPos = eventArgs.GetPosition(mouseCoordinatesReferencial);

            return new CefMouseEvent((int)cursorPos.X, (int)cursorPos.Y, eventArgs.KeyModifiers.AsCefKeyboardModifiers());
        }

        /// <summary>
        /// Converts a drag drop effects to Cef Drag Operations
        /// </summary>
        /// <param name="dragDropEffects">The drag drop effects.</param>
        /// <returns></returns>
        public static CefDragOperationsMask AsCefDragOperationsMask(this DragDropEffects dragDropEffects)
        {
            var operationsCount = 0;
            var operations = CefDragOperationsMask.None;

            if (dragDropEffects.HasFlag(DragDropEffects.Copy))
            {
                operations |= CefDragOperationsMask.Copy;
                operationsCount++;
            }

            if (dragDropEffects.HasFlag(DragDropEffects.Move))
            {
                operations |= CefDragOperationsMask.Move;
                operationsCount++;
            }

            if (dragDropEffects.HasFlag(DragDropEffects.Link))
            {
                operations |= CefDragOperationsMask.Link;
                operationsCount++;
            }

            if (operationsCount == 3)
            {
                return CefDragOperationsMask.Every;
            }

            return operations;
        }

        /// <summary>
        /// Gets the drag effects.
        /// </summary>
        /// <param name="mask">The mask.</param>
        /// <returns></returns>
        public static DragDropEffects AsDragDropEffects(this CefDragOperationsMask mask)
        {
            if (mask.HasFlag(CefDragOperationsMask.Every))
            {
                return DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            }

            if (mask.HasFlag(CefDragOperationsMask.Copy))
            {
                return DragDropEffects.Copy;
            }

            if (mask.HasFlag(CefDragOperationsMask.Move))
            {
                return DragDropEffects.Move;
            }

            if (mask.HasFlag(CefDragOperationsMask.Link))
            {
                return DragDropEffects.Link;
            }

            return DragDropEffects.None;
        }

        /// <summary>
        /// Gets the drag data
        /// </summary>
        /// <param name="e">The <see cref="DragEventArgs"/> instance containing the event data.</param>
        /// <returns></returns>
        public static CefDragData GetDragData(this DragEventArgs e, ICollection<string> temporaryFiles = null)
        {
            var dragData = CefDragData.Create();

            // Files
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                foreach (var file in files)
                {
                    var filePath = file.TryGetLocalPath();
                    if (filePath != null)
                    {
                        var displayName = System.IO.Path.GetFileName(filePath);
                        dragData.AddFile(filePath.Replace("\\", "/"), displayName);
                    }
                }
            }

            // KDE's Dolphin may expose the standard URI list and its KDE
            // variant without Avalonia materializing either as IStorageItem.
            // Convert local file URIs explicitly so CEF receives normal paths.
            if (files == null || files.Length == 0)
            {
                var addedPaths = new HashSet<string>(StringComparer.Ordinal);
                AddUriListFiles(dragData, ReadUriList(e.DataTransfer, UriListTextFormat, UriListBytesFormat), addedPaths);
                AddUriListFiles(dragData, ReadUriList(e.DataTransfer, KdeUriListTextFormat, KdeUriListBytesFormat), addedPaths);
            }

            // Some applications drag bitmap data instead of a file path. CEF's
            // drop target API accepts files, so materialize that bitmap as a
            // short-lived PNG for the duration of the native drag operation.
            if (files == null || files.Length == 0)
            {
                var bitmap = e.DataTransfer.TryGetBitmap();
                if (bitmap != null)
                {
                    var path = Path.Combine(Path.GetTempPath(), $"nexzen-drop-{Guid.NewGuid():N}.png");
                    try
                    {
                        using (var stream = File.Create(path))
                            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        temporaryFiles?.Add(path);
                        dragData.AddFile(path.Replace("\\", "/"), Path.GetFileName(path));
                    }
                    catch
                    {
                        try { File.Delete(path); } catch { }
                    }
                }
            }

            // Text
            var text = e.DataTransfer.TryGetText();
            if (text != null)
            {
                dragData.SetFragmentText(text);
            }

            return dragData;
        }

        private static string ReadUriList(IDataTransfer dataTransfer, DataFormat<string> textFormat, DataFormat<byte[]> bytesFormat)
        {
            var text = dataTransfer.TryGetValue(textFormat);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            var bytes = dataTransfer.TryGetValue(bytesFormat);
            return bytes == null || bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static void AddUriListFiles(CefDragData dragData, string uriList, ISet<string> addedPaths)
        {
            if (string.IsNullOrWhiteSpace(uriList))
                return;

            foreach (var line in uriList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = line.Trim();
                if (value.Length == 0 || value[0] == '#'
                    || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || !uri.IsFile)
                    continue;

                var path = uri.LocalPath;
                if (!File.Exists(path) || !addedPaths.Add(path))
                    continue;

                dragData.AddFile(path.Replace("\\", "/"), Path.GetFileName(path));
            }
        }
    }
}
