// Jarvis Browser Link: connects to Jarvis on this PC (127.0.0.1 only) and answers its requests.
// It only ever reads the active tab (its text, or a form's questions) when Jarvis asks (you asked Jarvis something), and only opens
// links you approved in Jarvis. It never sends anything anywhere else.

const JARVIS = "ws://127.0.0.1:47613/jarvis";
const PROBE = "http://127.0.0.1:47613/jarvis/";
const MAX_TEXT = 12000;

let socket = null;

let probing = false;

// Knock first: a WebSocket to a closed port is logged as an extension error every 20 s while
// Jarvis isn't running; a failed fetch is caught quietly.
async function connect() {
  if (probing || (socket && socket.readyState <= WebSocket.OPEN)) return;
  probing = true;
  try {
    await fetch(PROBE, { method: "HEAD", cache: "no-store" });
  } catch {
    return; // Jarvis isn't running
  } finally {
    probing = false;
  }
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
    case "act": return await act(request.action, request.target, request.text);
    case "form": return await readForm();
    case "fill": return await formOp("fill", request.question, request.value);
    case "check": return await formOp("check", request.question, request.value);
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

// Where each element id from the last page read lives: its frame and its id inside that frame.
let pageElements = {};

async function readPage() {
  const tab = await activeTab();
  const tabs = (await chrome.tabs.query({ windowId: tab.windowId })).map((t) => ({ id: "T" + t.id, title: t.title || "", active: !!t.active }));
  const page = { url: tab.url || "", title: tab.title || "", selection: "", text: "", elements: [], tabs };
  if (!/^https?:/.test(page.url)) return page; // browser pages (settings, new tab) can't be read
  // Every visible frame: a site's dialog (a verification code box, a sign-in) is often an iframe.
  const frames = (await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: pageOnPage, args: [MAX_TEXT] }))
    .filter((frame) => frame.result)
    .sort((a, b) => (a.frameId === 0 ? -1 : b.frameId === 0 ? 1 : 0));
  pageElements = {};
  const all = [];
  for (const frame of frames) {
    for (const element of frame.result.elements) all.push({ ...element, frameId: frame.frameId || 0 });
  }
  // On-screen first (what the user is looking at, a dialog included), then the rest, up to 120.
  const kept = [...all.filter((e) => e.onScreen), ...all.filter((e) => !e.onScreen)].slice(0, 120);
  const elements = kept.map((element, i) => {
    const id = "e" + (i + 1);
    pageElements[id] = { frameId: element.frameId, local: element.id };
    return { id, kind: element.kind, label: element.label, onScreen: element.onScreen };
  });
  const top = frames.find((frame) => (frame.frameId || 0) === 0)?.result;
  const inner = frames.filter((frame) => (frame.frameId || 0) !== 0 && frame.result.text.trim())
    .map((frame) => "\n\n[In a frame on the page]\n" + frame.result.text).join("");
  return {
    ...page,
    selection: frames.map((frame) => frame.result.selection).find(Boolean) || "",
    text: ((top ? top.text : "") + inner).slice(0, MAX_TEXT),
    elements,
  };
}

