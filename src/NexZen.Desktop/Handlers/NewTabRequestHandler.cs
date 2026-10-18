using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using NexZen.Desktop.Services;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers
{
    public class NewTabRequestHandler : RequestHandler
    {
        private readonly Action<string> _openInNewTab;
        private readonly Action<string>? _installFromStore;
        private readonly DomainLockStore? _domainLocks;
        private readonly DomainLockBrowserSession _domainSession;
        private readonly Action<string, bool>? _domainLockChangedCallback;
        private WeakReference<CefFrame>? _lastMainFrame;

        public NewTabRequestHandler(Action<string> openInNewTab, Action<string>? installFromStore,
            DomainLockStore? domainLocks = null, DomainLockBrowserSession? domainSession = null)
        {
            _openInNewTab = openInNewTab;
            _installFromStore = installFromStore;
            _domainLocks = domainLocks;
            _domainSession = domainSession ?? new DomainLockBrowserSession();
            if (_domainLocks != null)
            {
                _domainLockChangedCallback = OnDomainLockChanged;
                _domainLocks.RegisterLockSubscriber(_domainLockChangedCallback);
            }
        }

        protected override bool OnBeforeBrowse(CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
        {
            if (frame.IsMain)
                _lastMainFrame = new WeakReference<CefFrame>(frame);
            if (IsDomainUnlockRequest(request.Url))
            {
                HandleDomainUnlock(frame, request);
                return true;
            }

            if (_domainLocks != null
                && _domainLocks.TryGetLockedRoot(request.Url, out var lockedRoot)
                && !_domainSession.IsUnlocked(lockedRoot))
            {
                var challengePage = DomainLockPage.CreateDataUrl(request.Url, lockedRoot);
                _domainSession.SetChallenge(challengePage, request.Url, lockedRoot);
                Program.StartupLog.Write($"[DomainLock] access blocked host={lockedRoot}");
                frame.LoadUrl(challengePage);
                return true;
            }

            // Facebook's mobile site can redirect photo/profile links to an Android
            // app link. CEF cannot open the intent: scheme, so use the web fallback
            // embedded in the URL when one is provided.
            if (frame.IsMain && TryGetIntentFallback(request.Url, out var fallbackUrl))
            {
                frame.LoadUrl(fallbackUrl);
                return true;
            }

            // Never pass an Android intent URL through to CEF, even when it has no
            // usable fallback. This avoids replacing the page with an unknown-scheme
            // error.
            if (request.Url.StartsWith("intent://", StringComparison.OrdinalIgnoreCase))
                return true;

            if (_installFromStore != null && WebStoreInstaller.TryParseInstallUrl(request.Url, out var id))
            {
                var extensionId = id;
                Dispatcher.UIThread.Post(() => _installFromStore(extensionId));
                return true;
            }

            return false;
        }

        private void OnDomainLockChanged(string rootDomain, bool isLocked)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_lastMainFrame?.TryGetTarget(out var frame) != true || frame == null)
                    return;
                try
                {
                    var address = frame.Url ?? string.Empty;
                    var isChallenge = _domainSession.TryGetChallenge(address, out var target);
                    if (isChallenge)
                        address = target;
                    if (!DomainLockStore.TryGetRootDomain(address, out var currentRoot)
                        || !string.Equals(currentRoot, rootDomain, StringComparison.OrdinalIgnoreCase))
                        return;

                    if (isLocked)
                    {
                        if (_domainSession.IsUnlocked(rootDomain) || _domainLocks?.IsLocked(address) != true)
                            return;
                        var pageUrl = DomainLockPage.CreateDataUrl(address, rootDomain);
                        _domainSession.SetChallenge(pageUrl, address, rootDomain);
                        frame.LoadUrl(pageUrl);
                    }
                    else if (isChallenge)
                    {
                        _domainSession.ClearChallenge(rootDomain);
                        frame.LoadUrl(address);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // A browser can be disposed while the lock notification is queued.
                }
            });
        }

        private void HandleDomainUnlock(CefFrame frame, CefRequest request)
        {
            if (_domainLocks == null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
                return;

            var formData = string.Concat((request.PostData?.GetElements() ?? Array.Empty<CefPostDataElement>())
                .Where(element => element.ElementType == CefPostDataElementType.Bytes)
                .Select(element => Encoding.UTF8.GetString(element.GetBytes())));
            Program.StartupLog.Write($"[DomainLock] unlock request intercepted method={request.Method} queryBytes={uri.Query.Length} formBytes={formData.Length}");
            var values = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (uri.Query + "&" + formData).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2)
                    values[WebUtility.UrlDecode(pair[0])] = WebUtility.UrlDecode(pair[1]);
            }
            var pin = string.Concat(Enumerable.Range(1, 6)
                .Select(index => values.TryGetValue($"pin{index}", out var digit) ? digit : string.Empty));
            values.TryGetValue("domain", out var domain);
            values.TryGetValue("target", out var target);
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(target))
            {
                Program.StartupLog.Write("[DomainLock] unlock request rejected because domain or target was missing");
                return;
            }

            var challengeExists = !string.IsNullOrEmpty(domain)
                && !string.IsNullOrEmpty(target)
                && _domainSession.HasChallenge(target, domain);
            var pinIsValid = pin.Length == 6 && pin.All(character => character is >= '0' and <= '9');
            var targetMatchesDomain = !string.IsNullOrEmpty(target)
                && DomainLockStore.TryGetRootDomain(target, out var targetRoot)
                && string.Equals(domain, targetRoot, StringComparison.OrdinalIgnoreCase);
            var targetIsLocked = !string.IsNullOrEmpty(target)
                && _domainLocks.TryGetLockedRoot(target, out var currentlyLockedRoot)
                && string.Equals(currentlyLockedRoot, domain, StringComparison.OrdinalIgnoreCase);
            if (!challengeExists || !pinIsValid || !targetMatchesDomain || !targetIsLocked)
            {
                Program.StartupLog.Write($"[DomainLock] unlock request rejected challenge={challengeExists} pinFormat={pinIsValid} domainMatch={targetMatchesDomain} stillLocked={targetIsLocked}");
                return;
            }

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var authorized = await _domainLocks.VerifyPinAsync(pin);
                    Program.StartupLog.Write($"[DomainLock] PIN verification completed valid={authorized}");
                    if (authorized)
                    {
                        _domainSession.Unlock(domain);
                        _domainSession.ClearChallenge(domain);
                        frame.LoadUrl(target);
                    }
                    else
                    {
                        var challengePage = DomainLockPage.CreateDataUrl(target, domain, invalidPin: true);
                        _domainSession.SetChallenge(challengePage, target, domain);
                        frame.LoadUrl(challengePage);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or ObjectDisposedException)
                {
                    Program.StartupLog.Write($"[DomainLock] PIN verification failed type={ex.GetType().Name}");
                    try
                    {
                        var challengePage = DomainLockPage.CreateDataUrl(target, domain, invalidPin: true);
                        _domainSession.SetChallenge(challengePage, target, domain);
                        frame.LoadUrl(challengePage);
                    }
                    catch (ObjectDisposedException) { }
                }
            });
        }

        private static bool IsDomainUnlockRequest(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, "nexzen-domain-lock.invalid", StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.AbsolutePath, "/unlock", StringComparison.OrdinalIgnoreCase);

        private static bool TryGetIntentFallback(string url, out string fallbackUrl)
        {
            fallbackUrl = string.Empty;
            if (!url.StartsWith("intent://", StringComparison.OrdinalIgnoreCase))
                return false;

            const string key = "S.browser_fallback_url=";
            int intentMarker = url.IndexOf("#Intent;", StringComparison.OrdinalIgnoreCase);
            if (intentMarker < 0)
                return false;

            int searchFrom = intentMarker + "#Intent;".Length;
            int keyIndex = url.IndexOf(key, searchFrom, StringComparison.Ordinal);
            if (keyIndex < 0)
                return false;

            int valueStart = keyIndex + key.Length;
            int valueEnd = url.IndexOf(';', valueStart);
            if (valueEnd < 0)
                valueEnd = url.Length;

            try
            {
                var decoded = WebUtility.UrlDecode(url[valueStart..valueEnd]);
                if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return false;

                fallbackUrl = uri.AbsoluteUri;
                return true;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        protected override bool OnOpenUrlFromTab(CefBrowser browser, CefFrame frame, string targetUrl, CefWindowOpenDisposition targetDisposition, bool userGesture)
        {
            if (targetDisposition != CefWindowOpenDisposition.NewForegroundTab
                && targetDisposition != CefWindowOpenDisposition.NewBackgroundTab)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetUrl) || targetUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return true;

            var url = targetUrl;
            Dispatcher.UIThread.Post(() => _openInNewTab(url));
            return true;
        }
    }
}
