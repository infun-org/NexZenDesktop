const CHUNK_SIZE = 32 * 1024;
const WINDOW_BYTES = 8 * 1024 * 1024;

function el(id) { return document.getElementById(id); }
function now() { return Date.now(); }
function uuidv4() {
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, c => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

function addLog(text, cls) {
  const d = document.createElement("div");
  d.className = "msg " + cls;
  d.textContent = text;
  el("log").appendChild(d);
  el("log").scrollTop = el("log").scrollHeight;
}

function keylog(text) {
  const msg = `[${new Date().toISOString()}] ${text}`;
  try { console.log("[OpenOnPC]", msg); } catch (e) {}
  try {
    const root = document.getElementById("log");
    if (root) addLog("[OpenOnPC] " + msg, "sys");
  } catch (e) {}
  try {
    const k = "nexshare_open_on_pc_log";
    const raw = sessionStorage.getItem(k);
    let arr = [];
    try { arr = raw ? JSON.parse(raw) : []; } catch (e) { arr = []; }
    arr.push(msg);
    if (arr.length > 200) arr = arr.slice(arr.length - 200);
    sessionStorage.setItem(k, JSON.stringify(arr));
  } catch (e) {}
}

function formatBytes(bytes) {
  const b = Number(bytes || 0) || 0;
  if (b <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  let v = b;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i += 1;
  }
  const n = i === 0 ? String(Math.floor(v)) : v.toFixed(v >= 10 ? 1 : 2);
  return `${n} ${units[i]}`;
}

function guessMimeByName(name) {
  const n = (name || "").toLowerCase();
  if (n.endsWith(".pdf")) return "application/pdf";
  if (n.endsWith(".png")) return "image/png";
  if (n.endsWith(".jpg") || n.endsWith(".jpeg")) return "image/jpeg";
  if (n.endsWith(".gif")) return "image/gif";
  if (n.endsWith(".webp")) return "image/webp";
  if (n.endsWith(".mp4")) return "video/mp4";
  if (n.endsWith(".webm")) return "video/webm";
  if (n.endsWith(".mov")) return "video/quicktime";
  return "application/octet-stream";
}

function getDownloadUrl(offer) {
  const host = window.location.hostname;
  const port = Number(offer.httpPort || 0) || (info ? Number(info.file_http_port || 0) : 0);
  return port > 0 ? (`http://${host}:${port}/nexshare/file/${offer.transferId}`) : "";
}

function addDownloadOffer(offer) {
  if (!offer || !offer.transferId) return;
  const id = String(offer.transferId);
  const name = (offer.fileName || "file").toString();
  const size = Number(offer.fileSize || 0) || 0;
  const url = getDownloadUrl(offer);
  const mime = (offer.mimeType || offer.mimetype || "").toString().trim() || guessMimeByName(name);
  const prev = downloadsById.get(id);
  downloadsById.set(id, {
    transferId: id,
    fileName: name,
    fileSize: size,
    mimeType: mime,
    url,
    receivedAt: prev ? prev.receivedAt : now()
  });
  if (!selectedDownloadId) selectedDownloadId = id;
  renderDownloads();
  renderPreview();
}

function renderDownloads() {
  const root = el("downloads");
  if (!root) return;
  root.innerHTML = "";
  const items = Array.from(downloadsById.values()).sort((a, b) => (b.receivedAt || 0) - (a.receivedAt || 0));
  for (const it of items) {
    const card = document.createElement("div");
    card.className = "downloadItem" + (it.transferId === selectedDownloadId ? " selected" : "");
    card.title = `${it.fileName} • ${formatBytes(it.fileSize)}`;

    if (it.url) {
      const dl = document.createElement("a");
      dl.className = "downloadBtn";
      dl.href = it.url;
      dl.target = "_blank";
      dl.rel = "noreferrer";
      dl.textContent = "DL";
      dl.onclick = (e) => { e.stopPropagation(); };
      card.appendChild(dl);
    }

    const thumb = document.createElement("div");
    thumb.className = "downloadThumb";
    const m = (it.mimeType || "").toLowerCase();
    if (m.startsWith("image/") && it.url) {
      const img = document.createElement("img");
      img.loading = "lazy";
      img.src = it.url;
      thumb.appendChild(img);
    } else if (m.startsWith("video/") && it.url) {
      const v = document.createElement("video");
      v.muted = true;
      v.preload = "metadata";
      v.playsInline = true;
      v.src = it.url;
      thumb.appendChild(v);
    } else if ((m === "application/pdf" || it.fileName.toLowerCase().endsWith(".pdf")) && it.url) {
      const icon = document.createElement("div");
      icon.className = "downloadIcon";
      icon.textContent = "PDF";
      thumb.appendChild(icon);
    } else {
      const icon = document.createElement("div");
      icon.className = "downloadIcon";
      icon.textContent = "FILE";
      thumb.appendChild(icon);
    }

    const label = document.createElement("div");
    label.className = "downloadName";
    label.textContent = it.fileName;

    card.appendChild(thumb);
    card.appendChild(label);
    card.onclick = () => {
      selectedDownloadId = it.transferId;
      renderDownloads();
      renderPreview();
    };
    root.appendChild(card);
  }
}

function renderPreview() {
  const root = el("preview");
  const meta = el("previewMeta");
  if (!root) return;
  const it = selectedDownloadId ? downloadsById.get(selectedDownloadId) : null;
  if (!it) {
    root.innerHTML = `<div class="muted">Select a file to preview</div>`;
    if (meta) meta.textContent = "";
    return;
  }
  if (meta) meta.textContent = `${formatBytes(it.fileSize)}`;
  root.innerHTML = "";

  const m = (it.mimeType || "").toLowerCase();
  const name = (it.fileName || "").toLowerCase();
  const isPdf = m === "application/pdf" || name.endsWith(".pdf");
  const isImg = m.startsWith("image/");
  const isVid = m.startsWith("video/");
  const okPrev = it.url && (isPdf || isImg || isVid);

  if (!okPrev) {
    const box = document.createElement("div");
    box.className = "previewDetails";
    box.textContent = `${it.fileName} • ${formatBytes(it.fileSize)}`;
    root.appendChild(box);
    return;
  }

  const wrap = document.createElement("div");
  wrap.className = "previewMedia";
  if (isImg) {
    const img = document.createElement("img");
    img.src = it.url;
    wrap.appendChild(img);
  } else if (isVid) {
    const v = document.createElement("video");
    v.controls = true;
    v.playsInline = true;
    v.preload = "metadata";
    v.src = it.url;
    wrap.appendChild(v);
  } else if (isPdf) {
    const f = document.createElement("iframe");
    f.src = it.url;
    wrap.appendChild(f);
  }

  const details = document.createElement("div");
  details.className = "previewDetails";
  const line1 = document.createElement("div");
  line1.textContent = it.fileName;
  const line2 = document.createElement("div");
  line2.className = "muted";
  line2.textContent = `${formatBytes(it.fileSize)} • ${(it.mimeType || "").toString()}`;
  details.appendChild(line1);
  details.appendChild(line2);

  root.appendChild(wrap);
  root.appendChild(details);
}

function env(type, contentStr, senderId) {
  return {
    type,
    version: 1,
    messageId: uuidv4(),
    senderId: senderId || null,
    timestamp: now(),
    content: contentStr
  };
}

function sendAck(ackFor) {
  if (!ackFor || !ws || ws.readyState !== 1) return;
  ws.send(JSON.stringify({
    type: "ack",
    version: 1,
    messageId: uuidv4(),
    ackFor: ackFor,
    senderId: clientId,
    timestamp: now(),
    content: null
  }));
}

function b64FromBytes(bytes) {
  let s = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    s += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  }
  return btoa(s);
}

