using System;
using System.Threading;
using System.Threading.Tasks;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.InternalHandlers;
using Xilium.CefGlue.Common.Platform;
using Xilium.CefGlue.Common.Shared.Helpers;

namespace Xilium.CefGlue.Common
{
    internal class CommonOffscreenBrowserAdapter : CommonBrowserAdapter, IOffscreenCefBrowserHost
    {
        private static readonly TimeSpan ResizeDelay = TimeSpan.FromMilliseconds(50);

        private bool _isVisible = true;

        private int _resizeGeneration;
        private int _settledGeneration;
        private int _settleActive;

        public CommonOffscreenBrowserAdapter(object eventsEmitter, string name, IOffScreenControlHost control, IOffScreenPopupHost popup, ILogger logger, CefRequestContext cefRequestContext = null) 
            : base(eventsEmitter, name, control, logger, cefRequestContext) {

            Popup = popup;
            
            this.LoadEnd += (s, e) =>
            {
                if (e.Frame.IsMain)
                    ForceRepaint();
            };
        }

        private void ForceRepaint()
        {
            BrowserHost?.Invalidate(CefPaintElementType.View);
        }

        protected new IOffScreenControlHost Control => (IOffScreenControlHost) base.Control;

        private IOffScreenPopupHost Popup { get; }

        protected override void InnerDispose()
        {
            Control.RenderSurface.Dispose();
            Popup.RenderSurface.Dispose();
        }

        private int Width => Control.RenderSurface.Width;
        private int Height => Control.RenderSurface.Height;

        private void SendMouseClickEvent(CefMouseEvent mouseEvent, CefMouseButtonType mouseButton, bool isMouseUp, int clickCount)
        {
            BrowserHost?.SendMouseClickEvent(mouseEvent, mouseButton, isMouseUp, clickCount);
        }

