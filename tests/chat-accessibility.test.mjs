// Run with: node tests/chat-accessibility.test.mjs
// Exercises the real module's pure helpers plus bind/unbind against a tiny fake DOM.
import assert from "node:assert/strict";
import { pathToFileURL } from "node:url";
import path from "node:path";

// Minimal globals the module touches at call time.
globalThis.CSS = { escape: s => String(s).replace(/"/g, '\\"') };
globalThis.innerWidth = 1000;
globalThis.innerHeight = 800;
globalThis.performance ??= { now: () => Date.now() };

const mod = await import(pathToFileURL(path.join(import.meta.dirname, "../src/IPop.Web/wwwroot/js/chat-accessibility.js")).href);
const { isEditable, isContextKey, navDelta, targetIndex, anchorFromRect, bind, unbind, restoreRow, focusMenu, clearSuppress, isPointerContextMenu } = mod;

// ---- pure helpers ----
const k = (key, extra = {}) => ({ key, shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, ...extra });

assert.equal(isContextKey(k("F10", { shiftKey: true })), true);
assert.equal(isContextKey(k("ContextMenu")), true);
assert.equal(isContextKey(k("F10")), false, "plain F10 is the browser menu bar");
assert.equal(isContextKey(k("F10", { shiftKey: true, ctrlKey: true })), false);
assert.equal(isContextKey(k("ContextMenu", { altKey: true })), false);

assert.equal(navDelta(k("ArrowUp")), -1);
assert.equal(navDelta(k("ArrowDown")), 1);
assert.equal(navDelta(k("Home")), "first");
assert.equal(navDelta(k("End")), "last");
assert.equal(navDelta(k("PageDown")), 5);
assert.equal(navDelta(k("ArrowUp", { shiftKey: true })), null, "Shift+Arrow keeps native selection");
assert.equal(navDelta(k("Home", { ctrlKey: true })), null);
assert.equal(navDelta(k("a")), null);

assert.equal(targetIndex(2, 5, -1), 1);
assert.equal(targetIndex(0, 5, -1), 0, "clamps at the top");
assert.equal(targetIndex(4, 5, 5), 4, "clamps at the bottom");
assert.equal(targetIndex(-1, 5, -1), 3, "unknown current starts from newest");
assert.equal(targetIndex(2, 5, "first"), 0);
assert.equal(targetIndex(2, 5, "last"), 4);
assert.equal(targetIndex(0, 0, 1), -1);

const rect = { left: 100, right: 400, top: 200, bottom: 260 };
assert.deepEqual(anchorFromRect(rect, false, 1000, 800), { x: 116, y: 228 });
assert.deepEqual(anchorFromRect(rect, true, 1000, 800), { x: 384, y: 228 });
assert.deepEqual(anchorFromRect({ left: -50, right: 2000, top: 900, bottom: 950 }, true, 1000, 800), { x: 992, y: 792 }, "clamped into viewport");
assert.deepEqual(anchorFromRect({ left: 10, right: 50, top: 10, bottom: 20 }, false, 1000, 800), { x: 26, y: 20 }, "short bubble anchors at its bottom");

// ---- fake DOM ----
class El {
  constructor(tag, attrs = {}, classes = []) {
    this.tagName = tag.toUpperCase(); this.attrs = { ...attrs }; this.children = []; this.parent = null;
    this.classList = { contains: c => classes.includes(c) }; this.listeners = {}; this.isContentEditable = false;
  }
  append(...c) { for (const x of c) { x.parent = this; this.children.push(x); } return this; }
  getAttribute(n) { return n in this.attrs ? this.attrs[n] : null; }
  setAttribute(n, v) { this.attrs[n] = String(v); }
  hasAttribute(n) { return n in this.attrs; }
  contains(o) { for (let x = o; x; x = x.parent) if (x === this) return true; return false; }
  *walk() { for (const c of this.children) { yield c; yield* c.walk(); } }
  matches(sel) {
    if (sel === ".msg[data-msg-id]") return this.classList.contains("msg") && this.hasAttribute("data-msg-id");
    if (sel === ".msg[data-msg-id][tabindex='0']") return this.matches(".msg[data-msg-id]") && this.attrs.tabindex === "0";
    let m = /^\.msg\[data-msg-id="(.+)"\]$/.exec(sel);
    if (m) return this.classList.contains("msg") && this.attrs["data-msg-id"] === m[1];
    if (sel === ".bubble") return this.classList.contains("bubble");
    if (sel === "button:not([disabled])") return this.tagName === "BUTTON" && !this.hasAttribute("disabled");
    return false;
  }
  querySelectorAll(sel) { return [...this.walk()].filter(e => e.matches(sel)); }
  querySelector(sel) { return this.querySelectorAll(sel)[0] ?? null; }
  closest(sel) {
    if (sel.includes("contenteditable")) return null;
    for (let x = this; x; x = x.parent) if (x.matches && x.matches(sel)) return x;
    return null;
  }
  addEventListener(t, f) { (this.listeners[t] ??= []).push(f); }
  removeEventListener(t, f) { this.listeners[t] = (this.listeners[t] ?? []).filter(x => x !== f); }
  dispatch(t, e) {
    // bubble from target up to this listener owner (list) only
    for (const f of this.listeners[t] ?? []) f(e);
  }
  focus() { globalThis.document.activeElement = this; this.focused = (this.focused ?? 0) + 1; }
  scrollIntoView() { }
  getBoundingClientRect() { return { left: 100, right: 400, top: 100, bottom: 160 }; }
}

const body = new El("body");
const docListeners = [];
globalThis.document = {
  activeElement: body, body, contains: el => body.contains(el), querySelector: () => null,
  addEventListener: (t, f, cap) => docListeners.push({ t, f, cap }),
  removeEventListener: (t, f) => { const i = docListeners.findIndex(l => l.t === t && l.f === f); if (i >= 0) docListeners.splice(i, 1); },
};
const docKey = e => { for (const l of [...docListeners]) if (l.t === "keydown") l.f(e); return e; };
globalThis.MutationObserver = class { observe() { } disconnect() { } };

function makeList(n) {
  const list = new El("div");
  const rows = [];
  for (let i = 0; i < n; i++) {
    const row = new El("div", { "data-msg-id": "id" + i }, ["msg", i % 2 ? "sent" : "recv"]);
    const bubble = new El("div", {}, ["bubble"]);
    const btn = new El("button");
    row.append(bubble, btn);
    rows.push(row);
    list.append(row);
  }
  body.children = []; body.append(list);
  return { list, rows };
}
function key(list, target, keyName, extra = {}) {
  const e = { ...k(keyName, extra), target, isComposing: false, defaultPrevented: false, prevented: false,
    preventDefault() { this.prevented = true; this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; } };
  list.dispatch("keydown", e);
  return e;
}

// roving tabindex
{
  const { list, rows } = makeList(4);
  const calls = [];
  const dotnet = { invokeMethodAsync: (...a) => { calls.push(a); return Promise.resolve(); } };
  bind(list, dotnet);
  assert.deepEqual(rows.map(r => r.getAttribute("tabindex")), ["-1", "-1", "-1", "0"], "newest row is the single tab stop");

  let e = key(list, rows[3], "ArrowUp");
  assert.ok(e.prevented);
  assert.equal(document.activeElement, rows[2]);
  assert.deepEqual(rows.map(r => r.getAttribute("tabindex")), ["-1", "-1", "0", "-1"]);

  key(list, rows[2], "Home");
  assert.equal(document.activeElement, rows[0]);
  e = key(list, rows[0], "ArrowUp");
  assert.ok(e.prevented, "edge is swallowed so the list does not scroll away");
  assert.equal(document.activeElement, rows[0]);

  // arrows inside the row (on a button) are left alone
  e = key(list, rows[0].children[1], "ArrowDown");
  assert.equal(e.prevented, false);

  // Shift+F10 on the row and on a button inside it open the menu with a bubble anchor
  e = key(list, rows[1], "F10", { shiftKey: true });
  assert.ok(e.prevented);
  assert.deepEqual(calls.at(-1), ["OnA11yContextKey", "id1", 384, 128], "sent row anchors at trailing edge");
  key(list, rows[0].children[1], "ContextMenu");
  assert.deepEqual(calls.at(-1), ["OnA11yContextKey", "id0", 116, 128]);

  // the keyboard-generated contextmenu event that follows is swallowed; a later mouse one is not
  const ce = { target: rows[0], prevented: false, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } };
  list.dispatch("contextmenu", ce);
  assert.ok(ce.prevented && ce.stopped);
  const mouse = { target: rows[0], prevented: false, preventDefault() { this.prevented = true; }, stopPropagation() { } };
  list.dispatch("contextmenu", mouse);
  assert.equal(mouse.prevented, false, "only the one synthetic event is eaten");

  // editable targets keep every native shortcut
  const ta = new El("textarea");
  rows[2].append(ta);
  const before = calls.length;
  e = key(list, ta, "F10", { shiftKey: true });
  assert.equal(e.prevented, false);
  e = key(list, ta, "Home");
  assert.equal(e.prevented, false);
  assert.equal(calls.length, before);
  assert.equal(isEditable(ta), true);
  assert.equal(isEditable(rows[0]), false);

  // IME composition is never interpreted
  const ime = { ...k("ArrowDown"), target: rows[0], isComposing: true, preventDefault() { throw new Error("no"); } };
  list.dispatch("keydown", ime);

  // Regression: a real right-click right after a keyboard open/Escape must never be eaten.
  const cm = (extra = {}) => ({ target: rows[0], prevented: false, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...extra });
  const fire = ev => { list.dispatch("contextmenu", ev); return ev; };

  key(list, rows[1], "F10", { shiftKey: true });          // Shift+F10: no synthetic contextmenu follows
  assert.equal(fire(cm({ button: 2, pointerType: "mouse" })).prevented, false, "right-click (button 2) passes inside the window");

  key(list, rows[1], "F10", { shiftKey: true });
  const pd = docListeners.find(l => l.t === "pointerdown");
  assert.ok(pd && pd.cap, "pointerdown disarm listener is capture-phase on document");
  pd.f({});                                                 // any pointer press disarms
  assert.equal(fire(cm()).prevented, false, "contextmenu after a pointer press passes");

  key(list, rows[1], "F10", { shiftKey: true });
  clearSuppress(list);                                      // menu closed
  assert.equal(fire(cm()).prevented, false, "guard cleared on close");

  key(list, rows[1], "F10", { shiftKey: true });
  restoreRow(list, "id1");                                  // focus restore also disarms
  assert.equal(fire(cm()).prevented, false, "guard cleared on restore");

  // Escape through the keyboard menu disarms the guard too
  key(list, rows[1], "F10", { shiftKey: true });
  const kmenu = new El("div"); kmenu.isConnected = true; kmenu.append(new El("button")); body.append(kmenu);
  focusMenu(kmenu, dotnet);
  docKey({ key: "Escape", isComposing: false, preventDefault() { }, stopImmediatePropagation() { } });
  assert.deepEqual(calls.at(-1), ["OnA11yMenuEscape"]);
  assert.equal(fire(cm()).prevented, false, "right-click right after Escape opens the menu");

  // the Menu key's own synthetic event is still eaten exactly once
  key(list, rows[1], "ContextMenu");
  assert.equal(fire(cm({ button: 0, pointerType: "" })).prevented, true);
  assert.equal(fire(cm({ button: 0, pointerType: "" })).prevented, false);

  assert.equal(isPointerContextMenu({ button: 2 }), true);
  assert.equal(isPointerContextMenu({ button: 0, pointerType: "touch" }), true, "long-press");
  assert.equal(isPointerContextMenu({ button: 0, pointerType: "" }), false);
  assert.equal(isPointerContextMenu({ button: 0 }), false);

  unbind(list);
  assert.equal(docListeners.filter(l => l.t === "pointerdown").length, 0);
  assert.equal((list.listeners.keydown ?? []).length, 0);
  assert.equal((list.listeners.contextmenu ?? []).length, 0);
  assert.equal((list.listeners.focusin ?? []).length, 0);
}

// restoreRow only when focus was lost
{
  const { list, rows } = makeList(3);
  document.activeElement = body;
  assert.equal(restoreRow(list, "id1"), true);
  assert.equal(document.activeElement, rows[1]);
  const composer = new El("textarea"); body.append(composer);
  document.activeElement = composer;
  assert.equal(restoreRow(list, "id0"), false, "deliberate focus (composer) wins");
  document.activeElement = body;
  assert.equal(restoreRow(list, "missing"), false);
  document.querySelector = () => ({});
  assert.equal(restoreRow(list, "id0"), false, "a modal keeps focus");
  document.querySelector = () => null;
}

// focusMenu: roles + first item focus + arrow cycling
{
  const menu = new El("div");
  const a = new El("button"), b = new El("button"), c = new El("button", { disabled: "" });
  menu.append(a, b, c); body.append(menu);
  assert.equal(focusMenu(menu), true);
  assert.equal(menu.getAttribute("role"), "menu");
  assert.equal(a.getAttribute("role"), "menuitem");
  assert.equal(document.activeElement, a);
  const ev = name => ({ key: name, shiftKey: false, preventDefault() { } });
  menu.dispatch("keydown", ev("ArrowDown")); assert.equal(document.activeElement, b);
  menu.dispatch("keydown", ev("ArrowDown")); assert.equal(document.activeElement, a, "wraps, skips disabled");
  menu.dispatch("keydown", ev("End")); assert.equal(document.activeElement, b);
  assert.equal(focusMenu(new El("div")), false);
}

// Escape in a keyboard-opened menu: capture listener closes via the page, once, and stops
// the layout's global Escape from also running.
{
  const menu = new El("div"); menu.isConnected = true;
  menu.append(new El("button")); body.append(menu);
  const calls = [];
  const dotnet = { invokeMethodAsync: (...a) => { calls.push(a); return Promise.resolve(); } };
  assert.equal(focusMenu(menu, dotnet), true);
  assert.equal(docListeners.filter(l => l.t === "keydown").length, 1);
  assert.equal(docListeners[0].cap, true, "capture phase runs before the layout shortcut");

  const other = docKey({ key: "a", isComposing: false, preventDefault() { throw new Error("no"); }, stopImmediatePropagation() { throw new Error("no"); } });
  assert.equal(calls.length, 0);

  const esc = docKey({ key: "Escape", isComposing: false, preventDefault() { this.p = true; }, stopImmediatePropagation() { this.s = true; } });
  assert.ok(esc.p && esc.s);
  assert.deepEqual(calls, [["OnA11yMenuEscape"]]);
  assert.equal(docListeners.length, 0, "one-shot: released after closing");
  docKey({ key: "Escape", isComposing: false, preventDefault() { }, stopImmediatePropagation() { } });
  assert.equal(calls.length, 1, "a second Escape goes to the page's normal handling");

  // re-focusing replaces (never stacks) the listener; a detached menu just releases it
  focusMenu(menu, dotnet); focusMenu(menu, dotnet);
  assert.equal(docListeners.length, 1);
  menu.isConnected = false;
  const late = docKey({ key: "Escape", isComposing: false, preventDefault() { this.p = true; }, stopImmediatePropagation() { } });
  assert.equal(late.p, undefined);
  assert.equal(docListeners.length, 0);
  assert.equal(calls.length, 1);

  // restoreRow / unbind also release a still-armed listener
  menu.isConnected = true;
  focusMenu(menu, dotnet);
  restoreRow(null, null);
  assert.equal(docListeners.length, 0);
}

console.log("chat-accessibility: all tests passed");
