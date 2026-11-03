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
    case "fill": return await inPage(fillOnPage, [request.question, request.value]);
    case "check": return await inPage(checkOnPage, [request.question, request.value]);
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
  const tabs = (await chrome.tabs.query({ windowId: tab.windowId })).map((t) => ({ id: "T" + t.id, title: t.title || "", active: !!t.active }));
  const page = { url: tab.url || "", title: tab.title || "", selection: "", text: "", elements: [], tabs };
  if (!/^https?:/.test(page.url)) return page; // browser pages (settings, new tab) can't be read
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: pageOnPage, args: [MAX_TEXT] });
  return { ...page, ...result };
}

// The page's text, and its buttons, links and fields, each tagged with an id ("e12") Jarvis can
// act on. Off-screen ones are marked so Jarvis knows to scroll.
function pageOnPage(limit) {
  const clean = (text) => String(text || "").replace(/\s+/g, " ").trim().slice(0, 60);
  const selector = 'button, a[href], input:not([type="hidden"]), textarea, select, [role="button"], [role="link"], [role="tab"], [role="menuitem"], [role="checkbox"], [role="radio"], [role="option"], [role="combobox"], [contenteditable="true"]';
  const elements = [];
  for (const node of document.querySelectorAll(selector)) {
    const box = node.getBoundingClientRect();
    if (box.width < 2 || box.height < 2 || getComputedStyle(node).visibility === "hidden") continue;
    if (node.type === "password") continue; // never offered, never read
    const tag = node.tagName.toLowerCase();
    const kind = tag === "a" || node.getAttribute("role") === "link" ? "link"
      : tag === "input" || tag === "textarea" || node.isContentEditable || node.getAttribute("role") === "combobox" ? "field"
      : tag === "select" ? "dropdown" : "button";
    // A field's own label, or what names it nearby: aria-labelledby, a <label>, the question it's in.
    const labelledBy = (node.getAttribute("aria-labelledby") || "").split(/\s+/).map((ref) => ref && document.getElementById(ref)).filter(Boolean).map((el) => el.innerText).join(" ");
    const forLabel = node.id ? document.querySelector(`label[for="${CSS.escape(node.id)}"]`) : null;
    const question = node.closest('[role="listitem"], fieldset, .form-group, label');
    const heading = question && (question.querySelector('[role="heading"], legend') || (question.tagName === "LABEL" ? question : null));
    const own = kind === "field" ? node.getAttribute("aria-label") || labelledBy || (forLabel && forLabel.innerText) || (heading && heading.innerText) || node.placeholder || node.name
      : node.getAttribute("aria-label") || node.innerText || node.value || node.title || labelledBy;
    const label = clean(own);
    if (!label && kind !== "field") continue;
    const id = "e" + (elements.length + 1);
    node.setAttribute("data-jarvis-e", id);
    const onScreen = box.bottom > 0 && box.top < innerHeight;
    elements.push({ id, kind, label: label || "(unlabelled field)", onScreen });
    if (elements.length >= 80) break;
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
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, world: "MAIN", func: actOnPage, args: [action, target, text] });
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
    element.click();
    await wait(200);
    if (before !== null && state() === before) {
      const box = element.getBoundingClientRect();
      const at = { bubbles: true, cancelable: true, view: window, button: 0, clientX: box.left + box.width / 2, clientY: box.top + box.height / 2 };
      for (const name of ["pointerover", "mouseover", "pointermove", "mousemove", "pointerdown", "mousedown", "pointerup", "mouseup", "click"]) {
        element.dispatchEvent(new (name.startsWith("pointer") ? PointerEvent : MouseEvent)(name, { ...at, buttons: name.endsWith("down") ? 1 : 0 }));
      }
      await wait(200);
      if (state() === before) return { done: false, reason: "it didn't respond to the press", ...where(element) };
    }
    return { done: true, ...where(element) };
  }
  if (action === "fill") {
    const value = String(text || "");
    element.focus();
    if (element.isContentEditable) {
      document.execCommand("selectAll");
      document.execCommand("insertText", false, value);
    } else {
      const proto = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : element.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, "value").set.call(element, value);
      element.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: value }));
      element.dispatchEvent(new Event("change", { bubbles: true }));
    }
    await wait(300);
    const shown = element.isContentEditable ? element.innerText : element.value;
    if (String(shown || "").trim() === value.trim()) return { done: true, ...where(element) };
    element.focus();
    if (element.select) element.select();
    return { done: false, focused: true, reason: "the page didn't take a scripted value", ...where(element) };
  }
  return { done: false, reason: "unknown step " + action };
}