// The page's text, and its buttons, links and fields, each tagged with an id ("e12") Jarvis can
// act on. Off-screen ones are marked so Jarvis knows to scroll.
function pageOnPage(limit) {
  if (window !== top && innerWidth * innerHeight < 20000) return null; // hidden or tiny frames (ads, trackers)
  const clean = (text, length = 60) => String(text || "").replace(/\s+/g, " ").trim().slice(0, length);
  // Rows are list entries you open by pressing them (an email in an inbox, a result in a table).
  const selector = 'button, a[href], input:not([type="hidden"]), textarea, select, [role="button"], [role="link"], [role="tab"], [role="menuitem"], [role="checkbox"], [role="radio"], [role="option"], [role="combobox"], [contenteditable="true"], [role="row"]';
  const elements = [];
  for (const node of document.querySelectorAll(selector)) {
    const box = node.getBoundingClientRect();
    if (box.width < 2 || box.height < 2 || getComputedStyle(node).visibility === "hidden") continue;
    if (node.type === "password") continue; // never offered, never read
    const tag = node.tagName.toLowerCase();
    const row = node.getAttribute("role") === "row";
    if (row && node.querySelector('[role="columnheader"]')) continue; // a table's header, not an entry
    const kind = row ? "item" : tag === "a" || node.getAttribute("role") === "link" ? "link"
      : tag === "input" || tag === "textarea" || node.isContentEditable || node.getAttribute("role") === "combobox" ? "field"
      : tag === "select" ? "dropdown" : "button";
    // A field's own label, or what names it nearby: aria-labelledby, a <label>, the question it's in.
    const labelledBy = (node.getAttribute("aria-labelledby") || "").split(/\s+/).map((ref) => ref && document.getElementById(ref)).filter(Boolean).map((el) => el.innerText).join(" ");
    const forLabel = node.id ? document.querySelector(`label[for="${CSS.escape(node.id)}"]`) : null;
    const question = node.closest('[role="listitem"], fieldset, .form-group, label');
    const heading = question && (question.querySelector('[role="heading"], legend') || (question.tagName === "LABEL" ? question : null));
    const own = kind === "field" ? node.getAttribute("aria-label") || labelledBy || (forLabel && forLabel.innerText) || (heading && heading.innerText) || node.placeholder || node.name
      : node.getAttribute("aria-label") || node.innerText || node.value || node.title || labelledBy;
    const label = clean(own, row ? 100 : 60);
    if (!label && kind !== "field") continue;
    const id = "e" + (elements.length + 1);
    node.setAttribute("data-jarvis-e", id);
    const onScreen = box.bottom > 0 && box.top < innerHeight;
    elements.push({ id, kind, label: label || "(unlabelled field)", onScreen });
    if (elements.length >= 200) break;
  }
  return {
    selection: String(window.getSelection() || "").slice(0, 2000),
    text: (document.body ? document.body.innerText : "").replace(/\n{3,}/g, "\n\n").slice(0, limit),
    elements,
  };
}

// What Jarvis copied, kept here so it can be pasted into any tab.
let copied = "";

// One approved step on the page or the tabs. Never submits or buys on its own: Jarvis only sends
// steps the user approved.
async function act(action, target, text) {
  const tab = await activeTab();
  switch (action) {
    case "tab-close": {
      const id = target ? Number(String(target).replace(/^T/i, "")) : tab.id;
      await chrome.tabs.remove(id);
      return { done: true };
    }
    case "tab-switch": {
      const id = Number(String(target).replace(/^T/i, ""));
      const switched = await chrome.tabs.update(id, { active: true });
      await chrome.windows.update(switched.windowId, { focused: true });
      return { done: true };
    }
    case "paste":
      if (!copied) return { done: false, reason: "nothing has been copied yet" };
      return await pageAct(tab, "fill", target, copied);
    case "copy": {
      const result = await pageAct(tab, "copy", target, null);
      if (result.done) copied = result.text || "";
      return result;
    }
    default:
      return await pageAct(tab, action, target, text);
  }
}

async function pageAct(tab, action, target, text) {
  const where = pageElements[target] || { frameId: 0, local: target };
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id, frameIds: [where.frameId] }, world: "MAIN", func: actOnPage, args: [action, where.local, text] });
  return result;
}

