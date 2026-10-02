// Chat keyboard accessibility (ES module, loaded by Pages/Chat.Accessibility.cs via import()).
//
// One delegated keydown + one capture-phase contextmenu listener on the .messages list:
//  - Roving tabindex: exactly one message row is tabbable (tabindex=0), the rest are -1, so Tab
//    enters the list once and Arrow/Home/End/PageUp/PageDown move between rows.
//  - Shift+F10 / ContextMenu on a focused row opens the existing message menu (Chat.ShowCtx),
//    anchored to the row's bubble rect instead of a pointer position.
//  - Editable targets (inline edit textarea, poll inputs, contenteditable) are never touched,
//    so native caret/selection shortcuts and the native context menu keep working.
// Pure helpers are exported for tests (tests/chat-accessibility.test.mjs).

const ROW = ".msg[data-msg-id]";
const KBD_CTX_WINDOW_MS = 1000;
const bound = new Map();

export function isEditable(el) {
  if (!el || !el.tagName) return false;
  const tag = el.tagName;
  if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
  if (el.isContentEditable) return true;
  return !!(el.closest && el.closest("[contenteditable='true'], [contenteditable=''], .jodit-wysiwyg"));
}

/// Shift+F10 or the dedicated Menu/ContextMenu key; never with Ctrl/Alt/Meta (those are OS or browser chords).
export function isContextKey(e) {
  if (!e || e.ctrlKey || e.altKey || e.metaKey) return false;
  if (e.key === "ContextMenu" || e.key === "Apps") return true;
  return e.key === "F10" && !!e.shiftKey;
}