// The frame the last form was read in (an embedded form lives in an iframe); fills go there too.
let formFrame = 0;

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
    frames = await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: formOnPage });
  } catch (error) {
    return { ...base, reason: "the browser wouldn't let Jarvis read this page (" + String(error && error.message || error) + ")" };
  }
  const best = frames.filter((frame) => frame.result).sort((a, b) => b.result.questions.length - a.result.questions.length)[0];
  if (!best || best.result.questions.length === 0) return { ...base, reason: "no questions on the page yet (is it still loading?)" };
  formFrame = best.frameId || 0;
  return { ...base, title: best.result.title || tab.title || "", description: best.result.description, questions: best.result.questions };
}

function formOnPage() {
  const clean = (text) => String(text || "").replace(/\s+/g, " ").replace(/\s*\*\s*$/, "").trim();
  const values = (nodes, attribute) => [...nodes].map((n) => n.getAttribute(attribute) || n.getAttribute("aria-label") || "").filter(Boolean);
  const questions = [];

  // Google Forms: one listitem per question, a heading with the text and a "Required question" marker.
  for (const item of document.querySelectorAll('[role="listitem"]')) {
    const heading = item.querySelector('[role="heading"]');
    if (!heading) continue;
    const title = clean((heading.querySelector("span") || heading).textContent);
    if (!title) continue;
    item.setAttribute("data-jarvis-q", "q" + questions.length);
    const required = !!heading.querySelector('[aria-label="Required question"]');
    let kind = "other", options = [];
    if (item.querySelector('[role="radio"]')) { kind = "choice"; options = values(item.querySelectorAll('[role="radio"]'), "data-value"); }
    else if (item.querySelector('[role="checkbox"]')) { kind = "checkbox"; options = values(item.querySelectorAll('[role="checkbox"]'), "data-answer-value"); }
    else if (item.querySelector('[role="listbox"]')) { kind = "dropdown"; options = values(item.querySelectorAll('[role="option"][data-value]:not([data-value=""])'), "data-value"); }
    else if (item.querySelector('input[type="date"]')) kind = "date";
    else if (item.querySelector('input[type="time"]')) kind = "time";
    else if (item.querySelector("textarea")) kind = "paragraph";
    else if (item.querySelector('input[type="text"], input[type="email"], input[type="tel"], input[type="url"], input[type="number"]')) kind = "text";
    questions.push({ id: "q" + questions.length, text: title, kind, options: [...new Set(options)], required });
  }
  const meta = document.querySelector('meta[property="og:description"], meta[name="description"]');
  const description = clean(meta && meta.content).slice(0, 1000);
  if (questions.length) {
    const title = document.querySelector('[role="heading"][aria-level="1"]');
    return { title: clean(title && title.textContent), description, questions };
  }

  // Any other form: visible, labelled fields. Radio buttons are grouped by name.
  const labelOf = (field) => {
    const byFor = field.id && document.querySelector(`label[for="${CSS.escape(field.id)}"]`);
    const around = field.closest("label");
    return clean((byFor && byFor.textContent) || field.getAttribute("aria-label") || (around && around.textContent) || field.placeholder || field.name);
  };
  const groups = new Set();
  for (const field of document.querySelectorAll("input, textarea, select")) {
    const type = (field.type || "").toLowerCase();
    if (["hidden", "password", "submit", "button", "reset", "image", "file"].includes(type) || !field.offsetParent) continue;
    if (type === "radio" || type === "checkbox") {
      if (!field.name || groups.has(field.name)) continue;
      groups.add(field.name);
      const all = [...document.querySelectorAll(`input[name="${CSS.escape(field.name)}"]`)];
      const legend = field.closest("fieldset") && field.closest("fieldset").querySelector("legend");
      all.forEach((input) => input.setAttribute("data-jarvis-q", "q" + questions.length));
      questions.push({ id: "q" + questions.length, text: clean(legend ? legend.textContent : field.name), kind: type === "radio" ? "choice" : "checkbox", options: all.map(labelOf).filter(Boolean), required: field.required });
      continue;
    }
    const text = labelOf(field);
    if (!text) continue;
    field.setAttribute("data-jarvis-q", "q" + questions.length);
    const kind = field.tagName === "SELECT" ? "dropdown" : field.tagName === "TEXTAREA" ? "paragraph" : type === "date" ? "date" : type === "time" ? "time" : "text";
    const options = field.tagName === "SELECT" ? [...field.options].filter((o) => o.value !== "").map((o) => clean(o.textContent)).filter(Boolean) : [];
    questions.push({ id: "q" + questions.length, text, kind, options, required: field.required });
  }
  return { title: document.title, description, questions: questions.slice(0, 80) };
}

