const div = document.createElement('div');
div.style.position = 'fixed';
div.style.top = '0';
div.style.left = '0';
div.style.width = '100%';
div.style.backgroundColor = 'red';
div.style.color = 'white';
div.style.zIndex = '999999';
div.style.padding = '10px';
div.style.textAlign = 'center';
div.style.fontFamily = 'monospace';

const popupUrl = chrome.runtime.getURL("popup.html");
const extId = chrome.runtime.id;

div.innerHTML = `
  NexZen MV3 Extension Content Script Active!<br>
  <a href="#" id="openPopupBtn" style="color: black; font-weight: bold; background: yellow; padding: 5px; text-decoration: none; border-radius: 5px;">CLICK HERE TO OPEN POPUP (window.open)</a><br>
  Extension ID: ${extId}<br>
  Popup URL: ${popupUrl}
`;

document.body.appendChild(div);

document.getElementById('openPopupBtn').addEventListener('click', (e) => {
  e.preventDefault();
  window.open(popupUrl, '_blank', 'width=400,height=500');
});

chrome.storage.local.get("nexzen_test", (data) => {
    const p = document.createElement("div");
    p.innerText = "Storage Read: " + data.nexzen_test;
    div.appendChild(p);
});

chrome.runtime.sendMessage({ action: "ping" }, (response) => {
    const p = document.createElement("div");
    p.innerText = "Background Ping: " + (response ? response.status : "No response");
    div.appendChild(p);
});