/// Row-navigation key -> movement. null when the key is not ours (modifiers keep browser meaning).
export function navDelta(e) {
  if (!e || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return null;
  switch (e.key) {
    case "ArrowUp": return -1;
    case "ArrowDown": return 1;
    case "PageUp": return -5;
    case "PageDown": return 5;
    case "Home": return "first";
    case "End": return "last";
    default: return null;
  }
}

export function targetIndex(current, count, delta) {
  if (count <= 0) return -1;
  if (delta === "first") return 0;
  if (delta === "last") return count - 1;
  const from = current < 0 ? count - 1 : current;
  return Math.max(0, Math.min(count - 1, from + delta));
}

/// Menu anchor for a keyboard open: just inside the bubble's leading/trailing edge, below its
/// top line, clamped into the viewport. ipop.placeContextMenu still flips near the edges.
export function anchorFromRect(rect, sent, vw, vh) {
  const pad = 8;
  const x = sent ? rect.right - 16 : rect.left + 16;
  const y = Math.min(rect.bottom, rect.top + 28);
  return {
    x: Math.round(Math.max(pad, Math.min(vw - pad, x))),
    y: Math.round(Math.max(pad, Math.min(vh - pad, y))),
  };
}

function rows(list) {
  return Array.from(list.querySelectorAll(ROW));
}

function rowOf(list, el) {
  const row = el && el.closest ? el.closest(ROW) : null;
  return row && list.contains(row) ? row : null;
}

function setActive(list, row) {
  for (const r of list.querySelectorAll(ROW + "[tabindex='0']")) if (r !== row) r.setAttribute("tabindex", "-1");
  if (row) row.setAttribute("tabindex", "0");
}

/// Keeps one tabbable row: the focused one if any, else the previously active one, else the newest.
function ensureTabStop(list) {
  const all = rows(list);
  for (const r of all) if (!r.hasAttribute("tabindex")) r.setAttribute("tabindex", "-1");
  if (all.length === 0) return;
  if (all.some(r => r.getAttribute("tabindex") === "0")) return;
  all[all.length - 1].setAttribute("tabindex", "0");
}

function modalOpen() {
  return !!document.querySelector("[aria-modal='true'], .syno-modal-overlay, .modal-overlay.on");
}

function openFor(state, row) {
  const id = row.getAttribute("data-msg-id");
  if (!id) return;
  const bubble = row.querySelector(".bubble") || row;
  const a = anchorFromRect(bubble.getBoundingClientRect(), row.classList.contains("sent"), innerWidth, innerHeight);
  state.suppressUntil = performance.now() + KBD_CTX_WINDOW_MS;
  state.dotnet.invokeMethodAsync("OnA11yContextKey", id, a.x, a.y).catch(() => { });
}

export function bind(list, dotnet) {
  if (!list) return;
  for (const old of [...bound.keys()]) if (old !== list || !old.isConnected) unbind(old);
  unbind(list);
  const state = { dotnet, suppressUntil: 0 };

  state.keydown = e => {
    if (e.defaultPrevented || e.isComposing || isEditable(e.target)) return;
    const row = rowOf(list, e.target);
    if (!row) return;
    if (isContextKey(e)) {
      // Buttons inside the row (hover tools) open the same menu for their row.
      e.preventDefault();
      openFor(state, row);
      return;
    }
    // Arrow keys only when the row itself has focus: inside it, buttons/links keep theirs.
    if (e.target !== row) return;
    const d = navDelta(e);
    if (d === null) return;
    const all = rows(list);
    const next = all[targetIndex(all.indexOf(row), all.length, d)];
    if (!next || next === row) { e.preventDefault(); return; }
    e.preventDefault();
    setActive(list, next);
    next.focus({ preventScroll: false });
    if (next.scrollIntoView) next.scrollIntoView({ block: "nearest" });
  };

  // Keyboard-generated contextmenu (Menu key fires it after keydown on Windows) would reach
  // Blazor's @oncontextmenu with element-origin coordinates. Swallow it: keydown already opened
  // the menu at the bubble anchor. Only that one synthetic event is eaten: a pointer-originated
  // contextmenu (right button, or any preceding pointerdown) always passes and disarms the
  // guard, and closing the menu disarms it too (clearSuppress, via restoreRow).
  state.contextmenu = e => {
    if (isEditable(e.target)) return;
    if (!isSuppressed(state)) return;
    state.suppressUntil = 0;
    if (isPointerContextMenu(e)) return;
    e.preventDefault();
    e.stopPropagation();
  };

  state.pointerdown = () => { state.suppressUntil = 0; };

  state.focusin = e => {
    const row = rowOf(list, e.target);
    if (row) setActive(list, row);
  };

  state.observer = new MutationObserver(() => ensureTabStop(list));
  state.observer.observe(list, { childList: true });
  ensureTabStop(list);

  list.addEventListener("keydown", state.keydown);
  list.addEventListener("contextmenu", state.contextmenu, true);
  list.addEventListener("focusin", state.focusin);
  // Document-level: the menu backdrop sits over the list, and any pointer press anywhere means
  // the next contextmenu is a real one.
  document.addEventListener("pointerdown", state.pointerdown, true);
  bound.set(list, state);
}

function isSuppressed(state) {
  return state.suppressUntil > 0 && performance.now() < state.suppressUntil;
}

/// A right-click reports button 2; keyboard-generated contextmenu reports 0 (and, where the
/// event is a PointerEvent, pointerType "" / pointerId -1).
export function isPointerContextMenu(e) {
  if (!e) return false;
  if (e.button === 2) return true;
  return typeof e.pointerType === "string" && e.pointerType !== "";
}

/// Disarms the keyboard contextmenu guard (menu closed / focus restored).
export function clearSuppress(list) {
  const state = list && bound.get(list);
  if (state) state.suppressUntil = 0;
}

export function unbind(list) {
  const state = list && bound.get(list);
  if (!state) return;
  list.removeEventListener("keydown", state.keydown);
  list.removeEventListener("contextmenu", state.contextmenu, true);
  list.removeEventListener("focusin", state.focusin);
  document.removeEventListener("pointerdown", state.pointerdown, true);
  state.observer.disconnect();
  bound.delete(list);
  releaseMenu();
}

/// Bubble anchor for a row by id (keyboard activation of the hover "More" button, whose click
/// event carries 0,0 coordinates). null when the row is gone.
export function anchorForRow(list, msgId) {
  if (!list || !msgId) return null;
  const row = list.querySelector(`.msg[data-msg-id="${CSS.escape(msgId)}"]`);
  if (!row) return null;
  const bubble = row.querySelector(".bubble") || row;
  return anchorFromRect(bubble.getBoundingClientRect(), row.classList.contains("sent"), innerWidth, innerHeight);
}

/// Re-anchors an already rendered menu to a row's bubble (keyboard-activated "More" button).
/// Uses the shared ipop.placeContextMenu for edge flipping when present (read-only reuse).
export function placeAtRow(list, menu, msgId) {
  const a = anchorForRow(list, msgId);
  if (!a || !menu) return false;
  const ipop = globalThis.ipop;
  if (ipop && typeof ipop.placeContextMenu === "function") ipop.placeContextMenu(menu, a.x, a.y);
  else { menu.style.left = a.x + "px"; menu.style.top = a.y + "px"; }
  return true;
}

let menuEscape = null;

/// Removes the keyboard menu's document Escape listener (also called by unbind / on close).
export function releaseMenu() {
  if (!menuEscape) return;
  document.removeEventListener("keydown", menuEscape, true);
  menuEscape = null;
}

/// Moves focus into a keyboard-opened context menu and wires Arrow/Home/End between its items.
/// Escape is handled explicitly: a capture-phase document listener (runs before the layout's
/// bubbling Escape shortcut, so one key closes exactly one layer) asks the page to close the
/// menu via OnA11yMenuEscape; the page then restores focus to the row.
export function focusMenu(menu, dotnet) {
  if (!menu || !menu.querySelectorAll) return false;
  const items = () => Array.from(menu.querySelectorAll("button:not([disabled])"));
  const first = items()[0];
  if (!first) return false;
  releaseMenu();
  if (dotnet) {
    menuEscape = e => {
      if (e.key !== "Escape" || e.isComposing) return;
      if (!menu.isConnected) { releaseMenu(); return; }
      e.preventDefault();
      e.stopImmediatePropagation();
      releaseMenu();
      for (const s of bound.values()) s.suppressUntil = 0;
      dotnet.invokeMethodAsync("OnA11yMenuEscape").catch(() => { });
    };
    document.addEventListener("keydown", menuEscape, true);
  }
  menu.setAttribute("role", "menu");
  for (const b of items()) { b.setAttribute("role", "menuitem"); b.setAttribute("tabindex", "-1"); }
  if (!menu._a11yKeys) {
    menu._a11yKeys = e => {
      const list = items();
      const i = list.indexOf(document.activeElement);
      let n = -1;
      if (e.key === "ArrowDown") n = (i + 1) % list.length;
      else if (e.key === "ArrowUp") n = (i - 1 + list.length) % list.length;
      else if (e.key === "Home") n = 0;
      else if (e.key === "End") n = list.length - 1;
      else if (e.key === "Tab") { e.preventDefault(); n = e.shiftKey ? (i - 1 + list.length) % list.length : (i + 1) % list.length; }
      if (n < 0) return;
      e.preventDefault();
      list[n].focus();
    };
    menu.addEventListener("keydown", menu._a11yKeys);
  }
  first.focus();
  return true;
}

/// Returns focus to a message row after its keyboard-opened menu closed — only when focus was
/// actually lost (body / detached node). An action that moved focus on purpose (reply/edit ->
/// composer, a confirm dialog) wins.
export function restoreRow(list, msgId) {
  releaseMenu();
  clearSuppress(list);
  if (!list || !msgId) return false;
  const active = document.activeElement;
  if (active && active !== document.body && document.contains(active)) return false;
  if (modalOpen()) return false;
  const row = list.querySelector(`.msg[data-msg-id="${CSS.escape(msgId)}"]`);
  if (!row) return false;
  setActive(list, row);
  row.focus({ preventScroll: true });
  return true;
}
