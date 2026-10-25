using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Platform;

namespace Xilium.CefGlue.Avalonia.Platform
{
    internal enum ImeState
    {
        Idle,
        Composing,
        PendingCommit
    }

    /// <summary>
    /// The Avalonia control wrapper.
    /// </summary>
    internal class AvaloniaOffScreenControlHost : AvaloniaControl, IOffScreenControlHost
    {
        public ImeState CurrentImeState { get; set; } = ImeState.Idle;

        /// <summary>
        /// Leading part of the IME's pending (preedit) text that was already typed into the page
        /// (digits, see CefTextInputMethodClient). The IME commits it again later; that copy is dropped.
        /// </summary>
        public string TypedPreeditPrefix { get; set; } = "";

        // Numpad digits the IME swallows without any output (no key down, no preedit, no
        // commit; fcitx5 Lotus with NumLock on) are typed on key up instead.
        private readonly HashSet<Key> _keysDownSeen = new();
        private bool _imeOutputSinceKey;

        /// <summary>Called when the IME shows or commits text, so a key up does not type again.</summary>
        internal void NoteImeOutput() => _imeOutputSinceKey = true;

        private static char? NumPadDigit(Key key)
            => key >= Key.NumPad0 && key <= Key.NumPad9 ? (char)('0' + (key - Key.NumPad0)) : null;

        /// <summary>
        /// Types digits as complete keystrokes (key down, character, key up). A bare Char event
        /// that no key down announced is dropped by Chromium here (seen with numpad digits that
        /// the IME swallowed), so the page needs the whole keystroke.
        /// </summary>
        internal static void SendDigitKeystrokes(CefBrowserHost host, string digits)
        {
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.SendDigitKeystrokes] '{digits}'");
            foreach (var c in digits)
            {
                var virtualKey = CharKeyCode.WindowsKeyCode(c); // VK_0..VK_9
                var native = LinuxNativeKey.FromCharacter(c);
                host.SendKeyEvent(new CefKeyEvent
                {
                    EventType = CefKeyEventType.RawKeyDown,
                    WindowsKeyCode = virtualKey,
                    NativeKeyCode = native,
                    Character = c,
                    UnmodifiedCharacter = c
                });
                host.SendKeyEvent(new CefKeyEvent
                {
                    EventType = CefKeyEventType.Char,
                    WindowsKeyCode = virtualKey,
                    NativeKeyCode = native,
                    Character = c,
                    UnmodifiedCharacter = c
                });
                host.SendKeyEvent(new CefKeyEvent
                {
                    EventType = CefKeyEventType.KeyUp,
                    WindowsKeyCode = virtualKey,
                    NativeKeyCode = native,
                    Character = c,
                    UnmodifiedCharacter = c
                });
            }
        }