// Filling runs in the page's own world (not the extension's isolated one), so the page's scripts
// see a value exactly as if their own code had set it. Never submits.
async function inPage(func, args) {
  const tab = await activeTab();
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id, frameIds: [formFrame] }, world: "MAIN", func, args });
  return result;
}

// Whether the question now shows this value.
function checkOnPage(question, value) {
  const same = (a, b) => String(a || "").replace(/\s+/g, " ").trim().toLowerCase() === String(b || "").replace(/\s+/g, " ").trim().toLowerCase();
  const nodes = [...document.querySelectorAll(`[data-jarvis-q="${CSS.escape(question)}"]`)];
  if (!nodes.length) return false;
  const root = nodes[0];
  const wanted = String(value).split(";").map((part) => part.trim()).filter(Boolean);
  const iso = (text) => {
    if (/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
    const parsed = new Date(text);
    const pad = (n) => String(n).padStart(2, "0");
    return isNaN(parsed) ? null : `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}`;
  };
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
    const date = root.querySelector('input[type="date"]');
    if (date) return date.value === iso(value);
    const text = root.querySelector('textarea, input:not([type="hidden"])');
    return !!text && same(text.value, value);
  }
  if (root.tagName === "SELECT") return same(root.selectedOptions[0] && root.selectedOptions[0].textContent, value) || same(root.value, value);
  if (root.type === "radio" || root.type === "checkbox") {
    const labelOf = (input) => (input.id && document.querySelector(`label[for="${CSS.escape(input.id)}"]`)?.textContent) || input.closest("label")?.textContent || input.value;
    const on = nodes.filter((input) => input.checked).map(labelOf);
    return on.length === wanted.length && wanted.every((w) => on.some((label) => same(label, w)));
  }
  if (root.type === "date") return root.value === iso(value);
  return same(root.value, value);
}