async function apiJson(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : undefined
  });
  const t = await res.text();
  let j = null;
  try { j = JSON.parse(t); } catch (e) {}
  if (!res.ok) throw new Error((j && j.error) ? j.error : ("http_" + res.status));
  return j;
}

let info = null;
let pair = { requestId: null, token: null, status: "pending" };
let ws = null;
let wsReconnectTimer = null;
let wsReconnectAttempt = 0;
let clientId = localStorage.getItem("nexshare_client_id") || uuidv4();
localStorage.setItem("nexshare_client_id", clientId);
let clientName = localStorage.getItem("nexshare_client_name") || ("PC-" + clientId.slice(0, 4));
localStorage.setItem("nexshare_client_name", clientName);
let upload = { file: null, transferId: null, offset: 0, acked: 0, sending: false, mime: null };
let downloadsById = new Map();
let selectedDownloadId = null;
let handoff = { id: null, token: null, expireAt: 0 };

async function loadInfo() {
  info = await apiJson("GET", "/api/info");
  el("info").textContent = `${info.device_name} • ws:${info.ws_port} • file:${info.file_http_port} • web:${info.web_http_port}`;
}

async function requestPair() {
  el("pairState").textContent = "Requesting approval…";
  pair.requestId = null;
  pair.token = null;
  pair.status = "pending";
  const r = await apiJson("POST", "/api/pair/request", { client_id: clientId, client_name: clientName });
  pair.requestId = r.request_id;
  pollPair();
}

