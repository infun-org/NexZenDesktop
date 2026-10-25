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
            _host.NoteImeOutput();

            // Phase 5 — Null safety: ensure browser is initialised before calling CEF
            if (_control is not AvaloniaCefBrowser browser) return;
            if (!browser.IsBrowserInitialized) return;

            var host = browser.GetBrowserHost();
            if (host == null) return;

            // Some IMEs (fcitx5 Lotus in Preedit mode, numpad keys with NumLock on) keep digits
            // pending and only commit them on an unrelated key, and the pending text never shows
            // in the page. Digits are typed at once; the later commit drops what was typed.
            var typed = _host.TypedPreeditPrefix;
            if (typed.Length > 0 && !string.IsNullOrEmpty(text) && !text.StartsWith(typed, StringComparison.Ordinal))
                typed = _host.TypedPreeditPrefix = "";
            var pending = string.IsNullOrEmpty(text) ? "" : text.Substring(typed.Length);

            if (string.IsNullOrEmpty(text))
            {
                if (_host.CurrentImeState == ImeState.Composing)
                {
                    _host.CurrentImeState = ImeState.PendingCommit;
                    host.ImeCancelComposition();
                }
            }
            else if (pending.Length > 0 && IsTypedAtOnce(pending))
            {
                if (_host.CurrentImeState == ImeState.Composing)
                    host.ImeCancelComposition();
                AvaloniaOffScreenControlHost.SendDigitKeystrokes(host, pending);
                _host.TypedPreeditPrefix = text;
                // The commit that follows goes through the IME path (and drops the typed part).
                _host.CurrentImeState = ImeState.PendingCommit;
                host.Invalidate(CefPaintElementType.View);
            }
            else if (pending.Length > 0)
            {
                _host.CurrentImeState = ImeState.Composing;
                var underline = new CefCompositionUnderline
                {
                    Range = new CefRange(0, pending.Length),
                    Color = new CefColor(255, 0, 0, 0),       // Solid black
                    BackgroundColor = new CefColor(0, 0, 0, 0), // Transparent
                    Thick = false,
                    Style = CefCompositionUnderlineStyle.Solid
                };

                host.ImeSetComposition(
                    pending,
                    1,
                    underline,
                    new CefRange(int.MaxValue, int.MaxValue),
                    new CefRange(pending.Length, pending.Length)
                );
                // The results page does not schedule a frame for a composition
                // update, so the preedit stays invisible until a later key paints.
                host.Invalidate(CefPaintElementType.View);
            }
        }

        // Only plain digits: a Vietnamese syllable (letters, VNI tone digits after a letter) still composes.
        private static bool IsTypedAtOnce(string pending)
        {
            foreach (var c in pending)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            return true;
        }

        public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();
    }
}
