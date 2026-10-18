using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.TextInput;
using System;

namespace Xilium.CefGlue.Avalonia.Platform
{
    internal class CefTextInputMethodClient : TextInputMethodClient
    {
        private readonly Control _control;
        private readonly AvaloniaOffScreenControlHost _host;

        public CefTextInputMethodClient(Control control, AvaloniaOffScreenControlHost host)
        {
            _control = control;
            _host = host;
        }

        public override Visual TextViewVisual => _control;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => _host.CursorRect;
        public override TextSelection Selection { get; set; }

        public override void SetPreeditText(string text)
        {
            // Phase 5 — Kill-switch: disable entire IME bridge via env var
            if (ImeConfig.IsDisabled) return;

            if (ImeConfig.IsTraceEnabled)
                Console.WriteLine($"[Trace][CefTextInputMethodClient.SetPreeditText] text='{text}'");

            // Phase 5 — Null safety: ensure browser is initialised before calling CEF
            if (_control is not AvaloniaCefBrowser browser) return;
            if (!browser.IsBrowserInitialized) return;

            var host = browser.GetBrowserHost();
            if (host == null) return;

            if (string.IsNullOrEmpty(text))
            {
                if (_host.CurrentImeState == ImeState.Composing)
                {
                    _host.CurrentImeState = ImeState.PendingCommit;
                    host.ImeCancelComposition();
                }
            }
            else
            {
                _host.CurrentImeState = ImeState.Composing;
                var underline = new CefCompositionUnderline
                {
                    Range = new CefRange(0, text.Length),
                    Color = new CefColor(255, 0, 0, 0),       // Solid black
                    BackgroundColor = new CefColor(0, 0, 0, 0), // Transparent
                    Thick = false,
                    Style = CefCompositionUnderlineStyle.Solid
                };

                host.ImeSetComposition(
                    text,
                    1,
                    underline,
                    new CefRange(int.MaxValue, int.MaxValue),
                    new CefRange(text.Length, text.Length)
                );
                // The results page does not schedule a frame for a composition
                // update, so the preedit stays invisible until a later key paints.
                host.Invalidate(CefPaintElementType.View);
            }
        }

        public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();
    }
}