async function pollPair() {
  if (!pair.requestId) return;
  try {
    const r = await apiJson("GET", "/api/pair/status?id=" + encodeURIComponent(pair.requestId));
    pair.status = r.status || "pending";
    if (pair.status === "accepted" && r.token) {
      pair.token = r.token;
      el("pairState").textContent = "Approved";
      el("pair").classList.add("hidden");
      el("app").classList.remove("hidden");
      connectWs();
      return;
    }
    if (pair.status === "rejected" || pair.status === "expired") {
      el("pairState").textContent = "Rejected/Expired";
      return;
    }
    el("pairState").textContent = "Waiting for approval…";
    setTimeout(pollPair, 1000);
  } catch (e) {
    el("pairState").textContent = "Error: " + e.message;
  }
}

function connectWs() {
  if (!info || !pair.token) return;
  if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;
  const host = window.location.hostname;
  if (wsReconnectTimer) {
    clearTimeout(wsReconnectTimer);
    wsReconnectTimer = null;
  }
  ws = new WebSocket(`ws://${host}:${info.ws_port}`);
  el("wsState").textContent = wsReconnectAttempt > 0 ? "Reconnecting…" : "Connecting…";
  ws.onopen = () => {
    el("wsState").textContent = "Connected";
    wsReconnectAttempt = 0;
    const auth = env("browser_auth", JSON.stringify({ requestId: pair.requestId, token: pair.token }), clientId);
    ws.send(JSON.stringify(auth));
    const hello = env("hello", JSON.stringify({ deviceId: clientId, deviceName: clientName, deviceType: "pc" }), clientId);
    ws.send(JSON.stringify(hello));
    fetchHandoffCurrent();
  };
  ws.onclose = () => {
    el("wsState").textContent = "Disconnected";
    scheduleWsReconnect();
  };
  ws.onerror = () => {
    el("wsState").textContent = "Error";
  };
  ws.onmessage = (ev) => {
    let m = null;
    try { m = JSON.parse(ev.data); } catch (e) { return; }
    if (!m || !m.type) return;
    if (m.type === "handoff_created") {
      try {
        const o = JSON.parse(m.content || "{}");
        onHandoffCreated(o);
      } catch (e) {}
      return;
    }
    if (m.type === "text") {
      const t = (m.content || "").toString();
      if (t) addLog(t, "peer");
      if (m.messageId) sendAck(m.messageId);
      return;
    }
    if (m.type === "file_offer") {
      try {
        const o = JSON.parse(m.content || "{}");
        addLog(`File offer: ${o.fileName} (${o.fileSize})`, "sys");
        addDownloadOffer(o);
      } catch (e) {}
      return;
    }
    if (m.type === "file_chunk_req") {
      onChunkReq(m);
      return;
    }
    if (m.type === "file_chunk_ack") {
      onChunkAck(m);
      return;
    }
    if (m.type === "file_cancel") {
      addLog("Transfer cancelled", "sys");
      upload.sending = false;
      return;
    }
  };
}