        private void HandleLostFocus()
        {
            WithErrorHandling(nameof(HandleLostFocus), () =>
            { 
                if (Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE") == "1")
                    Console.WriteLine("[Trace][CommonOffscreenBrowserAdapter] BrowserHost.SetFocus(false)");
                // BrowserHost?.SetFocus(false);
            });
        }

        protected override void HandleGotFocus()
        {
            WithErrorHandling(nameof(HandleGotFocus), () =>
            { 
                if (Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE") == "1")
                    Console.WriteLine("[Trace][CommonOffscreenBrowserAdapter] BrowserHost.SetFocus(true)");
                BrowserHost?.SetFocus(true);
            });
        }

        private void HandleMouseMove(CefMouseEvent mouseEvent)
        {
            WithErrorHandling(nameof(HandleMouseMove), () =>
            {
                BrowserHost?.SendMouseMoveEvent(mouseEvent, false);
            });
        }

        private void HandleMouseLeave(CefMouseEvent mouseEvent)
        {
            WithErrorHandling(nameof(HandleMouseLeave), () =>
            {
                BrowserHost?.SendMouseMoveEvent(mouseEvent, true);
            });
        }

        private void HandleMouseButtonDown(IOffScreenControlHost control, CefMouseEvent mouseEvent, CefMouseButtonType mouseButton, int clickCount)
        {
            WithErrorHandling(nameof(HandleMouseButtonDown), () =>
            {
                control.Focus();
                if (BrowserHost != null)
                {
                    BrowserHost.SetFocus(true);
                    Console.WriteLine($"[CEF-INPUT] HandleMouseButtonDown: button={mouseButton} pos=({mouseEvent.X},{mouseEvent.Y}) clickCount={clickCount}");
                    SendMouseClickEvent(mouseEvent, mouseButton, false, clickCount);
                }
            });
        }

        private void HandleMouseButtonUp(CefMouseEvent mouseEvent, CefMouseButtonType mouseButton, int clickCount)
        {
            WithErrorHandling(nameof(HandleMouseButtonUp), () =>
            {
                if (BrowserHost != null)
                {
                    Console.WriteLine($"[CEF-INPUT] HandleMouseButtonUp: button={mouseButton} pos=({mouseEvent.X},{mouseEvent.Y}) clickCount={clickCount}");
                    // CEF offscreen quirk: without at least one mousemove between mousedown
                    // and mouseup, CEF batches them in the same input frame and Blink may not
                    // generate a DOM 'click' event (causing links and JS onClick handlers to
                    // silently fail on a quick tap). Sending a mousemove at the exact same
                    // coordinates is a no-op visually but forces CEF to flush mousedown first.
                    if (mouseButton == CefMouseButtonType.Left)
                    {
                        Console.WriteLine($"[CEF-INPUT] Sending synthetic mousemove before mouseup at ({mouseEvent.X},{mouseEvent.Y})");
                        var syntheticMove = new CefMouseEvent(mouseEvent.X, mouseEvent.Y, mouseEvent.Modifiers | CefEventFlags.LeftMouseButton);
                        BrowserHost.SendMouseMoveEvent(syntheticMove, false);
                    }
                    SendMouseClickEvent(mouseEvent, mouseButton, true, clickCount);
                }
            });
        }

        private void HandleMouseWheel(CefMouseEvent mouseEvent, int deltaX, int deltaY)
        {
            WithErrorHandling(nameof(HandleMouseWheel), () =>
            {
                BrowserHost?.SendMouseWheelEvent(mouseEvent, deltaX, deltaY);
            });
        }

        private void HandleTextInput(string text, out bool handled)
        {
            var _handled = false;

            WithErrorHandling(nameof(HandleMouseWheel), () =>
            {
                if (BrowserHost != null)
                {
                    foreach (var c in text)
                    {
                        var keyEvent = new CefKeyEvent()
                        {
                            EventType = CefKeyEventType.Char,
                            WindowsKeyCode = CharKeyCode.WindowsKeyCode(c),
                            NativeKeyCode = LinuxNativeKey.FromCharacter(c),
                            Character = c,
                            UnmodifiedCharacter = c,
                            Modifiers = CharKeyCode.Modifiers(c)
                        };

                        if (Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE") == "1")
                            Console.WriteLine($"[Trace][CommonOffscreenBrowserAdapter] BrowserHost.SendKeyEvent(Char='{c}')");

                        BrowserHost?.SendKeyEvent(keyEvent);
                    }

                    _handled = true;
                }
            });

            handled = _handled;
        }

        private void HandleKeyPress(CefKeyEvent keyEvent, out bool handled)
        {
            WithErrorHandling(nameof(HandleKeyPress), () =>
            {
                if (BrowserHost != null)
                {
                    if (Environment.GetEnvironmentVariable("NEXZEN_IME_TRACE") == "1")
                        Console.WriteLine($"[Trace][CommonOffscreenBrowserAdapter] BrowserHost.SendKeyEvent({keyEvent.EventType}, WindowsKeyCode={keyEvent.WindowsKeyCode})");
                    //_logger.Debug(string.Format("KeyDown: system key {0}, key {1}", arg.SystemKey, arg.Key));
                    BrowserHost?.SendKeyEvent(keyEvent);
                    MaybeSendControlChar(keyEvent);
                }
            });
            handled = false;
        }

        private void MaybeSendControlChar(CefKeyEvent keyEvent)
        {
            if (keyEvent.EventType != CefKeyEventType.RawKeyDown)
                return;
            if ((keyEvent.Modifiers & (CefEventFlags.ControlDown | CefEventFlags.AltDown | CefEventFlags.CommandDown)) != 0)
                return;

            char ch;
            switch (keyEvent.WindowsKeyCode)
            {
                case 0x0D: // VK_RETURN
                    ch = '\r';
                    break;
                case 0x09: // VK_TAB
                    ch = '\t';
                    break;
                default:
                    return;
            }

            BrowserHost?.SendKeyEvent(new CefKeyEvent
            {
                EventType = CefKeyEventType.Char,
                WindowsKeyCode = ch,
                NativeKeyCode = keyEvent.NativeKeyCode,
                Character = ch,
                UnmodifiedCharacter = ch,
                Modifiers = keyEvent.Modifiers,
                FocusOnEditableField = true
            });
        }

        private void HandleDragEnter(CefMouseEvent mouseEvent, CefDragData dragData, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDragEnter), () =>
            {
                Console.WriteLine($"[CEF-DND] CEF enter effects={effects} files={dragData.GetFileNames()?.Length ?? 0} file={dragData.IsFile} fragment={dragData.IsFragment} image={dragData.HasImage} browserHost={BrowserHost != null}");
                BrowserHost?.DragTargetDragEnter(dragData, mouseEvent, effects);
                BrowserHost?.DragTargetDragOver(mouseEvent, effects);
            });
        }

        private void HandleDragOver(CefMouseEvent mouseEvent, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDragOver), () =>
            {
                BrowserHost?.DragTargetDragOver(mouseEvent, effects);
            });

            // TODO
            //e.Effects = currentDragDropEffects;
            //e.Handled = true;
        }

        private void HandleDragLeave()
        {
            WithErrorHandling(nameof(HandleDragLeave), () =>
            {
                Console.WriteLine("[CEF-DND] CEF leave");
                BrowserHost?.DragTargetDragLeave();
            });
        }

