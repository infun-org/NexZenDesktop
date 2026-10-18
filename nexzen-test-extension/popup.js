let polyfillStatus = "Not Run";
if (typeof globalThis !== 'undefined' && !globalThis.__nexzen_polyfilled) {
    globalThis.__nexzen_polyfilled = true;
    if (typeof chrome !== 'undefined' && chrome.tabs && chrome.tabs.query) {
        const originalQuery = chrome.tabs.query;
        try {
            Object.defineProperty(chrome.tabs, 'query', {
                value: function(queryInfo, callback) {
                    if (queryInfo && queryInfo.active) {
                        const fetchPromise = fetch('http://127.0.0.1:32001/api/active_tab')
                            .then(r => r.json())
                            .then(tab => tab ? [tab] : [])
                            .catch(e => {
                                console.error("Fetch failed", e);
                                return [];
                            });
                        if (callback) {
                            fetchPromise.then(tabs => callback(tabs));
                            return;
                        }
                        return fetchPromise;
                    }
                    if (callback) return originalQuery.call(chrome.tabs, queryInfo, callback);
                    return originalQuery.call(chrome.tabs, queryInfo);
                },
                writable: true,
                configurable: true
            });
            polyfillStatus = "Success";
        } catch(e) {
            polyfillStatus = "Error: " + e.message;
        }
    } else {
        polyfillStatus = "chrome.tabs.query not found";
    }
} else {
    polyfillStatus = "Already polyfilled";
}

document.getElementById('testBtn').addEventListener('click', () => {
  const statusDiv = document.getElementById('status');
  statusDiv.innerText = "Polyfill: " + polyfillStatus + "\nSending ping...";
  
  chrome.tabs.query({ active: true }, (tabs) => {
      const p = document.createElement("p");
      p.innerText = "Popup Query result: " + (tabs ? tabs.length : "null");
      document.body.appendChild(p);
  });

  chrome.runtime.sendMessage({ action: "ping" }, (response) => {
    if (chrome.runtime.lastError) {
      statusDiv.innerText += "\nError: " + chrome.runtime.lastError.message;
    } else {
      statusDiv.innerText += "\nResponse: " + response.status;
    }
  });
});
