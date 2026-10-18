using System;
using System.Text;

namespace NexZen.Desktop.Services;

internal static class DomainLockPage
{
    public static string CreateDataUrl(string targetUrl, string rootDomain, bool invalidPin = false)
    {
        var safeDomain = System.Net.WebUtility.HtmlEncode(rootDomain);
        var safeMessage = invalidPin ? "Incorrect PIN. Try again." : "Enter your NexZen PIN to continue.";
        var html = """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
              <title>Domain locked</title>
              <style>
                *{box-sizing:border-box}body{margin:0;min-height:100vh;padding:12px;display:grid;place-items:center;background:#e9ecef;color:#333;font:14px Arial,sans-serif}
                .card{width:min(440px,100%);min-height:min(386px,calc(100vh - 24px));padding:10px;background:#f3f3f3;border:1px solid #ccc;border-radius:8px;box-shadow:0 4px 30px #0004;display:flex;flex-direction:column}
                h1{margin:0 0 8px;font-size:16px;font-weight:700;color:#333}.domain{margin:0 0 12px;color:#4e4e4e;font-size:14px;font-weight:600;overflow-wrap:anywhere}
                p{margin:0 0 4px;color:#4e4e4e;line-height:1.4}.pin-label{display:block;margin-bottom:6px;color:#4e4e4e;font-size:13px}
                form{display:flex;flex:1;flex-direction:column;margin-top:10px}.digits{display:flex;width:100%;gap:6px}.digits input{width:48px;min-width:0;max-width:48px;height:48px;flex:1 1 0;border:1px solid #ccc;border-radius:6px;text-align:center;font-size:20px;outline:none;background:#fff;color:#333}
                .digits input:focus{border:2px solid #2196f3}.error{min-height:20px;margin-top:6px;color:#b3261e;font-size:13px}
                button{height:38px;min-width:96px;align-self:flex-end;margin-top:auto;padding:8px 16px;border:1px solid #81c784;border-radius:6px;background:#e8f5e9;color:#333;font-size:14px;cursor:pointer}button:hover{background:#c8e6c9}
              </style>
            </head>
            <body><main class="card">
              <h1>Domain access is locked</h1>
              <div class="domain">__SAFE_DOMAIN__</div>
              <p>__SAFE_MESSAGE__</p>
              <form id="unlockForm" action="https://nexzen-domain-lock.invalid/unlock" method="get">
                <label class="pin-label" for="pin1">NexZen PIN</label>
                <div class="digits" aria-label="Six digit NexZen PIN">
                  <input id="pin1" type="password" name="pin1" inputmode="numeric" autocomplete="one-time-code" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 1" autofocus>
                  <input type="password" name="pin2" inputmode="numeric" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 2">
                  <input type="password" name="pin3" inputmode="numeric" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 3">
                  <input type="password" name="pin4" inputmode="numeric" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 4">
                  <input type="password" name="pin5" inputmode="numeric" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 5">
                  <input type="password" name="pin6" inputmode="numeric" maxlength="1" pattern="[0-9]" required aria-label="PIN digit 6">
                </div>
                <input type="hidden" name="domain" value="__DOMAIN__">
                <input type="hidden" name="target" value="__TARGET__">
                <div id="error" class="error"></div><button type="submit">Access</button>
              </form>
            </main>
            <script>
              const pinFields = [...document.querySelectorAll('.digits input')];
              const unlockForm = document.getElementById('unlockForm');
              const errorMessage = document.getElementById('error');
              function submitPin() {
                const firstMissing = pinFields.find(field => !/^[0-9]$/.test(field.value));
                if (firstMissing) {
                  errorMessage.textContent = 'Enter all six digits.';
                  firstMissing.focus();
                  return;
                }
                unlockForm.requestSubmit();
              }
              pinFields.forEach((field, index) => {
                field.addEventListener('input', () => {
                  field.value = field.value.replace(/[^0-9]/g, '').slice(-1);
                  errorMessage.textContent = '';
                  if (field.value && index < pinFields.length - 1) pinFields[index + 1].focus();
                });
                field.addEventListener('keydown', event => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    submitPin();
                    return;
                  }
                  if (event.key === 'Backspace' && !field.value && index > 0) {
                    pinFields[index - 1].focus();
                    pinFields[index - 1].value = '';
                    event.preventDefault();
                  }
                });
                field.addEventListener('paste', event => {
                  const digits = (event.clipboardData || window.clipboardData).getData('text').replace(/[^0-9]/g, '').slice(0, 6);
                  if (!digits) return;
                  event.preventDefault();
                  digits.split('').forEach((digit, digitIndex) => { pinFields[digitIndex].value = digit; });
                  pinFields[Math.min(digits.length, pinFields.length - 1)].focus();
                });
              });
            </script>
            </body></html>
            """.Replace("__SAFE_DOMAIN__", safeDomain, StringComparison.Ordinal)
                .Replace("__SAFE_MESSAGE__", safeMessage, StringComparison.Ordinal)
                .Replace("__DOMAIN__", System.Net.WebUtility.HtmlEncode(rootDomain), StringComparison.Ordinal)
                .Replace("__TARGET__", System.Net.WebUtility.HtmlEncode(targetUrl), StringComparison.Ordinal);
        return "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
    }
}
