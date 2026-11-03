// Jarvis Browser Link: connects to Jarvis on this PC (127.0.0.1 only) and answers its requests.
// It only ever reads the active tab when Jarvis asks (you asked Jarvis something), and only opens
// links you approved in Jarvis. It never sends anything anywhere else.

const JARVIS = "ws://127.0.0.1:47613/jarvis";
const MAX_TEXT = 12000;

let socket = null;

function connect() {
  if (socket && socket.readyState <= WebSocket.OPEN) return;
  try {
    socket = new WebSocket(JARVIS);
  } catch {
    return;
  }

  socket.onopen = () => setBadge("on");
  socket.onclose = () => { setBadge(""); socket = null; };
  socket.onerror = () => {};
  socket.onmessage = async (event) => {
    let request;
    try { request = JSON.parse(event.data); } catch { return; }
    let reply;
    try {
      reply = { id: request.id, ok: true, result: await handle(request) };
    } catch (error) {
      reply = { id: request.id, ok: false, error: String(error && error.message || error) };
    }
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(reply));
  };
}

async function handle(request) {
  switch (request.type) {
    case "page": return await readPage();
    case "open": return await openUrl(request.url);
    case "ping": return "pong";
    default: throw new Error("Unknown request: " + request.type);
  }
}

async function activeTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab) throw new Error("No browser tab is open.");
  return tab;
}

async function readPage() {
  const tab = await activeTab();
  const page = { url: tab.url || "", title: tab.title || "", selection: "", text: "" };
  if (!/^https?:/.test(page.url)) return page; // browser pages (settings, new tab) can't be read
  const [{ result }] = await chrome.scripting.executeScript({
    target: { tabId: tab.id },
    func: (limit) => ({
      selection: String(window.getSelection() || "").slice(0, 2000),
      text: (document.body ? document.body.innerText : "").replace(/\n{3,}/g, "\n\n").slice(0, limit),
    }),
    args: [MAX_TEXT],
  });
  return { ...page, ...result };
}

async function openUrl(url) {
  if (!/^https?:\/\//i.test(url || "")) throw new Error("Only http and https links can be opened.");
  const tab = await activeTab().catch(() => null);
  if (tab && (tab.url === "chrome://newtab/" || tab.url === "about:blank")) {
    await chrome.tabs.update(tab.id, { url });
  } else {
    await chrome.tabs.create({ url, active: true });
  }
  return "opened";
}

function setBadge(text) {
  chrome.action.setBadgeText({ text });
  chrome.action.setBadgeBackgroundColor({ color: "#3380FF" });
}

// Reconnect while Jarvis isn't running, and keep the service worker alive while connected.
chrome.alarms.create("jarvis-reconnect", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(connect);
setInterval(() => {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: "keepalive" }));
  else connect();
}, 20000);
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