        /// <summary>Deletes characters before the caret, as Backspace keystrokes.</summary>
        internal static void SendBackspaces(CefBrowserHost host, int count)
        {
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.SendBackspaces] {count}");
            var native = LinuxNativeKey.FromVirtualKey(0x08);
            for (var i = 0; i < count; i++)
            {
                host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.RawKeyDown, WindowsKeyCode = 0x08, NativeKeyCode = native });
                host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.KeyUp, WindowsKeyCode = 0x08, NativeKeyCode = native });
            }
        }

        /// <summary>
        /// The IME cleared its pending text. If no commit of the typed digits follows shortly,
        /// the IME dropped them, so they are removed from the page as well.
        /// </summary>
        internal void RemoveTypedPreeditIfNotCommitted(string typed)
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (TypedPreeditPrefix != typed)
                    return; // committed (or changed) in the meantime
                TypedPreeditPrefix = "";
                if (_control is AvaloniaCefBrowser browser && browser.IsBrowserInitialized
                    && browser.GetBrowserHost() is { } host)
                    SendBackspaces(host, typed.Length);
            }, TimeSpan.FromMilliseconds(150));
        }

        /// <summary>Sends text to the page as plain character events.</summary>
        internal static void SendChars(CefBrowserHost host, string text)
        {
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.SendChars] '{text}'");
            foreach (var c in text)
            {
                host.SendKeyEvent(new CefKeyEvent()
                {
                    EventType = CefKeyEventType.Char,
                    WindowsKeyCode = CharKeyCode.WindowsKeyCode(c),
                    NativeKeyCode = LinuxNativeKey.FromCharacter(c),
                    Character = c,
                    UnmodifiedCharacter = c,
                    Modifiers = CharKeyCode.Modifiers(c)
                });
            }
        }
        public Rect CursorRect { get; private set; } = new Rect(0, 0, 1, 1);
        private CefTextInputMethodClient _imeClient;
        // TODO avalonia: get value from OS
        private const int MouseWheelDelta = 100;
        // Use centralised ImeConfig instead of a per-class env-var read (Phase 5)
        private static readonly bool s_isTraceEnabled = ImeConfig.IsTraceEnabled;

        private IDisposable _windowStateChangedObservable;
        private IDisposable _windowVisibleObservable;

        private PointerPressedEventArgs _lastPointerEvent;
        private Cursor _currentDragCursor;
        private Cursor _previousCursor;
        private readonly List<string> _dragTemporaryFiles = new();
        private DateTime _lastDragOverLog = DateTime.MinValue;

        public event Action LostFocus;
        public event KeyEventHandler KeyDown;
        public event KeyEventHandler KeyUp;
        public event TextInputEventHandler TextInput;
        public event Action<IOffScreenControlHost, CefMouseEvent, CefMouseButtonType, int> MouseButtonPressed;
        public event Action<CefMouseEvent, CefMouseButtonType, int> MouseButtonReleased;
        public event Action<CefMouseEvent> MouseLeave;
        public event Action<CefMouseEvent> MouseMoved;
        public event Action<CefMouseEvent, int, int> MouseWheelChanged;
        public event Action<CefMouseEvent, CefDragData, CefDragOperationsMask> DragEnter;
        public event Action<CefMouseEvent, CefDragOperationsMask> DragOver;
        public event Action DragLeave;
        public event Action<CefMouseEvent, CefDragOperationsMask> Drop;
        public event Action<float> ScreenInfoChanged;
        public event Action<bool> VisibilityChanged;

        public AvaloniaOffScreenControlHost(Control control, IAvaloniaList<Visual> visualChildren) :
            base(control, visualChildren)
        {
            DragDrop.SetAllowDrop(control, true);
            Console.WriteLine($"[CEF-DND] handler attached control={control.GetType().Name}");

            control.AttachedToVisualTree += OnAttachedToVisualTree;
            control.DetachedFromVisualTree += OnDetachedFromVisualTree;

            control.LostFocus += OnLostFocus;
            
            control.GetPropertyChangedObservable(Visual.IsVisibleProperty).Subscribe(new ActionObserver<AvaloniaPropertyChangedEventArgs>(e =>
            {
                if (e.NewValue is false)
                {
                    SetTooltip(null);
                }
                PublishHostVisibility();
            }));

            control.PointerMoved += OnPointerMoved;
            control.PointerExited += OnPointerExited;

            // Use AddHandler with handledEventsToo=true so CEF always receives
            // mousedown/mouseup even when Avalonia's gesture recognizer (Tapped/DoubleTapped)
            // marks the PointerReleased event as handled on a quick click.
            control.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
                RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
            control.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
                RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);

            control.PointerWheelChanged += OnPointerWheelChanged;

            const RoutingStrategies dragRoutes = RoutingStrategies.Bubble;
            control.AddHandler(DragDrop.DragEnterEvent, OnDragEnter, dragRoutes, handledEventsToo: true);
            control.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave, dragRoutes, handledEventsToo: true);
            control.AddHandler(DragDrop.DragOverEvent, OnDragOver, dragRoutes, handledEventsToo: true);
            control.AddHandler(DragDrop.DropEvent, OnDrop, dragRoutes, handledEventsToo: true);

            control.KeyDown += OnKeyDown;
            control.KeyUp += OnKeyUp;
            control.TextInput += OnTextInput;

            control.AddHandler(global::Avalonia.Input.InputElement.TextInputMethodClientRequestedEvent, (s, e) =>
            {
                // IME bridge is Linux-only. On Windows/macOS, CEF handles IME natively
                // via the windowing system — our bridge must not interfere.
                if (CefRuntime.Platform != CefRuntimePlatform.Linux) return;

                // Phase 5 — Kill-switch: skip IME bridge entirely when disabled
                if (ImeConfig.IsDisabled) return;

                if (s_isTraceEnabled)
                    Console.WriteLine("[Trace][AvaloniaOffScreenControlHost] TextInputMethodClientRequestedEvent fired");
                _imeClient = new CefTextInputMethodClient(_control, this);
                e.Client = _imeClient;
            });

            var image = CreateImage();
            var viewbox = new Viewbox()
            {
                Child = image,
                Stretch = Stretch.Fill,
                StretchDirection = StretchDirection.Both
            };
            // Viewbox is only a visual child, so it does not inherit AllowDrop; Avalonia
            // only checks the hit-tested element, so OS drags over the page found no target.
            DragDrop.SetAllowDrop(viewbox, true);
            DragDrop.SetAllowDrop(image, true);
            _cursorTarget = image;
            SetContent(viewbox);
            RenderSurface = new AvaloniaRenderSurface(image);
        }

        public OffScreenRenderSurface RenderSurface { get; }

        public void HandleImeCompositionRangeChanged(CefRange selectedRange, CefRectangle[] characterBounds)
        {
            if (characterBounds == null || characterBounds.Length == 0)
                return;

            // CEF returns bounds in view coordinates (physical pixels before DPI scaling).
            // Avalonia CursorRectangle expects logical pixels (DIPs).
            var scaleFactor = RenderSurface?.DeviceScaleFactor ?? 1f;
            var first = characterBounds[0];

            Dispatcher.UIThread.Post(() =>
            {
                CursorRect = new Rect(
                    first.X / scaleFactor,
                    first.Y / scaleFactor,
                    Math.Max(1, first.Width / scaleFactor),
                    Math.Max(1, first.Height / scaleFactor)
                );
                _imeClient?.NotifyCursorRectangleChanged();
                if (s_isTraceEnabled)
                    Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost] ImeCompositionRange => CursorRect={CursorRect}");
            });
        }

        private void OnTextInput(object sender, TextInputEventArgs e)
        {
            NoteImeOutput();
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnTextInput] Text='{e.Text}'");

            var handled = false;

            // Digits the IME kept pending were typed already; don't type them a second time.
            var text = e.Text ?? "";
            if (TypedPreeditPrefix.Length > 0)
            {
                if (text.StartsWith(TypedPreeditPrefix, StringComparison.Ordinal))
                    text = text.Substring(TypedPreeditPrefix.Length);
                TypedPreeditPrefix = "";
                if (text.Length == 0)
                {
                    CurrentImeState = ImeState.Idle;
                    e.Handled = true;
                    return;
                }
            }

            if (CurrentImeState == ImeState.Composing || CurrentImeState == ImeState.PendingCommit)
            {
                // IME commit path is Linux-only. On Windows/macOS, CEF handles text input natively.
                if (CefRuntime.Platform != CefRuntimePlatform.Linux)
                {
                    TextInput?.Invoke(text, out handled);
                    e.Handled = handled;
                    return;
                }

                if (s_isTraceEnabled)
                    Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnTextInput] Intercepted by IME. Committing text: {text}");
                
                if (_control is AvaloniaCefBrowser browser)
                {
                    // Phase 5 — Null safety: skip if browser not yet initialised
                    if (!browser.IsBrowserInitialized) return;

                    var host = browser.GetBrowserHost();
                    if (host != null)
                    {
                        // Workaround for Chromium Linux ImeCommitText bugs:
                        // Cancel the existing composition to clear the preedit.
                        host.ImeCancelComposition();

                        // Send the committed text as standard Char events.
                        SendChars(host, text);

                        host.Invalidate(CefPaintElementType.View);
                        CurrentImeState = ImeState.Idle;
                    }
                }
                handled = true;
            }
            else
            {
                TextInput?.Invoke(text, out handled);
            }
            
            e.Handled = handled;

            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnTextInput] After Invoke: Handled={handled}");
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnKeyUp] Key={e.Key}");

            var keyDownSeen = _keysDownSeen.Remove(e.Key);
            var imeOutput = _imeOutputSinceKey;
            _imeOutputSinceKey = false;
            if (CefRuntime.Platform == CefRuntimePlatform.Linux && !keyDownSeen && !imeOutput
                && NumPadDigit(e.Key) is char digit
                && _control is AvaloniaCefBrowser numpadBrowser && numpadBrowser.IsBrowserInitialized
                && numpadBrowser.GetBrowserHost() is { } numpadHost)
            {
                SendDigitKeystrokes(numpadHost, digit.ToString());
                e.Handled = true;
                return;
            }

            if (CefRuntime.Platform == CefRuntimePlatform.Linux
                && !ImeConfig.IsDisabled
                && CurrentImeState == ImeState.Composing)
            {
                e.Handled = false;
                return;
            }

            var handled = false;
            KeyUp?.Invoke(e.AsCefKeyEvent(true), out handled);
            e.Handled = handled;
            
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnKeyUp] After Invoke: Handled={handled}");
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnKeyDown] Key={e.Key}, Modifiers={e.KeyModifiers}");
            _keysDownSeen.Add(e.Key);
            _imeOutputSinceKey = false;

            if (CurrentImeState == ImeState.PendingCommit)
            {
                if (s_isTraceEnabled)
                    Console.WriteLine("[Trace][AvaloniaOffScreenControlHost.OnKeyDown] Orphaned PendingCommit detected, resetting to Idle.");
                CurrentImeState = ImeState.Idle;
            }

            // While a syllable is composing, the raw key must not also reach the
            // page. Google's results box treats that key as a reset and wipes the
            // preedit until Space commits it.
            if (CefRuntime.Platform == CefRuntimePlatform.Linux
                && !ImeConfig.IsDisabled
                && CurrentImeState == ImeState.Composing)
            {
                e.Handled = false;
                return;
            }

            var handled = false;
            KeyDown?.Invoke(e.AsCefKeyEvent(false), out handled);

            var key = e.Key;
            if (key == Key.Tab  // Avoid tabbing out the web browser control
                || key == Key.Enter || key == Key.Return // Keep Return inside the page (Translate, chat, etc.)
                || key == Key.Home || key == Key.End // Prevent keyboard navigation using home and end keys
                || key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right // Prevent keyboard navigation using arrows
            )
            {
                handled = true;
            }

            e.Handled = handled;

            if (s_isTraceEnabled)
                Console.WriteLine($"[Trace][AvaloniaOffScreenControlHost.OnKeyDown] After Invoke: Handled={handled}");
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            ApplyCursor(_previousCursor); // restore cursor
            var offeredEffects = e.DragEffects;
            var effects = PrepareDropEffects(e);
            Console.WriteLine($"[CEF-DND] Avalonia drop offered={offeredEffects} accepted={effects} files={e.DataTransfer.TryGetFiles()?.Length ?? 0} bitmap={e.DataTransfer.TryGetBitmap() != null} text={!string.IsNullOrEmpty(e.DataTransfer.TryGetText())}");
            if (effects != DragDropEffects.None)
                Drop?.Invoke(e.AsCefMouseEvent(MousePositionReferential), effects.AsCefDragOperationsMask());
            ScheduleDragFileCleanup();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            var effects = PrepareDropEffects(e);
            if (DateTime.UtcNow - _lastDragOverLog >= TimeSpan.FromSeconds(1))
            {
                _lastDragOverLog = DateTime.UtcNow;
                Console.WriteLine($"[CEF-DND] Avalonia over accepted={effects}");
            }
            DragOver?.Invoke(e.AsCefMouseEvent(MousePositionReferential), effects.AsCefDragOperationsMask());
            ApplyCursor(_currentDragCursor);
        }

        private void OnDragLeave(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[CEF-DND] Avalonia leave");
            DragLeave?.Invoke();
            e.Handled = true;
            ScheduleDragFileCleanup();
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            _previousCursor = _control.Cursor;
            ScheduleDragFileCleanup();
            var offeredEffects = e.DragEffects;
            var fileCount = e.DataTransfer.TryGetFiles()?.Length ?? 0;
            var hasBitmap = e.DataTransfer.TryGetBitmap() != null;
            var hasText = !string.IsNullOrEmpty(e.DataTransfer.TryGetText());
            var effects = PrepareDropEffects(e);
            var dragData = e.GetDragData(_dragTemporaryFiles);
            var formats = string.Join(",", e.DataTransfer.Formats.Select(format => format.Identifier));
            Console.WriteLine($"[CEF-DND] Avalonia enter offered={offeredEffects} accepted={effects} files={fileCount} bitmap={hasBitmap} text={hasText} formats={formats} cefFiles={dragData.GetFileNames()?.Length ?? 0}");
            DragEnter?.Invoke(e.AsCefMouseEvent(MousePositionReferential), dragData, effects.AsCefDragOperationsMask());
        }

        private static DragDropEffects PrepareDropEffects(DragEventArgs e)
        {
            var effects = e.DragEffects;
            if (effects == DragDropEffects.None)
            {
                var hasFiles = e.DataTransfer.HasFileListPayload();
                var hasBitmap = e.DataTransfer.TryGetBitmap() != null;
                var hasText = !string.IsNullOrEmpty(e.DataTransfer.TryGetText());
                if (hasFiles || hasBitmap || hasText)
                    effects = DragDropEffects.Copy;
            }

            e.DragEffects = effects;
            e.Handled = effects != DragDropEffects.None;
            return effects;
        }

        private void ScheduleDragFileCleanup()
        {
            if (_dragTemporaryFiles.Count == 0)
                return;

            var files = new List<string>(_dragTemporaryFiles);
            _dragTemporaryFiles.Clear();
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Temporary image cleanup is best-effort.
                    }
                }
            });
        }

        private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
        {
            MouseWheelChanged?.Invoke(e.AsCefMouseEvent(MousePositionReferential), (int)e.Delta.X * MouseWheelDelta, (int)e.Delta.Y * MouseWheelDelta);
        }

        private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
        {
            var button = e.AsCefMouseButtonType();
            Console.WriteLine($"[CEF-INPUT] OnPointerReleased: button={button} handled={e.Handled} pos={e.GetPosition(_control):F1}");
            MouseButtonReleased?.Invoke(e.AsCefMouseEvent(MousePositionReferential), button, _lastPointerEvent?.ClickCount ?? 1);
            // if (button == CefMouseButtonType.Left)
            // {
            //     e.Pointer.Capture(null);
            // }
        }

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
        {
            var button = e.AsCefMouseButtonType();
            Console.WriteLine($"[CEF-INPUT] OnPointerPressed: button={button} clickCount={e.ClickCount} handled={e.Handled} pos={e.GetPosition(_control):F1}");
            _lastPointerEvent = e;
            
            if (button == CefMouseButtonType.Left)
            {
                bool focused = _control.Focus();
                if (s_isTraceEnabled) Console.WriteLine($"[Trace] Focus() returned {focused}");
                // e.Pointer.Capture(_control);
            }

            MouseButtonPressed?.Invoke(this, e.AsCefMouseEvent(MousePositionReferential), button, e.ClickCount);
        }

        private void OnPointerExited(object sender, PointerEventArgs e)
        {
            MouseLeave?.Invoke(e.AsCefMouseEvent(MousePositionReferential));
        }

        private void OnPointerMoved(object sender, PointerEventArgs e)
        {
            MouseMoved?.Invoke(e.AsCefMouseEvent(MousePositionReferential));
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (s_isTraceEnabled)
                Console.WriteLine("[Trace][AvaloniaOffScreenControlHost.OnLostFocus]");

            // Digits typed from a pending composition: the IME drops that composition now.
            TypedPreeditPrefix = "";

            // Phase 4+5: Cancel any in-progress composition when focus is lost
            // to prevent ghost preedit text remaining in the browser.
            if (CurrentImeState == ImeState.Composing)
            {
                if (_control is AvaloniaCefBrowser browser && browser.IsBrowserInitialized)
                {
                    var cefHost = browser.GetBrowserHost();
                    if (cefHost != null)
                    {
                        if (s_isTraceEnabled)
                            Console.WriteLine("[Trace][AvaloniaOffScreenControlHost.OnLostFocus] Cancelling active composition.");
                        cefHost.ImeCancelComposition();
                    }
                }
                CurrentImeState = ImeState.Idle;
            }

            LostFocus?.Invoke();
        }

        private void OnDetachedFromVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            _lastPointerEvent = null;
            _previousCursor = null;
            _currentDragCursor = null;
            _windowStateChangedObservable?.Dispose();
            _windowStateChangedObservable = null;
            _windowVisibleObservable?.Dispose();
            _windowVisibleObservable = null;
            SetTooltip(null); // Clear tooltip
            // The adapter subscribes only once CEF has created the browser, which needs a size.
            // A background tab that was never shown has no subscriber when its window closes;
            // invoking null here crashed the whole app (CEF's SIGSEGV handler bypasses .NET's NRE).
            VisibilityChanged?.Invoke(false);
        }

        private void OnAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(_control);
            if (topLevel is Window newWindow)
            {
                _windowStateChangedObservable = newWindow.GetPropertyChangedObservable(Window.WindowStateProperty).Subscribe(new ActionObserver<AvaloniaPropertyChangedEventArgs>(_ => PublishHostVisibility()));
                _windowVisibleObservable = newWindow.GetPropertyChangedObservable(Window.IsVisibleProperty).Subscribe(new ActionObserver<AvaloniaPropertyChangedEventArgs>(_ => PublishHostVisibility()));
            }
            PublishHostVisibility();
            if (topLevel != null && topLevel.RenderScaling != RenderSurface.DeviceScaleFactor)
            {
                RenderSurface.DeviceScaleFactor = (float)topLevel.RenderScaling;
                ScreenInfoChanged?.Invoke(RenderSurface.DeviceScaleFactor);
            }
        }

        private void PublishHostVisibility()
        {
            VisibilityChanged?.Invoke(HostWindowAllowsPaint() && _control.IsEffectivelyVisible);
        }

        private bool HostWindowAllowsPaint()
        {
            var top = TopLevel.GetTopLevel(_control);
            if (top is Window window)
                return window.IsVisible && window.WindowState != WindowState.Minimized;
            return true;
        }

        protected virtual Visual MousePositionReferential => _control;

        public CefPoint PointToScreen(CefPoint point, float deviceScaleFactor)
        {
            var screenCoordinates = _control.PointToScreen(new Point(point.X, point.Y));

            var result = new CefPoint(0, 0);
            result.X = screenCoordinates.X;
            result.Y = screenCoordinates.Y;
            return result;
        }

        public override IntPtr? GetHostViewHandle(int initialWidth, int initialHeight)
        {
            var platformHandle = GetHostWindowPlatformHandle();
            if (platformHandle is IMacOSTopLevelPlatformHandle macOSHandle)
            {
                return macOSHandle.NSView;
            }
            return platformHandle?.Handle;
        }

        public override void SetTooltip(string text)
        {
            // TODO BUG: sometimes the tooltips are left hanging when the user moves the cursor over the tooltip but meanwhile
            // the tooltip is destroyed
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        ToolTip.SetIsOpen(_control, false);
                    }
                    else
                    {
                        ToolTip.SetTip(_control, text);
                        ToolTip.SetShowDelay(_control, 0);
                        ToolTip.SetIsOpen(_control, true);
                    }
                }, DispatcherPriority.Input);
        }

        public void Focus()
        {
            if (s_isTraceEnabled)
                Console.WriteLine("[Trace][AvaloniaOffScreenControlHost.Focus]");
            _control.Focus();
        }

        public async Task<CefDragOperationsMask> StartDrag(CefDragData dragData, CefDragOperationsMask allowedOps, int x, int y)
        {
            var lastPointerEvent = this._lastPointerEvent; // story a copy, since this might be other thread
            if (lastPointerEvent != null)
            {
                Bitmap draggedBitmap = null;
                try
                {
                    if (dragData.HasImage)
                    {
                        using var cefImage = dragData.GetImage();
                        if (cefImage != null && !cefImage.IsEmpty)
                        {
                            using var png = cefImage.GetAsPng(1f, true, out _, out _);
                            if (png != null)
                            {
                                using var stream = new MemoryStream(png.ToArray());
                                draggedBitmap = new Bitmap(stream);
                            }
                        }
                    }

                    var dataTransfer = new DataTransfer();
                    if (!string.IsNullOrEmpty(dragData.FragmentText))
                        dataTransfer.Add(DataTransferItem.CreateText(dragData.FragmentText));
                    if (draggedBitmap != null)
                        dataTransfer.Add(DataTransferItem.Create(DataFormat.Bitmap, draggedBitmap));

                    var result = await Dispatcher.UIThread.InvokeAsync(() => DragDrop.DoDragDropAsync(lastPointerEvent, dataTransfer, allowedOps.AsDragDropEffects()));
                    return result.AsCefDragOperationsMask();
                }
                finally
                {
                    draggedBitmap?.Dispose();
                    this._lastPointerEvent = null;
                    _previousCursor = null;
                    _currentDragCursor = null;
                }
            }
            return CefDragOperationsMask.None;
        }

        public void UpdateDragCursor(CefDragOperationsMask allowedOps)
        {
            // Called on a CEF thread; cursors are made on the UI thread.
            Dispatcher.UIThread.Post(() => _currentDragCursor = CursorsProvider.GetCursorFromCefType(allowedOps));
        }

        // Avalonia uses the cursor of the element under the pointer and does not inherit it, so the
        // page image that receives the pointer must carry it as well as the browser control.
        private void ApplyCursor(Cursor cursor)
        {
            _control.Cursor = cursor;
            if (_cursorTarget != null) _cursorTarget.Cursor = cursor;
        }

        private Control _cursorTarget;

        public override bool SetCursor(IntPtr cursorHandle, CefCursorType cursorType)
        {
            // Called on a CEF thread: look the cursor up on the UI thread.
            Dispatcher.UIThread.Post(() => ApplyCursor(CursorsProvider.GetCursorFromCefType(cursorType)));
            return true;
        }

        /// <summary>
        /// Create an image that is used to render the browser frame and popups
        /// </summary>
        /// <returns></returns>
        private static Image CreateImage()
        {
            return new Image()
            {
                Focusable = false,
                Stretch = Stretch.None,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            };
        }
    }
}