function scheduleWsReconnect() {
  if (!info || !pair.token) return;
  if (wsReconnectTimer) return;
  wsReconnectAttempt += 1;
  const delay = Math.min(5000, Math.round(500 * Math.pow(1.7, Math.min(wsReconnectAttempt, 10))));
  wsReconnectTimer = setTimeout(() => {
    wsReconnectTimer = null;
    try { connectWs(); } catch (e) {}
  }, delay);
}

async function fetchHandoffCurrent() {
  try {
    const r = await apiJson("GET", "/api/handoff/current");
    if (!r || !r.active || !r.handoff_id || !r.token) return;
    onHandoffCreated({
      handoffId: r.handoff_id,
      title: r.title,
      url: r.url,
      token: r.token,
      timestamp: r.timestamp,
      expireAt: r.expire_at
    });
  } catch (e) {}
}

function ensureHandoffUi() {
  let root = el("handoffToast");
  if (root) return root;
  root = document.createElement("div");
  root.id = "handoffToast";
  root.className = "handoffToast hidden";
  root.innerHTML = `
    <div class="handoffTitle">Open tab from NexZen?</div>
    <div class="handoffMeta" id="handoffMeta"></div>
    <div class="handoffActions">
      <button id="handoffOpenBtn">Open</button>
      <button id="handoffDismissBtn">Dismiss</button>
    </div>
  `;
  document.body.appendChild(root);
  el("handoffDismissBtn").onclick = () => hideHandoff();
  el("handoffOpenBtn").onclick = () => openHandoff();
  return root;
}

function shouldRedirectToOpenOnPc() {
  const p = (window.location && window.location.pathname) ? String(window.location.pathname) : "/";
  return p === "/" || p === "/index.html";
}

function openOnPcDebugEnabled() {
  try { return localStorage.getItem("nexshare_debug") === "1"; } catch (e) { return false; }
}

function onHandoffCreated(o) {
  if (!o || !o.handoffId || !o.token) return;
  handoff.id = String(o.handoffId);
  handoff.token = String(o.token);
  handoff.expireAt = Number(o.expireAt || 0) || 0;
  const title = (o.title || "").toString().trim();
  const url = (o.url || "").toString().trim();
  if (shouldRedirectToOpenOnPc()) {
    keylog("handoff active -> redirect to /open-on-pc");
    const q = [
      "id=" + encodeURIComponent(handoff.id),
      "token=" + encodeURIComponent(handoff.token),
      "title=" + encodeURIComponent(title),
      "url=" + encodeURIComponent(url)
    ].join("&");
    const dbg = openOnPcDebugEnabled() ? "&debug=1" : "";
    window.location.replace("/open-on-pc?" + q + dbg);
    return;
  }
  const meta = (title ? title : "New tab") + (url ? ("\n" + url) : "");
  ensureHandoffUi();
  const metaEl = el("handoffMeta");
  if (metaEl) metaEl.textContent = meta;
  const root = el("handoffToast");
  if (root) root.classList.remove("hidden");
  scheduleHandoffAutoDismiss();
}

function hideHandoff() {
  handoff.id = null;
  handoff.token = null;
  handoff.expireAt = 0;
  const root = el("handoffToast");
  if (root) root.classList.add("hidden");
}

function scheduleHandoffAutoDismiss() {
  const exp = Number(handoff.expireAt || 0) || 0;
  if (exp <= 0) return;
  const delay = Math.max(0, exp - now());
  setTimeout(() => {
    if (handoff.expireAt === exp) hideHandoff();
  }, Math.min(delay + 50, 60_000));
}

async function openHandoff() {
  if (!handoff.id || !handoff.token) return;
  const id = encodeURIComponent(handoff.id);
  const tok = encodeURIComponent(handoff.token);
  try {
    const r = await apiJson("GET", `/api/handoff/open?id=${id}&token=${tok}`);
    const url = (r && r.url) ? String(r.url) : "";
    if (url) window.open(url, "_blank");
    hideHandoff();
  } catch (e) {
    addLog("Handoff failed: " + e.message, "sys");
    hideHandoff();
  }
}