// Runs in the page: press a button or link, type into a field (read back), copy text, scroll.
async function actOnPage(action, target, text) {
  const wait = (ms) => new Promise((done) => setTimeout(done, ms));
  const where = (element) => {
    const box = element.getBoundingClientRect();
    const scale = window.devicePixelRatio || 1;
    const left = window.screenX + (window.outerWidth - window.innerWidth) / 2;
    const top = window.screenY + (window.outerHeight - window.innerHeight);
    return { x: Math.round((left + box.left + Math.min(box.width, 240) / 2) * scale), y: Math.round((top + box.top + Math.min(box.height, 60) / 2) * scale) };
  };
  if (action === "scroll") {
    const by = { up: -0.8, down: 0.8 }[text] || 0;
    if (text === "top") window.scrollTo({ top: 0, behavior: "smooth" });
    else if (text === "bottom") window.scrollTo({ top: document.documentElement.scrollHeight, behavior: "smooth" });
    else window.scrollBy({ top: by * innerHeight, behavior: "smooth" });
    await wait(350);
    return { done: true };
  }
  if (action === "copy" && (!target || target === "selection")) {
    const selected = String(window.getSelection() || "");
    return selected ? { done: true, text: selected } : { done: false, reason: "nothing is selected" };
  }
  const element = document.querySelector(`[data-jarvis-e="${CSS.escape(String(target))}"]`);
  if (!element) return { done: false, reason: "that part of the page is gone (the page changed)" };
  element.scrollIntoView({ block: "center", behavior: "smooth" });
  await wait(300);
  if (action === "copy") {
    const value = element.value !== undefined && element.tagName !== "BUTTON" ? element.value : element.innerText;
    return { done: true, text: String(value || "").trim(), ...where(element) };
  }
  if (action === "press") {
    // A plain click first. Options, tabs and checkboxes say whether it took (aria state); if not,
    // a full hover-and-press, which some widgets need.
    const state = () => element.getAttribute("aria-checked") || element.getAttribute("aria-selected") || element.getAttribute("aria-expanded") || element.getAttribute("aria-pressed");
    const before = state();
    const page = location.href;
    const row = element.getAttribute("role") === "row";
    element.click();
    await wait(row ? 500 : 200);
    if ((before !== null && state() === before) || (row && location.href === page)) {
      const box = element.getBoundingClientRect();
      const at = { bubbles: true, cancelable: true, view: window, button: 0, clientX: box.left + box.width / 2, clientY: box.top + box.height / 2 };
      for (const name of ["pointerover", "mouseover", "pointermove", "mousemove", "pointerdown", "mousedown", "pointerup", "mouseup", "click"]) {
        element.dispatchEvent(new (name.startsWith("pointer") ? PointerEvent : MouseEvent)(name, { ...at, buttons: name.endsWith("down") ? 1 : 0 }));
      }
      await wait(row ? 500 : 200);
      if (before !== null && state() === before) return { done: false, reason: "it didn't respond to the press", ...where(element) };
    }
    return { done: true, ...where(element) };
  }
  if (action === "fill") {
    // Set as the page's own code would, then (for pages that only listen to real editing) the
    // browser's own insert-text over a selection, each read back. Always this exact element.
    const value = String(text || "");
    const shown = () => String((element.isContentEditable ? element.innerText : element.value) || "").trim() === value.trim();
    for (const how of element.isContentEditable ? ["insert"] : ["set", "insert"]) {
      element.focus();
      if (how === "set") {
        const proto = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : element.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
        Object.getOwnPropertyDescriptor(proto, "value").set.call(element, value);
        element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: value }));
      } else {
        if (element.isContentEditable) document.execCommand("selectAll");
        else if (element.select) element.select();
        document.execCommand("insertText", false, value);
      }
      element.dispatchEvent(new Event("change", { bubbles: true }));
      await wait(300);
      if (shown()) return { done: true, ...where(element) };
    }
    return { done: false, reason: "the page didn't take the text", ...where(element) };
  }
  return { done: false, reason: "unknown step " + action };
}

// The frame the last form was read in (an embedded form lives in an iframe); fills go there too.
let formFrame = 0;

// How to find each question's field again, by question id, from the last read. Pages that redraw
// after every change (PeopleSoft, many job portals) replace their fields, so a fill finds its field
// by its HTML id, then name, then label, never by where it was on screen.
let formFields = {};

// Reads the form on the active tab: each question's text, kind and offered options (never what
// the user typed). Google Forms by its ARIA roles; any other page by its labelled fields. Looks
// in every frame and keeps the one with the most questions. When there is none, says why.
async function readForm() {
  const tab = await activeTab();
  const base = {
    url: tab.url || "",
    title: tab.title || "",
    tabTitle: tab.title || "",
    questions: [],
    version: chrome.runtime.getManifest().version,
    allowedInPrivate: await chrome.extension.isAllowedIncognitoAccess(),
  };
  if (!/^https?:/.test(base.url)) return { ...base, reason: "the tab in front isn't a web page" };
  let frames;
  try {
    frames = await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: formPage, args: ["read"] });
  } catch (error) {
    return { ...base, reason: "the browser wouldn't let Jarvis read this page (" + String(error && error.message || error) + ")" };
  }
  const best = frames.filter((frame) => frame.result).sort((a, b) => b.result.questions.length - a.result.questions.length)[0];
  if (!best || best.result.questions.length === 0) return { ...base, reason: "no questions on the page yet (is it still loading?)" };
  formFrame = best.frameId || 0;
  formFields = Object.fromEntries(best.result.questions.map((q) => [q.id, q.find]));
  const questions = best.result.questions.map(({ find, ...question }) => question);
  return { ...base, title: best.result.title || tab.title || "", description: best.result.description, questions };
}

