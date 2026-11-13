using System;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Application.Services;
using Avalonia.Threading;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers
{
    public class CustomLifeSpanHandler : LifeSpanHandler
    {
        private readonly Action<string, string> _onPopupRequest;
        private readonly Action? _onBeforeClose;

        private readonly Action<string, string>? _onPopupBlocked;

        public CustomLifeSpanHandler(Action<string, string> onPopupRequest, Action? onBeforeClose = null,
            Action<string, string>? onPopupBlocked = null)
        {
            _onPopupRequest = onPopupRequest;
            _onBeforeClose = onBeforeClose;
            _onPopupBlocked = onPopupBlocked;
        }

        private static bool PopupsAllowed(string sourceUrl)
        {
            var store = App.ServiceProvider?.GetService<SitePermissionStore>();
            return store?.Decide(SiteDomains.SiteOf(sourceUrl), SitePermissionKeys.Popups) == true;
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
            // Site privacy "Pop-ups and redirects": like Chrome, pop-ups the user did not click
            // for are blocked unless the site is allowed to open them.
            if (!userGesture && !PopupsAllowed(sourceUrl))
            {
                Console.WriteLine($"[Popup] blocked (no user gesture) from {SiteDomains.SiteOf(sourceUrl)} to {targetUrl}");
                if (_onPopupBlocked != null && !string.IsNullOrEmpty(targetUrl))
                    Dispatcher.UIThread.Post(() => _onPopupBlocked(sourceUrl, targetUrl));
                return true;
            }
            // window.open(url, name, "width=...,height=...") — sign-in pop-ups (Google, Apple,
            // Microsoft…) send their result back through window.opener and then close. Opened
            // as a tab, the pop-up has no opener: sign-in ends on a blank page and the site never
            // hears about it. So real pop-ups get CEF's own window, which keeps the opener.
            if (targetDisposition == CefWindowOpenDisposition.NewPopup && ConfigureNativePopup(windowInfo, popupFeatures))
            {
                Console.WriteLine($"[Popup] native pop-up window (keeps opener) from {SiteDomains.SiteOf(sourceUrl)} to {targetUrl}");
                return false;
            }
            if (!string.IsNullOrEmpty(targetUrl))
            {
                Dispatcher.UIThread.Post(() => _onPopupRequest(sourceUrl, targetUrl));
            }
            return true; // Cancel popup creation — we handled it above
        }

        /// <summary>
        /// Makes the pop-up a normal (windowed) CEF window at the size and place the page asked
        /// for, instead of inheriting the opener's windowless (off-screen) mode.
        /// </summary>
        private static bool ConfigureNativePopup(CefWindowInfo windowInfo, CefPopupFeatures features)
        {
            try
            {
                var width = Math.Clamp(features.Width ?? 520, 320, 1600);
                var height = Math.Clamp(features.Height ?? 680, 320, 1200);
                var x = features.X ?? 160;
                var y = features.Y ?? 120;
                windowInfo.WindowlessRenderingEnabled = false;
                windowInfo.ParentHandle = IntPtr.Zero;
                windowInfo.Bounds = new CefRectangle(x, y, width, height);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Popup] native pop-up not possible, opening as a tab: {ex.Message}");
                return false;
            }
        }
    }
}