function sendText() {
  const t = el("text").value.trim();
  if (!t || !ws || ws.readyState !== 1) return;
  ws.send(JSON.stringify(env("text", t, clientId)));
  addLog(t, "me");
  el("text").value = "";
}

function offerUpload() {
  const f = el("file").files && el("file").files[0];
  if (!f || !ws || ws.readyState !== 1) return;
  const transferId = uuidv4();
  upload.file = f;
  upload.transferId = transferId;
  upload.offset = 0;
  upload.acked = 0;
  upload.sending = false;
  upload.mime = f.type || "application/octet-stream";
  el("uploadState").textContent = `Offered ${f.name}`;
  const offer = {
    transferId,
    fileName: f.name,
    fileSize: f.size,
    mimeType: upload.mime,
    httpPort: 0
  };
  ws.send(JSON.stringify(env("file_offer", JSON.stringify(offer), clientId)));
}

function onChunkReq(envelope) {
  if (!upload.file || !upload.transferId) return;
  let req = null;
  try { req = JSON.parse(envelope.content || "{}"); } catch (e) { return; }
  if (req.transferId !== upload.transferId) return;
  upload.offset = Number(req.startOffset || 0) || 0;
  upload.acked = upload.offset;
  if (!upload.sending) {
    upload.sending = true;
    pumpUpload();
  }
}

function onChunkAck(envelope) {
  let ack = null;
  try { ack = JSON.parse(envelope.content || "{}"); } catch (e) { return; }
  if (!ack || ack.transferId !== upload.transferId) return;
  upload.acked = Math.max(upload.acked, Number(ack.receivedUpTo || 0) || 0);
}

async function blobToBase64(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result.split(',')[1]);
    reader.onerror = reject;
    reader.readAsDataURL(blob);
  });
}

async function pumpUpload() {
  if (!upload.sending || !upload.file || !ws || ws.readyState !== 1) return;
  const file = upload.file;
  while (upload.offset < file.size) {
    if (!upload.sending || !ws || ws.readyState !== 1) return;
    while ((upload.offset - upload.acked) > WINDOW_BYTES) {
      await new Promise(r => setTimeout(r, 10));
      if (!upload.sending) return;
    }
    const end = Math.min(file.size, upload.offset + CHUNK_SIZE);
    const slice = file.slice(upload.offset, end);
    const b64 = await blobToBase64(slice);
    const chunk = {
      transferId: upload.transferId,
      offset: upload.offset,
      dataB64: b64,
      isLast: false,
      sha256Hex: null
    };
    ws.send(JSON.stringify(env("file_chunk", JSON.stringify(chunk), clientId)));
    upload.offset = end;
    const pct = Math.floor((upload.offset * 100) / file.size);
    el("uploadState").textContent = `Uploading ${file.name} • ${pct}%`;
  }
  const last = {
    transferId: upload.transferId,
    offset: upload.offset,
    dataB64: "",
    isLast: true,
    sha256Hex: null
  };
  ws.send(JSON.stringify(env("file_chunk", JSON.stringify(last), clientId)));
  el("uploadState").textContent = `Upload complete: ${file.name}`;
  upload.sending = false;
}

function bind() {
  el("btnSend").onclick = sendText;
  el("text").addEventListener("keydown", (e) => { if (e.key === "Enter") sendText(); });
  el("btnRetry").onclick = () => { requestPair(); };
  el("btnOffer").onclick = offerUpload;
}

async function main() {
  bind();
  await loadInfo();
  try {
    keylog("probe /api/handoff/current");
    const r = await apiJson("GET", "/api/handoff/current");
    if (r && r.active && r.handoff_id && r.token) {
      keylog("handoff current is active");
      onHandoffCreated({
        handoffId: r.handoff_id,
        title: r.title,
        url: r.url,
        token: r.token,
        timestamp: r.timestamp,
        expireAt: r.expire_at
      });
      return;
    }
    keylog("handoff current inactive");
  } catch (e) {
    keylog("probe failed: " + (e && e.message ? e.message : String(e)));
  }
  await requestPair();
}

main().catch(e => {
  el("pairState").textContent = "Error: " + e.message;
});