// Filling runs in the page's own world (not the extension's isolated one), so the page's scripts
// see a value exactly as if their own code had set it. Never submits.
async function formOp(op, question, value) {
  const tab = await activeTab();
  const [{ result }] = await chrome.scripting.executeScript({
    target: { tabId: tab.id, frameIds: [formFrame] }, world: "MAIN", func: formPage, args: [op, question, value, formFields[question] || null],
  });
  return result;
}

// Everything done to a form inside the page, in one function because each injection runs on its own:
//   "read":  the questions, each with how to find its field again
//   "fill":  puts one answer in (typed the way the page's own editing would, or the matching
//            options clicked), waits for the page to settle, and reads it back. Only counts as
//            filled when the page really shows it. Returns where the field is, for the cursor.
//   "check": whether the question shows the value now
async function formPage(op, question, value, find) {
  const clean = (text) => String(text || "").replace(/\s+/g, " ").replace(/\s*\*\s*$/, "").replace(/^\s*\*\s*/, "").trim();
  const same = (a, b) => clean(a).toLowerCase() === clean(b).toLowerCase();
  const wait = (ms) => new Promise((done) => setTimeout(done, ms));
  const visible = (element) => !!element && element.getClientRects().length > 0;
  const labelOf = (field) => {
    const byFor = field.id && document.querySelector(`label[for="${CSS.escape(field.id)}"]`);
    const around = field.closest("label");
    const by = field.getAttribute("aria-labelledby");
    const labelled = by && by.split(/\s+/).map((id) => document.getElementById(id)?.textContent || "").join(" ");
    return clean((byFor && byFor.textContent) || field.getAttribute("aria-label") || labelled || (around && around.textContent) || field.placeholder || field.title || field.name);
  };
  const googleTitle = (item) => {
    const heading = item.querySelector('[role="heading"]');
    return heading ? clean((heading.querySelector("span") || heading).textContent) : "";
  };
  const DATE_WORDS = /\b(date|birth|born|dob|d\.o\.b)\b/i;

  function read() {
    const values = (list, attribute) => [...list].map((n) => n.getAttribute(attribute) || n.getAttribute("aria-label") || "").filter(Boolean);
    const questions = [];
    const add = (question, elements, find) => {
      const id = "q" + questions.length;
      elements.forEach((element) => element.setAttribute("data-jarvis-q", id));
      questions.push({ id, ...question, find });
    };

    // Google Forms: one listitem per question, a heading with the text and a "Required question" marker.
    for (const item of document.querySelectorAll('[role="listitem"]')) {
      const title = googleTitle(item);
      if (!title) continue;
      const heading = item.querySelector('[role="heading"]');
      let kind = "other", options = [];
      if (item.querySelector('[role="radio"]')) { kind = "choice"; options = values(item.querySelectorAll('[role="radio"]'), "data-value"); }
      else if (item.querySelector('[role="checkbox"]')) { kind = "checkbox"; options = values(item.querySelectorAll('[role="checkbox"]'), "data-answer-value"); }
      else if (item.querySelector('[role="listbox"]')) { kind = "dropdown"; options = values(item.querySelectorAll('[role="option"][data-value]:not([data-value=""])'), "data-value"); }
      else if (item.querySelector('input[type="date"]')) kind = "date";
      else if (item.querySelector('input[type="time"]')) kind = "time";
      else if (item.querySelector("textarea")) kind = "paragraph";
      else if (item.querySelector('input[type="text"], input[type="email"], input[type="tel"], input[type="url"], input[type="number"]')) kind = "text";
      add({ text: title, kind, options: [...new Set(options)], required: !!heading.querySelector('[aria-label="Required question"]') }, [item], { google: title, kind });
    }
    const meta = document.querySelector('meta[property="og:description"], meta[name="description"]');
    const description = clean(meta && meta.content).slice(0, 1000);
    if (questions.length) {
      const title = document.querySelector('[role="heading"][aria-level="1"]');
      return { title: clean(title && title.textContent), description, questions };
    }

    // Any other form: visible, labelled fields. Radio buttons are grouped by name.
    const groups = new Set();
    for (const field of document.querySelectorAll("input, textarea, select")) {
      const type = (field.type || "").toLowerCase();
      if (["hidden", "password", "submit", "button", "reset", "image", "file"].includes(type) || !visible(field) || field.disabled) continue;
      if (type === "radio" || type === "checkbox") {
        if (!field.name || groups.has(field.name)) continue;
        groups.add(field.name);
        const all = [...document.querySelectorAll(`input[name="${CSS.escape(field.name)}"]`)];
        const legend = field.closest("fieldset") && field.closest("fieldset").querySelector("legend");
        add({ text: clean(legend ? legend.textContent : field.name), kind: type === "radio" ? "choice" : "checkbox", options: all.map(labelOf).filter(Boolean), required: field.required }, all, { group: field.name });
        continue;
      }
      const text = labelOf(field);
      if (!text) continue;
      const kind = field.tagName === "SELECT" ? "dropdown" : field.tagName === "TEXTAREA" ? "paragraph" : type === "date" ? "date" : type === "time" ? "time" : "text";
      const options = field.tagName === "SELECT" ? [...field.options].filter((o) => o.value !== "").map((o) => clean(o.textContent)).filter(Boolean) : [];
      add({ text, kind, options, required: field.required || field.getAttribute("aria-required") === "true" }, [field], { id: field.id, name: field.name, label: text, kind });
    }
    return { title: document.title, description, questions: questions.slice(0, 80) };
  }

  // --- finding the field again: the tagged element if it's still on the page; else the same field found again (and re-tagged).
  function locate() {
    const tagged = [...document.querySelectorAll(`[data-jarvis-q="${CSS.escape(question)}"]`)];
    if (tagged.length) return tagged;
    if (!find) return [];
    let found = [];
    if (find.google) found = [...document.querySelectorAll('[role="listitem"]')].filter((item) => googleTitle(item) === find.google).slice(0, 1);
    else if (find.group) found = [...document.querySelectorAll(`input[name="${CSS.escape(find.group)}"]`)];
    else {
      const named = find.name ? [...document.getElementsByName(find.name)].filter(visible) : [];
      const one = (find.id && document.getElementById(find.id))
        || (named.length === 1 ? named[0] : null)
        || [...document.querySelectorAll("input, textarea, select")].find((field) => visible(field) && labelOf(field) === find.label);
      if (one) found = [one];
    }
    found.forEach((element) => element.setAttribute("data-jarvis-q", question));
    return found;
  }

  // Waits until the page stops changing (a redraw after a change, a round trip to its server),
  // up to a few seconds. Busy markers (PeopleSoft's "Processing", aria-busy) count as changing.
  async function settle(quiet = 450, cap = 4000) {
    const start = Date.now();
    let last = Date.now();
    const observer = new MutationObserver(() => { last = Date.now(); });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    try {
      while (Date.now() - start < cap) {
        await wait(100);
        const busy = [...document.querySelectorAll('[aria-busy="true"], [id^="WAIT_win"], [id^="processing"]')].some(visible);
        if (!busy && Date.now() - last >= quiet) return;
      }
    } finally {
      observer.disconnect();
    }
  }

  // --- dates: one remembered date, written the way each field wants it ---
  function parseDate(text) {
    const t = clean(text);
    const months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
    const month = (word) => months.indexOf(String(word).slice(0, 3).toLowerCase()) + 1;
    const ok = (y, m, d) => (y > 999 && m >= 1 && m <= 12 && d >= 1 && d <= 31 ? { y, m, d } : null);
    let m = t.match(/^(\d{4})[-/. ](\d{1,2})[-/. ](\d{1,2})$/);
    if (m) return ok(+m[1], +m[2], +m[3]);
    m = t.match(/^(\d{1,2})[-/. ](\d{1,2})[-/. ](\d{2}|\d{4})$/);
    if (m) {
      let [a, b, y] = [+m[1], +m[2], +m[3]];
      if (y < 100) y += y > new Date().getFullYear() % 100 ? 1900 : 2000;
      // ponytail: day first unless that's impossible; a US-only month-first date with both parts
      // under 13 (04/03/2004) reads as 4 March. Remembered dates written ISO (2004-03-04) never are.
      return b > 12 ? ok(y, a, b) : ok(y, b, a);
    }
    m = t.match(/^(\d{1,2})(?:st|nd|rd|th)?[-\s]+([a-z]+)\.?,?[-\s]+(\d{4})$/i);
    if (m && month(m[2])) return ok(+m[3], month(m[2]), +m[1]);
    m = t.match(/^([a-z]+)\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})$/i);
    if (m && month(m[1])) return ok(+m[3], month(m[1]), +m[2]);
    return null;
  }
  const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const iso = (date) => `${date.y}-${String(date.m).padStart(2, "0")}-${String(date.d).padStart(2, "0")}`;
  // A format like DD/MM/YYYY, mm-dd-yy or DD-MMM-YYYY, from what the page says about the field.
  function formatHint(field) {
    const described = (field.getAttribute("aria-describedby") || "").split(/\s+/).map((id) => document.getElementById(id)?.textContent || "").join(" ");
    const near = field.parentElement ? field.parentElement.textContent.slice(0, 300) : "";
    for (const text of [field.placeholder, field.title, field.getAttribute("data-date-format"), field.getAttribute("data-format"), described, labelOf(field), near]) {
      const m = String(text || "").match(/\b(d{1,2}|m{1,4}|y{2,4})([-/. ])(d{1,2}|m{1,4}|y{2,4})\2(d{1,2}|m{1,4}|y{2,4})\b/i);
      if (m) {
        const parts = [m[1], m[3], m[4]];
        const order = parts.map((p) => p[0].toLowerCase()).join("");
        if (["dmy", "mdy", "ymd"].includes(order)) return { parts, sep: m[2] };
      }
    }
    return null;
  }
  const writeDate = (date, format) => format.parts.map((p) => {
    const kind = p[0].toLowerCase();
    if (kind === "d") return p.length > 1 ? String(date.d).padStart(2, "0") : String(date.d);
    if (kind === "m") return p.length >= 3 ? MONTHS[date.m - 1] : p.length > 1 ? String(date.m).padStart(2, "0") : String(date.m);
    return p.length === 2 ? String(date.y).slice(-2) : String(date.y);
  }).join(format.sep);
  // The ways a date may legitimately read back (the page may reformat what it was given).
  const dateShown = (shown, date) => {
    const parsed = parseDate(shown);
    if (parsed && iso(parsed) === iso(date)) return true;
    const formats = ["DD/MM/YYYY", "MM/DD/YYYY", "YYYY/MM/DD", "DD/MMM/YYYY", "MM/DD/YY", "DD/MM/YY"].map((f) => ({ parts: f.split("/"), sep: "/" }));
    const norm = (s) => clean(s).toLowerCase().replace(/[-. ]/g, "/");
    return formats.some((f) => norm(writeDate(date, f)) === norm(shown));
  };
  const isDateQuestion = () => find && (find.kind === "date" || DATE_WORDS.test(find.label || find.google || ""));

  // --- reading back ---
  function shows(nodes) {
    const root = nodes[0];
    const wanted = String(value).split(";").map((part) => part.trim()).filter(Boolean);
    const date = isDateQuestion() ? parseDate(value) : null;
    const text = (field) => (date ? dateShown(field.value, date) || (field.type === "date" && field.value === iso(date)) : same(field.value, value));
    if (root.getAttribute("role") === "listitem") {
      const listbox = root.querySelector('[role="listbox"]');
      if (root.querySelector('[role="radio"]')) {
        const radio = root.querySelector('[role="radio"][aria-checked="true"]');
        return !!radio && same(radio.getAttribute("data-value") || radio.getAttribute("aria-label"), value);
      }
      if (root.querySelector('[role="checkbox"]')) {
        const on = [...root.querySelectorAll('[role="checkbox"][aria-checked="true"]')].map((b) => b.getAttribute("data-answer-value") || b.getAttribute("aria-label"));
        return on.length === wanted.length && wanted.every((w) => on.some((b) => same(b, w)));
      }
      if (listbox) return [...listbox.querySelectorAll('[role="option"][aria-selected="true"]')].some((o) => same(o.getAttribute("data-value"), value));
      const field = root.querySelector('input[type="date"], textarea, input:not([type="hidden"])');
      return !!field && text(field);
    }
    if (root.tagName === "SELECT") return same(root.selectedOptions[0] && root.selectedOptions[0].textContent, value) || same(root.value, value);
    if (root.type === "radio" || root.type === "checkbox") {
      const on = nodes.filter((input) => input.checked).map(labelOf);
      return on.length === wanted.length && wanted.every((w) => on.some((label) => same(label, w)));
    }
    return text(root);
  }

  // --- putting it in ---
  async function fill(nodes) {
    const root = nodes[0];
    root.scrollIntoView({ block: "center" });
    await settle(150, 3000); // let an earlier field's redraw finish first

    // Some widgets ignore a bare click(): they want a hover, then a press, at the element.
    const press = (element) => {
      const box = element.getBoundingClientRect();
      const at = { bubbles: true, cancelable: true, view: window, button: 0, clientX: box.left + box.width / 2, clientY: box.top + box.height / 2 };
      for (const name of ["pointerover", "mouseover", "pointermove", "mousemove", "pointerdown", "mousedown", "pointerup", "mouseup", "click"]) {
        element.dispatchEvent(new (name.startsWith("pointer") ? PointerEvent : MouseEvent)(name, { ...at, buttons: name.endsWith("down") ? 1 : 0 }));
      }
    };
    const key = (element, name, code, keyCode) => {
      for (const kind of ["keydown", "keypress", "keyup"]) element.dispatchEvent(new KeyboardEvent(kind, { key: name, code, keyCode, which: keyCode, bubbles: true, cancelable: true }));
    };
    const where = (element) => {
      // ponytail: the browser's own toolbar height is outer minus inner height; zoom and side
      // panels shift this a little, which only moves where the cursor points.
      const box = element.getBoundingClientRect();
      const scale = window.devicePixelRatio || 1;
      const left = window.screenX + (window.outerWidth - window.innerWidth) / 2;
      const top = window.screenY + (window.outerHeight - window.innerHeight);
      return { x: Math.round((left + box.left + Math.min(box.width, 240) / 2) * scale), y: Math.round((top + box.top + Math.min(box.height, 60) / 2) * scale) };
    };
    // The field as it is now: pages that redraw replace it, so look it up again.
    const current = (field) => {
      if (field.isConnected) return field;
      const fresh = locate()[0];
      if (!fresh) return field;
      return fresh.matches("input, textarea, select") ? fresh : fresh.querySelector('input[type="date"], textarea, input:not([type="hidden"])') || field;
    };
    // Typed in two ways, each read back: the value set as the page's own code would (with input and
    // change events), then, for pages that only listen to real editing, the browser's own "insert
    // text" over a selection. Both act on this exact element; nothing is typed at the screen.
    const type = async (field, text, accepts) => {
      for (const how of ["set", "insert"]) {
        field = current(field);
        field.focus();
        if (how === "set") {
          const proto = field.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          Object.getOwnPropertyDescriptor(proto, "value").set.call(field, text);
          field.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: text }));
        } else {
          if (field.select) field.select();
          if (!document.execCommand("insertText", false, text)) continue;
        }
        field.dispatchEvent(new Event("change", { bubbles: true }));
        field.blur();
        await settle();
        field = current(field);
        if (accepts(field)) return true;
      }
      return false;
    };
    const rejected = (field) => !field.value || field.getAttribute("aria-invalid") === "true" || (field.checkValidity && !field.checkValidity());

    const wanted = String(value).split(";").map((part) => part.trim()).filter(Boolean);
    let reason = null;
    let target = root;

    if (root.getAttribute("role") === "listitem") {
      // Google Forms.
      const radios = [...root.querySelectorAll('[role="radio"]')];
      const boxes = [...root.querySelectorAll('[role="checkbox"]')];
      const listbox = root.querySelector('[role="listbox"]');
      if (radios.length) {
        const match = radios.find((r) => same(r.getAttribute("data-value") || r.getAttribute("aria-label"), value));
        if (!match) reason = "no option matches";
        else if (match.getAttribute("aria-checked") !== "true") { match.click(); await wait(150); if (match.getAttribute("aria-checked") !== "true") press(match); }
      } else if (boxes.length) {
        for (const box of boxes) {
          const on = wanted.some((w) => same(box.getAttribute("data-answer-value") || box.getAttribute("aria-label"), w));
          if (on !== (box.getAttribute("aria-checked") === "true")) { box.click(); await wait(100); }
        }
      } else if (listbox) {
        // Google's dropdown opens on Space (synthetic clicks don't open it reliably) and takes an
        // option on a hover-and-press at it.
        listbox.focus();
        key(listbox, " ", "Space", 32);
        let option = null;
        for (let tries = 0; tries < 12 && !option; tries++) {
          await wait(120);
          option = [...root.querySelectorAll('[role="option"][data-value]')].find((o) => o.getBoundingClientRect().height > 0 && same(o.getAttribute("data-value"), value));
        }
        if (option) press(option);
        else { key(listbox, "Escape", "Escape", 27); reason = "no option matches"; }
      } else {
        target = root.querySelector('input[type="date"], textarea, input[type="text"], input[type="email"], input[type="tel"], input[type="url"], input[type="number"], input[type="time"]');
        if (!target) reason = "no field to type in";
        else reason = await typeAnswer(target);
      }
    } else if (root.tagName === "SELECT") {
      const option = [...root.options].find((o) => same(o.textContent, value) || same(o.value, value));
      if (option) { root.value = option.value; root.dispatchEvent(new Event("change", { bubbles: true })); }
      else reason = "no option matches";
    } else if (root.type === "radio" || root.type === "checkbox") {
      for (const input of nodes) {
        const on = wanted.some((w) => same(labelOf(input), w));
        if (on !== input.checked && (on || input.type === "checkbox")) input.click();
      }
    } else {
      reason = await typeAnswer(root);
    }

    await settle();
    const again = locate();
    const filled = reason === null && again.length > 0 && shows(again);
    return { filled, reason: filled ? null : reason || "the page doesn't show it", ...where(target ? current(target) : root) };

    // A text or date field; null when it took the value.
    async function typeAnswer(field) {
      const date = isDateQuestion() || field.type === "date" ? parseDate(value) : null;
      if (field.type === "date") return date && (await type(field, iso(date), (f) => f.value === iso(date))) ? null : date ? "the page didn't take it" : "not a date";
      if (!date) return (await type(field, value, (f) => same(f.value, value))) ? null : "the page didn't take it";
      // A date in a text box: the format the page asks for; if it doesn't say, the page's
      // language decides day or month first, and the other order is tried if the page refuses.
      const hint = formatHint(field);
      const monthFirst = /^en-US$/i.test(document.documentElement.lang || navigator.language);
      const tries = hint ? [hint] : [monthFirst ? "MM/DD/YYYY" : "DD/MM/YYYY", monthFirst ? "DD/MM/YYYY" : "MM/DD/YYYY"].map((f) => ({ parts: f.split("/"), sep: "/" }));
      for (const format of tries) {
        if (await type(field, writeDate(date, format), (f) => !rejected(f) && dateShown(f.value, date))) return null;
      }
      return "the page didn't take this date (" + tries.map((f) => f.parts.join(f.sep)).join(" or ") + ")";
    }
  }

  // (Down here so every helper above exists before it runs.)
  if (op === "read") return read();
  const nodes = locate();
  if (!nodes.length) return op === "check" ? false : { filled: false, reason: "the field is gone (was the page reloaded?)" };
  if (op === "check") return shows(nodes);
  return await fill(nodes);
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
