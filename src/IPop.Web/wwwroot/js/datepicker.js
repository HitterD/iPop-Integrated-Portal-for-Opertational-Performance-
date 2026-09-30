// Positioning, portal, outside-click and focus trap for the shared date/time
// pickers (Components/DatePickerCore.razor). One state object per open layer.
const MOBILE = "(max-width: 639.98px)";
const states = new WeakMap();

function place(root, layer) {
  const pop = layer.querySelector(".dp-pop");
  if (!pop) return;
  const mobile = window.matchMedia(MOBILE).matches;
  layer.classList.toggle("dp-sheet", mobile);
  if (mobile) {
    pop.style.left = pop.style.top = "";
    return;
  }
  const field = root.querySelector(".dp-field") || root;
  const r = field.getBoundingClientRect();
  const pw = pop.offsetWidth, ph = pop.offsetHeight;
  const vw = window.innerWidth, vh = window.innerHeight;
  let left = r.left;
  if (left + pw > vw - 8) left = Math.max(8, vw - pw - 8);
  let top = r.bottom + 6;
  let above = false;
  if (top + ph > vh - 8 && r.top - ph - 6 >= 8) { top = r.top - ph - 6; above = true; }
  else if (top + ph > vh - 8) top = Math.max(8, vh - ph - 8);
  pop.style.left = Math.round(left) + "px";
  pop.style.top = Math.round(top) + "px";
  pop.classList.toggle("above", above);
}

function centerTime(layer) {
  const list = layer.querySelector(".dp-times");
  if (!list) return;
  const target = list.querySelector(".dp-topt.sel") || list.querySelector(".dp-topt[tabindex='0']");
  if (target) list.scrollTop = target.offsetTop - list.clientHeight / 2 + target.offsetHeight / 2;
}

export function open(root, layer, dotnet) {
  dispose(layer);
  const home = layer.parentElement;
  document.body.appendChild(layer);
  layer.classList.add("dp-portal");
  const reposition = () => place(root, layer);
  const onDown = e => {
    if (root.contains(e.target) || layer.contains(e.target)) return;
    dotnet.invokeMethodAsync("OutsideClose").catch(() => { });
  };
  // Escape closes only the picker: stop it before it bubbles to document, where
  // Blazor handlers (drawers, modals) and the global shortcut would also see it.
  const onEsc = e => {
    if (e.key !== "Escape") return;
    e.stopPropagation(); e.preventDefault();
    dotnet.invokeMethodAsync("EscapeClose").catch(() => { });
  };
  root.addEventListener("keydown", onEsc);
  layer.addEventListener("keydown", onEsc);
  const onKey = e => {
    // Grid / time-list navigation keys must not scroll the page behind the popover.
    if (/^(Arrow(Up|Down|Left|Right)|Page(Up|Down)|Home|End| )$/.test(e.key)
        && e.target.closest && e.target.closest(".dp-grid, .dp-times")) e.preventDefault();
    if (e.key !== "Tab") return;
    const pop = layer.querySelector(".dp-pop");
    if (!pop || !pop.contains(document.activeElement)) return;
    // Trap: cycle through the tabbable controls inside the popover.
    const items = [...pop.querySelectorAll("button:not([disabled]):not([tabindex='-1']), [tabindex='0']")]
      .filter(el => el.offsetParent !== null);
    if (items.length === 0) return;
    const i = items.indexOf(document.activeElement);
    e.preventDefault();
    const next = e.shiftKey ? (i <= 0 ? items.length - 1 : i - 1) : (i + 1) % items.length;
    items[next].focus();
  };
  document.addEventListener("mousedown", onDown, true);
  document.addEventListener("touchstart", onDown, { capture: true, passive: true });
  layer.addEventListener("keydown", onKey);
  window.addEventListener("resize", reposition);
  window.addEventListener("scroll", reposition, true);
  states.set(layer, { home, root, onDown, onKey, onEsc, reposition });
  reposition();
  centerTime(layer);
  requestAnimationFrame(() => { reposition(); centerTime(layer); });
}

export function focusIn(layer, selector) {
  const el = layer.querySelector(selector);
  if (!el) return;
  el.focus({ preventScroll: true });
  const list = el.closest(".dp-times");
  if (list) {
    const top = el.offsetTop, bottom = top + el.offsetHeight;
    if (top < list.scrollTop) list.scrollTop = top - 4;
    else if (bottom > list.scrollTop + list.clientHeight) list.scrollTop = bottom - list.clientHeight + 4;
  }
}

export function close(layer, root, focusEl) {
  dispose(layer);
  if (focusEl && typeof focusEl.focus === "function") {
    try { focusEl.focus({ preventScroll: true }); } catch { }
  }
}

export function dispose(layer) {
  const s = layer && states.get(layer);
  if (!s) return;
  states.delete(layer);
  document.removeEventListener("mousedown", s.onDown, true);
  document.removeEventListener("touchstart", s.onDown, true);
  layer.removeEventListener("keydown", s.onKey);
  layer.removeEventListener("keydown", s.onEsc);
  if (s.root) s.root.removeEventListener("keydown", s.onEsc);
  window.removeEventListener("resize", s.reposition);
  window.removeEventListener("scroll", s.reposition, true);
  layer.classList.remove("dp-portal", "dp-sheet");
  // Put the layer back in its original slot (last child of the picker root) so
  // Blazor's view of the DOM stays consistent; if the root is gone, drop it.
  if (s.home && s.home.isConnected) s.home.appendChild(layer);
  else layer.remove();
}
