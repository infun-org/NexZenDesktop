using System;
using Avalonia.Threading;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers
{
    public class CustomLifeSpanHandler : LifeSpanHandler
    {
        private readonly Action<string, string> _onPopupRequest;
        private readonly Action? _onBeforeClose;

        public CustomLifeSpanHandler(Action<string, string> onPopupRequest, Action? onBeforeClose = null)
        {
            _onPopupRequest = onPopupRequest;
            _onBeforeClose = onBeforeClose;
        }

        protected override void OnBeforeClose(CefBrowser browser)
        {
            _onBeforeClose?.Invoke();
            base.OnBeforeClose(browser);
        }

        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            // Only intercept actual new-window/tab requests. 
            // Letting CurrentTab and SaveToDisk pass through allows sites like Pinterest
            // to do in-tab navigation triggered via JavaScript.
            bool isNewWindow = targetDisposition == CefWindowOpenDisposition.NewForegroundTab
                            || targetDisposition == CefWindowOpenDisposition.NewBackgroundTab
                            || targetDisposition == CefWindowOpenDisposition.NewPopup
                            || targetDisposition == CefWindowOpenDisposition.NewWindow;

            if (!isNewWindow)
                return false; // Let CEF handle it normally (in-tab navigation)

            var sourceUrl = browser.GetMainFrame()?.Url ?? "";
            if (!string.IsNullOrEmpty(targetUrl))
            {
                Dispatcher.UIThread.Post(() => _onPopupRequest(sourceUrl, targetUrl));
            }
            return true; // Cancel popup creation — we handled it above
        }
    }
}
