using System;
using System.Text.Json;

namespace NexZen.Desktop.Handlers;

public sealed record PasswordCapture(string Origin, string Username, string Password);
public sealed record PasswordAutofillRequest(string Origin);
public sealed record PasswordCaptureDiagnostic(string Stage, string Origin, string Host, int FormCount, int PasswordFields, bool PasswordHasValue, int DocumentPasswordFields, bool DocumentPasswordHasValue, int UsernameFields, string[] InputTypes);
public sealed record PasswordCapturePollResult(PasswordCapture[] Captures, PasswordCaptureDiagnostic? Diagnostic, PasswordAutofillRequest[]? AutofillRequests = null);

public sealed class PasswordCaptureBridge
{
    public const string DrainQueueScript = """
        const queue = (window.__nexZenPasswordCaptureQueue || []).splice(0);
        let diagnostic = null;
        try {
          const saved = JSON.parse(sessionStorage.getItem('__nexZenPasswordCaptureQueueV1') || '[]');
          sessionStorage.removeItem('__nexZenPasswordCaptureQueueV1');
          const signature = payload => `${payload.origin}\n${payload.username}\n${payload.password}`;
          const seen = new Set(queue.map(signature));
          for (const item of saved) {
            if (item && Date.now() - item.capturedAt < 120000 && item.payload && !seen.has(signature(item.payload))) {
              queue.push(item.payload);
              seen.add(signature(item.payload));
            }
          }
          diagnostic = JSON.parse(sessionStorage.getItem('__nexZenPasswordCaptureDiagnosticV1') || 'null');
          sessionStorage.removeItem('__nexZenPasswordCaptureDiagnosticV1');
        } catch (_) { }
        const autofillRequests = (window.__nexZenPasswordAutofillQueue || []).splice(0);
        return JSON.stringify({ captures: queue, diagnostic, autofillRequests });
        """;

