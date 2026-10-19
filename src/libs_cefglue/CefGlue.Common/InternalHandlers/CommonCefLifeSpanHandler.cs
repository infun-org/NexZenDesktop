using System;

namespace Xilium.CefGlue.Common.InternalHandlers
{
    internal sealed class CommonCefLifeSpanHandler : CefLifeSpanHandler
    {
        private readonly ICefBrowserHost _owner;

        public CommonCefLifeSpanHandler(ICefBrowserHost owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException("owner");
            }

            _owner = owner;
        }

        // DevTools opened from this browser share its CefClient, so their life span events
        // arrive here too. Only the first browser created is the page; events of the others
        // must not reach the page or the app, or closing DevTools tears the page down.
        private int? _ownBrowserId;

        private bool IsOwn(CefBrowser browser)
            => _ownBrowserId == null || _ownBrowserId == browser.Identifier;

        protected override void OnAfterCreated(CefBrowser browser)
        {
            _ownBrowserId ??= browser.Identifier;
            if (!IsOwn(browser))
                return;
            _owner.LifeSpanHandler?.HandleAfterCreated(browser);
            _owner.HandleBrowserCreated(browser);
        }

        protected override bool DoClose(CefBrowser browser)
        {
            if (!IsOwn(browser))
                return false;
            return (_owner.LifeSpanHandler?.HandleDoClose(browser) ?? false) || 
                _owner.HandleBrowserClose(browser);
        }

        protected override void OnBeforeClose(CefBrowser browser)
        {
            if (!IsOwn(browser))
                return;
            _owner.LifeSpanHandler?.HandleBeforeClose(browser);
            _owner.HandleBrowserDestroyed(browser);
        }


        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName, CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            return (_owner.LifeSpanHandler?.HandleBeforePopup(browser, frame, targetUrl, targetFrameName, targetDisposition, userGesture, popupFeatures, windowInfo, ref client, settings, ref extraInfo, ref noJavascriptAccess) ?? false) ||
                base.OnBeforePopup(browser, frame, targetUrl, targetFrameName, targetDisposition, userGesture, popupFeatures, windowInfo, ref client, settings, ref extraInfo, ref noJavascriptAccess);
        }

        protected override void OnBeforeDevToolsPopup(CefBrowser browser, CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool useDefaultWindow)
        {
            _owner.LifeSpanHandler?.HandleBeforeDevToolsPopup(browser, windowInfo, ref client, settings, ref extraInfo, ref useDefaultWindow);
            base.OnBeforeDevToolsPopup(browser, windowInfo, ref client, settings, ref extraInfo, ref useDefaultWindow);
        }
    }
}