        private void HandleDrop(CefMouseEvent mouseEvent, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDrop), () =>
            {
                Console.WriteLine($"[CEF-DND] CEF drop effects={effects} browserHost={BrowserHost != null}");
                BrowserHost?.DragTargetDragOver(mouseEvent, effects);
                BrowserHost?.DragTargetDrop(mouseEvent);
            });
        }

        private void HandleVisibilityChanged(bool isVisible)
        {
            if (isVisible == _isVisible)
            {
                // visiblity didn't change at all
                return;
            }

            WithErrorHandling(nameof(HandleVisibilityChanged), () =>
            {
                _isVisible = isVisible;
                if (BrowserHost == null)
                    return;

                if (isVisible)
                {
                    BrowserHost.WasHidden(false);
                    ForceRepaint();
                }
                else
                {
                    BrowserHost.WasHidden(true);
                }
            });
        }

        private void HandleScreenInfoChanged(float deviceScaleFactor)
        {
            WithErrorHandling(nameof(HandleScreenInfoChanged), () =>
            {
                Control.RenderSurface.DeviceScaleFactor = deviceScaleFactor;
                Popup.RenderSurface.DeviceScaleFactor = deviceScaleFactor;

                ForceRepaint();
                // Might cause a crash due to a SurfaceSync check in chromium code.
                //
                // Fixed in chromium versions >= 79.0.3909.0 (https://chromium-review.googlesource.com/c/chromium/src/+/1792459)
                //
                //_browserHost?.NotifyScreenInfoChanged();
            });
        }

        protected override void HandleControlSizeChanged(CefSize size)
        {
            // A closed popup reports 0x0. Creating the browser then drops the only paint.
            if (size.Width < 1 || size.Height < 1)
                return;

            if (IsBrowserCreated)
                ResizeBrowser(size.Width, size.Height);
            else
                CreateBrowser(size.Width, size.Height);
        }

        private void AttachEventHandlers(IOffScreenControlHost control)
        {
            control.LostFocus += HandleLostFocus;
            control.GotFocus += HandleGotFocus;

            control.MouseMoved += HandleMouseMove;
            control.MouseLeave += HandleMouseLeave;
            control.MouseButtonPressed += HandleMouseButtonDown;
            control.MouseButtonReleased += HandleMouseButtonUp;
            control.MouseWheelChanged += HandleMouseWheel;

            control.KeyDown += HandleKeyPress;
            control.KeyUp += HandleKeyPress;

            control.TextInput += HandleTextInput;

            control.DragEnter += HandleDragEnter;
            control.DragOver += HandleDragOver;
            control.DragLeave += HandleDragLeave;
            control.Drop += HandleDrop;
        }

        protected override CommonCefClient CreateCefClient()
        {
            return new CommonCefClient(this, new CommonCefRenderHandler(this, _logger), _logger);
        }

        protected override void SetupBrowserView(CefWindowInfo windowInfo, int width, int height, IntPtr hostViewHandle)
        {
            AttachEventHandlers(Control);
            AttachEventHandlers(Popup);

            Control.ScreenInfoChanged += HandleScreenInfoChanged;
            Control.VisibilityChanged += HandleVisibilityChanged;

            Control.RenderSurface.Resize(width, height);

            // Find the window that's hosting us
            windowInfo.SetAsWindowless(hostViewHandle, Control.RenderSurface.AllowsTransparency);
        }

        protected override void OnBrowserHostCreated(CefBrowserHost browserHost)
        {
            if (Width > 0 && Height > 0)
                browserHost.WasResized();

            // Initialize with focus so dropdowns and inputs work on first click
            browserHost.SetFocus(true);

            if (!_isVisible)
            {
                browserHost.WasHidden(true);
                return;
            }

            if (Width > 0 && Height > 0)
                ForceRepaint();
        }

        protected void ResizeBrowser(int newWidth, int newHeight)
        {
            if (newWidth < 1 || newHeight < 1)
                return;

            if (Width == newWidth && Height == newHeight)
                return;

            Control.RenderSurface.Resize(newWidth, newHeight);
            Interlocked.Increment(ref _resizeGeneration);
            BrowserHost?.WasResized();
            _logger.Debug($"Browser resized {newWidth}x{newHeight}");
            StartResizeSettle();
        }

        private void StartResizeSettle()
        {
            if (Interlocked.CompareExchange(ref _settleActive, 1, 0) != 0)
                return;

            ActionTask.Run(async () =>
            {
                try
                {
                    while (BrowserHost != null)
                    {
                        var generation = Volatile.Read(ref _resizeGeneration);
                        await Task.Delay(ResizeDelay);
                        if (BrowserHost == null)
                            return;
                        if (generation != Volatile.Read(ref _resizeGeneration))
                            continue;

                        if (Width < 1 || Height < 1)
                        {
                            Volatile.Write(ref _settledGeneration, generation);
                            return;
                        }

                        BrowserHost.WasResized();
                        BrowserHost.Invalidate(CefPaintElementType.View);
                        Volatile.Write(ref _settledGeneration, generation);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Resize settle failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _settleActive, 0);
                    if (BrowserHost != null &&
                        Volatile.Read(ref _resizeGeneration) != Volatile.Read(ref _settledGeneration))
                    {
                        StartResizeSettle();
                    }
                }
            });
        }


        protected override bool OnBrowserClose(CefBrowser browser)
        {
            Cleanup(browser);
            // According to cef documentation:
            // If no OS window exists (window rendering disabled) returning false will cause the browser object to be destroyed immediately
            return false;
        }

        #region IOffscreenCefBrowserHost

        void IOffscreenCefBrowserHost.GetViewRect(out CefRectangle rect)
        {
            rect = GetViewRect();
        }

        private CefRectangle GetViewRect()
        {
            var result = new CefRectangle(0, 0, Width, Height);
            // The simulated screen and view rectangle are the same. This is necessary
            // for popup menus to be located and sized inside the view.
            if (result.Width <= 0 || result.Height <= 0)
            {
                // NOTE: width and height must be > 0, otherwise cef will blow up
                return new CefRectangle(0, 0, Math.Max(1, result.Width), Math.Max(1, result.Height));
            }
            return result;
        }

        void IOffscreenCefBrowserHost.GetScreenPoint(int viewX, int viewY, ref int screenX, ref int screenY)
        {
            GetScreenPoint(viewX, viewY, ref screenX, ref screenY);
        }

        private void GetScreenPoint(int viewX, int viewY, ref int screenX, ref int screenY)
        {
            // TODO
            //var point = new Point(0, 0);
            //WithErrorHandling(nameof(GetScreenPoint), () =>
            //{
            //    point = _control.PointToScreen(new Point(viewX, viewY), _controlRenderHandler.DeviceScaleFactor);
            //});
            //screenX = point.X;
            //screenY = point.Y;
        }

        void IOffscreenCefBrowserHost.GetScreenInfo(CefScreenInfo screenInfo)
        {
            screenInfo.DeviceScaleFactor = Control.RenderSurface.DeviceScaleFactor;
        }

        void IOffscreenCefBrowserHost.HandlePopupShow(bool show)
        {
            WithErrorHandling(nameof(IOffscreenCefBrowserHost.HandlePopupShow), () =>
            {
                Console.WriteLine($"[CEF-POPUP] HandlePopupShow: show={show}");
                if (show)
                {
                    Popup.Open();
                }
                else
                {
                    Popup.Close();
                }
            });
        }

        void IOffscreenCefBrowserHost.HandlePopupSizeChange(CefRectangle rect)
        {
            WithErrorHandling(nameof(IOffscreenCefBrowserHost.HandlePopupSizeChange), () =>
            {
                Popup.RenderSurface.Resize(rect.Width, rect.Height);
                Popup.MoveAndResize(rect.X, rect.Y, rect.Width, rect.Height);
            });
        }

        void IOffscreenCefBrowserHost.HandleViewPaint(IntPtr buffer, int width, int height, CefRectangle[] dirtyRects, bool isPopup)
        {
            OffScreenRenderSurface renderHandler;
            if (isPopup)
            {
                renderHandler = Popup.RenderSurface;
            }
            else
            {
                renderHandler = Control.RenderSurface;
            }

            const string ScopeName = nameof(IOffscreenCefBrowserHost.HandleViewPaint);

            WithErrorHandling(ScopeName, () =>
            {
                renderHandler?.Render(buffer, width, height, dirtyRects)
                              .ContinueWith(t => HandleException(ScopeName, t.Exception), TaskContinuationOptions.OnlyOnFaulted);
            });
        }

        void IOffscreenCefBrowserHost.HandleImeCompositionRangeChanged(CefBrowser browser, CefRange selectedRange, CefRectangle[] characterBounds)
        {
            WithErrorHandling(nameof(IOffscreenCefBrowserHost.HandleImeCompositionRangeChanged), () =>
            {
                Control.HandleImeCompositionRangeChanged(selectedRange, characterBounds);
            });
        }

        void IOffscreenCefBrowserHost.HandleStartDragging(CefBrowser browser, CefDragData dragData, CefDragOperationsMask allowedOps, int x, int y)
        {
            WithErrorHandling(nameof(IOffscreenCefBrowserHost.HandleStartDragging), async () =>
            {
                var result = await Control.StartDrag(dragData, allowedOps, x, y);
                BrowserHost.DragSourceEndedAt(x, y, result);
                BrowserHost.DragSourceSystemDragEnded();
            });
        }

        void IOffscreenCefBrowserHost.HandleUpdateDragCursor(CefBrowser browser, CefDragOperationsMask operation)
        {
            Control.UpdateDragCursor(operation);
        }

        #endregion
    }
}