    public static bool IsSameOrigin(string currentUrl, string submittedOrigin)
    {
        return Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            && Uri.TryCreate(submittedOrigin, UriKind.Absolute, out var submitted)
            && current.Scheme is "http" or "https"
            && string.Equals(current.GetLeftPart(UriPartial.Authority),
                submitted.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    public const string ObserverScript = """
        (() => {
          if (window.__nexZenPasswordObserverVersion === 9) return;
          window.__nexZenPasswordObserverVersion = 9;
          let lastCapture = '';
          let lastCaptureAt = 0;
          const passwordSelector = 'input[type="password"], input[autocomplete="current-password"], input[autocomplete="new-password"], input[name*="pass" i], input[id*="pass" i]';
          if (window === window.top) window.__nexZenPasswordCaptureQueue ||= [];
          if (window === window.top) window.__nexZenPasswordAutofillQueue ||= [];
          const report = stage => console.info(`[PasswordCapture] ${stage}`);
          const diagnose = (stage, form = null) => {
            try {
              const passwordInputs = form ? [...form.querySelectorAll(passwordSelector)] : [...document.querySelectorAll(passwordSelector)];
              const documentPasswordInputs = [...document.querySelectorAll(passwordSelector)];
              const usernameInputs = form ? [...form.querySelectorAll('input')].filter(input => !input.matches(passwordSelector) && (input.type === 'text' || input.type === 'email' || input.autocomplete === 'username'))
                : [...document.querySelectorAll('input')].filter(input => !input.matches(passwordSelector) && (input.type === 'text' || input.type === 'email' || input.autocomplete === 'username'));
              const diagnostic = {
                stage, origin: location.origin, host: location.host, formCount: document.forms.length,
                passwordFields: passwordInputs.length, passwordHasValue: passwordInputs.some(input => !!input.value),
                documentPasswordFields: documentPasswordInputs.length,
                documentPasswordHasValue: documentPasswordInputs.some(input => !!input.value),
                usernameFields: usernameInputs.length,
                inputTypes: [...document.querySelectorAll('input')].slice(0, 20)
                  .map(input => `${input.type}:${input.autocomplete || ''}`)
              };
              sessionStorage.setItem('__nexZenPasswordCaptureDiagnosticV1', JSON.stringify(diagnostic));
              if (window !== window.top)
                window.top.postMessage({ __nexZenPasswordDiagnostic: diagnostic }, '*');
            } catch (_) { }
          };
          diagnose('observer-installed');
          const send = payload => {
            const enqueue = item => {
              window.__nexZenPasswordCaptureQueue ||= [];
              window.__nexZenPasswordCaptureQueue.push(item);
              try {
                const saved = JSON.parse(sessionStorage.getItem('__nexZenPasswordCaptureQueueV1') || '[]');
                saved.push({ capturedAt: Date.now(), payload: item });
                sessionStorage.setItem('__nexZenPasswordCaptureQueueV1', JSON.stringify(saved));
              } catch (_) { }
            };
            if (window === window.top) {
              enqueue(payload);
              return;
            }
            window.top.postMessage({ __nexZenPasswordCapture: payload }, '*');
          };
          if (window === window.top) {
            window.addEventListener('message', event => {
              const diagnostic = event.data && event.data.__nexZenPasswordDiagnostic;
              if (diagnostic && event.origin === diagnostic.origin) {
                try { sessionStorage.setItem('__nexZenPasswordCaptureDiagnosticV1', JSON.stringify(diagnostic)); } catch (_) { }
              }
              const payload = event.data && event.data.__nexZenPasswordCapture;
              if (payload && event.origin === payload.origin) {
                window.__nexZenPasswordCaptureQueue ||= [];
                window.__nexZenPasswordCaptureQueue.push(payload);
                try {
                  const saved = JSON.parse(sessionStorage.getItem('__nexZenPasswordCaptureQueueV1') || '[]');
                  saved.push({ capturedAt: Date.now(), payload });
                  sessionStorage.setItem('__nexZenPasswordCaptureQueueV1', JSON.stringify(saved));
                } catch (_) { }
              }
              const autofillRequest = event.data && event.data.__nexZenPasswordAutofillRequest;
              if (autofillRequest && event.origin === autofillRequest.origin) {
                window.__nexZenPasswordAutofillQueue ||= [];
                window.__nexZenPasswordAutofillQueue.push(autofillRequest);
              }
            });
          }
          let lastAutofillRequestAt = 0;
          const autofillPasswordSelector = 'input[type="password"], input[autocomplete="current-password"]';
          const requestAutofill = () => {
            const now = Date.now();
            if (now - lastAutofillRequestAt < 1800) return;
            const passwordInput = [...document.querySelectorAll(autofillPasswordSelector)]
              .find(input => !input.disabled && input.getClientRects().length);
            if (!passwordInput || passwordInput.autocomplete === 'new-password') return;
            lastAutofillRequestAt = now;
            const request = { origin: location.origin };
            if (window === window.top) {
              window.__nexZenPasswordAutofillQueue ||= [];
              window.__nexZenPasswordAutofillQueue.push(request);
            } else {
              window.top.postMessage({ __nexZenPasswordAutofillRequest: request }, '*');
            }
          };
          document.addEventListener('focusin', event => {
            if (event.target instanceof Element && event.target.matches(autofillPasswordSelector)
                && event.target.autocomplete !== 'new-password')
              requestAutofill();
          }, true);
          const capture = form => {
            const targetForm = form instanceof HTMLFormElement ? form : null;
            const formPasswordInputs = targetForm ? [...targetForm.querySelectorAll(passwordSelector)] : [];
            const pagePasswordInputs = [...document.querySelectorAll(passwordSelector)];
            const passwordInput = [...formPasswordInputs, ...pagePasswordInputs]
              .find(input => input.value && !input.disabled && input.getClientRects().length)
              || [...formPasswordInputs, ...pagePasswordInputs]
                .find(input => input.value && !input.disabled);
            if (!passwordInput) {
              diagnose('password-input-not-filled-or-visible', targetForm);
              return;
            }
            diagnose('password-found', targetForm);
            report('password-form-detected');
            const formFields = targetForm ? [...targetForm.querySelectorAll('input')] : [];
            const pageFields = [...document.querySelectorAll('input')];
            const fields = [...new Set([...formFields, ...pageFields])]
              .filter(input => !input.disabled && !input.matches(passwordSelector) && input.type !== 'hidden'
                && input.type !== 'submit' && input.type !== 'button' && input.type !== 'checkbox'
                && input.type !== 'radio' && input.value
                && !/pass(word)?/i.test(input.name + ' ' + input.id + ' ' + input.autocomplete));
            const userInput = fields.find(input => input.autocomplete === 'username'
              || /user|login|account/i.test(input.name + ' ' + input.id))
              || fields.find(input => input.type === 'email')
              || fields.find(input => input.type === 'text')
              || fields.find(input => input.type === 'email' || input.type === 'text');
            if (!userInput) {
              diagnose('username-not-found', targetForm);
              report('username-not-found');
              return;
            }
            const signature = userInput.value + '\\n' + passwordInput.value;
            const now = Date.now();
            if (signature === lastCapture && now - lastCaptureAt < 2000) return;
            lastCapture = signature;
            lastCaptureAt = now;
            diagnose('credentials-captured', targetForm);
            report('credentials-captured');
            send({
              origin: location.origin,
              username: userInput.value,
              password: passwordInput.value
            });
          };
          window.addEventListener('submit', event => capture(event.target), true);
          document.addEventListener('submit', event => capture(event.target), true);
          document.addEventListener('click', event => {
            const button = event.target instanceof Element
              ? event.target.closest('button, input[type="submit"], input[type="image"], [role="button"]') : null;
            if (!button) return;
            const form = button.form || button.closest('form');
            capture(form);
            if (form) setTimeout(() => capture(form), 0);
          }, true);
          document.addEventListener('keydown', event => {
            if (event.key !== 'Enter' || !(event.target instanceof Element)) return;
            const form = event.target.closest('form');
            capture(form);
            if (form) setTimeout(() => capture(form), 0);
          }, true);
          for (const methodName of ['submit', 'requestSubmit']) {
            const nativeMethod = HTMLFormElement.prototype[methodName];
            if (typeof nativeMethod !== 'function') continue;
            HTMLFormElement.prototype[methodName] = function(...args) {
              capture(this);
              return nativeMethod.apply(this, args);
            };
          }
        })();
        """;

    public static string CreateFillScript(string username, string password)
    {
        var serializedUsername = JsonSerializer.Serialize(username);
        var serializedPassword = JsonSerializer.Serialize(password);
        return """
            (() => {
              const username = __NEXZEN_USERNAME__;
              const password = __NEXZEN_PASSWORD__;
              const visible = input => !input.disabled && input.getClientRects().length > 0;
              const passwordSelector = 'input[type="password"], input[autocomplete="current-password"]';
              const passwordInput = [...document.querySelectorAll(passwordSelector)].find(visible);
              if (!passwordInput) return false;
              const form = passwordInput.form || passwordInput.closest('form');
              const fields = [...(form || document).querySelectorAll('input')].filter(input => visible(input)
                && !input.matches(passwordSelector) && input.type !== 'hidden' && input.type !== 'submit'
                && input.type !== 'button' && input.type !== 'checkbox' && input.type !== 'radio');
              const usernameInput = fields.find(input => input.autocomplete === 'username')
                || fields.find(input => /user|login|account|email/i.test(input.name + ' ' + input.id + ' ' + input.autocomplete))
                || fields.find(input => input.type === 'email')
                || fields.find(input => input.type === 'text');
              const setValue = (input, value) => {
                const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                setter.call(input, value);
                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
              };
              if (usernameInput) setValue(usernameInput, username);
              setValue(passwordInput, password);
              return true;
            })();
            """.Replace("__NEXZEN_USERNAME__", serializedUsername, StringComparison.Ordinal)
            .Replace("__NEXZEN_PASSWORD__", serializedPassword, StringComparison.Ordinal);
    }

    private readonly Action<PasswordCapture> _onCredentialsSubmitted;

    public PasswordCaptureBridge(Action<PasswordCapture> onCredentialsSubmitted) =>
        _onCredentialsSubmitted = onCredentialsSubmitted;

    public void ReportDiagnostic(string stage, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;

        if (stage is "observer-installed" or "bridge-ready" or "password-form-detected"
            or "username-not-found" or "credentials-captured")
            Program.StartupLog.Write($"[PasswordCapture] {stage} host={uri.Host}");
    }

    // Called by the page-side submit observer through CEF's Javascript object bridge.
    public void SaveCredentials(string payload)
    {
        try
        {
            var capture = JsonSerializer.Deserialize<PasswordCapture>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (capture != null
                && Uri.TryCreate(capture.Origin, UriKind.Absolute, out var origin)
                && origin.Scheme is "http" or "https"
                && !string.IsNullOrWhiteSpace(capture.Username)
                && !string.IsNullOrEmpty(capture.Password))
            {
                Program.StartupLog.Write($"[PasswordCapture] native callback host={origin.Host}");
                _onCredentialsSubmitted(capture);
            }
            else
            {
                Program.StartupLog.Write("[PasswordCapture] native callback rejected invalid payload");
            }
        }
        catch (JsonException)
        {
            Program.StartupLog.Write("[PasswordCapture] native callback received invalid JSON");
            // Ignore malformed or page-generated bridge calls.
        }
    }
}
