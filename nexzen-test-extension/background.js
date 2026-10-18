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
                            .catch(e => []);
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
        } catch(e) {}
    }
}

console.log("[NexZen Ext] Background Service Worker started!");
console.log("[NexZen Ext] My ID is: " + chrome.runtime.id);
console.log("NEXZEN_EXT_ID:" + chrome.runtime.id);

// Test chrome.tabs.query upon startup
chrome.tabs.query({ active: true }, (tabs) => {
  console.log("[NexZen Ext] chrome.tabs.query({ active: true }) result: " + JSON.stringify(tabs));
});

chrome.runtime.onInstalled.addListener(() => {
  console.log("[NexZen Ext] Extension Installed!");
  chrome.storage.local.set({ nexzen_test: "Storage is working" });
});

// Listen for messages from content script or popup
chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  console.log("[NexZen Ext] Background received message:", request);
  if (request.action === "ping") {
    chrome.tabs.query({ active: true }, (tabs) => {
      sendResponse({ status: "Background is alive! Active tabs count: " + (tabs ? tabs.length : "null") });
    });
    return true; // async
  }
  return true;
});