// Puts one answer into the page (types it, or clicks the matching options), then reads it back:
// it only counts as filled when the page really shows the value. When a text field won't take a
// scripted value, it is left focused with its text selected so Jarvis can type it with real
// keystrokes ("focused"). Returns where the field is on screen in physical pixels, for the cursor.
async function fillOnPage(question, value) {
  const wait = (ms) => new Promise((done) => setTimeout(done, ms));
  const same = (a, b) => String(a || "").replace(/\s+/g, " ").trim().toLowerCase() === String(b || "").replace(/\s+/g, " ").trim().toLowerCase();
  const nodes = [...document.querySelectorAll(`[data-jarvis-q="${CSS.escape(question)}"]`)];
  if (!nodes.length) return { filled: false, reason: "the field is gone (was the page reloaded?)" };
  const root = nodes[0];
  root.scrollIntoView({ block: "center", behavior: "smooth" });
  await wait(300);

  // A typed value, set the way a person's typing would be seen by the page's own scripts.
  const type = (field, text) => {
    const proto = field.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    field.focus();
    Object.getOwnPropertyDescriptor(proto, "value").set.call(field, text);
    field.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText", data: text }));
    field.dispatchEvent(new Event("change", { bubbles: true }));
    field.blur();
  };
  const iso = (text) => {
    if (/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
    const parsed = new Date(text);
    const pad = (n) => String(n).padStart(2, "0");
    return isNaN(parsed) ? null : `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}`; // local date, not UTC
  };
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
  const wanted = String(value).split(";").map((part) => part.trim()).filter(Boolean);
  let textField = null;
  let reason = null;

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
      const date = root.querySelector('input[type="date"]');
      textField = date || root.querySelector('textarea, input[type="text"], input[type="email"], input[type="tel"], input[type="url"], input[type="number"], input[type="time"]');
      if (date) { if (iso(value)) type(date, iso(value)); else reason = "not a date"; }
      else if (textField) type(textField, value);
      else reason = "no field to type in";
    }
  } else if (root.tagName === "SELECT") {
    const option = [...root.options].find((o) => same(o.textContent, value) || same(o.value, value));
    if (option) { root.value = option.value; root.dispatchEvent(new Event("change", { bubbles: true })); }
    else reason = "no option matches";
  } else if (root.type === "radio" || root.type === "checkbox") {
    const labelOf = (input) => (input.id && document.querySelector(`label[for="${CSS.escape(input.id)}"]`)?.textContent) || input.closest("label")?.textContent || input.value;
    for (const input of nodes) {
      const on = wanted.some((w) => same(labelOf(input), w));
      if (on !== input.checked && (on || input.type === "checkbox")) input.click();
    }
  } else if (root.type === "date") {
    if (iso(value)) type(root, iso(value)); else reason = "not a date";
  } else {
    textField = root;
    type(root, value);
  }

  await wait(350); // let the page react (some re-render a field, and a value that didn't take vanishes)
  const filled = reason === null && checkOnPage(question, value);
  if (!filled && reason === null && textField && textField.type !== "date") {
    textField.focus();
    if (textField.select) textField.select();
    return { filled: false, focused: true, reason: "the page didn't take a scripted value", ...where(textField) };
  }

  return { filled, reason: filled ? null : reason || "the page doesn't show it", ...where(root) };

  // The same check as the top-level one: this function runs in the page on its own.
  function checkOnPage(question, value) {
    const same = (a, b) => String(a || "").replace(/\s+/g, " ").trim().toLowerCase() === String(b || "").replace(/\s+/g, " ").trim().toLowerCase();
    const nodes = [...document.querySelectorAll(`[data-jarvis-q="${CSS.escape(question)}"]`)];
    if (!nodes.length) return false;
    const root = nodes[0];
    const wanted = String(value).split(";").map((part) => part.trim()).filter(Boolean);
    const iso = (text) => {
      if (/^\d{4}-\d{2}-\d{2}$/.test(text)) return text;
      const parsed = new Date(text);
      const pad = (n) => String(n).padStart(2, "0");
      return isNaN(parsed) ? null : `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())}`;
    };
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
      const date = root.querySelector('input[type="date"]');
      if (date) return date.value === iso(value);
      const text = root.querySelector('textarea, input:not([type="hidden"])');
      return !!text && same(text.value, value);
    }
    if (root.tagName === "SELECT") return same(root.selectedOptions[0] && root.selectedOptions[0].textContent, value) || same(root.value, value);
    if (root.type === "radio" || root.type === "checkbox") {
      const labelOf = (input) => (input.id && document.querySelector(`label[for="${CSS.escape(input.id)}"]`)?.textContent) || input.closest("label")?.textContent || input.value;
      const on = nodes.filter((input) => input.checked).map(labelOf);
      return on.length === wanted.length && wanted.every((w) => on.some((label) => same(label, w)));
    }
    if (root.type === "date") return root.value === iso(value);
    return same(root.value, value);
  }
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
