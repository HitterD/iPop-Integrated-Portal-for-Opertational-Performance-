// Free drag + resize for the in-call window and the minimized pill (GlobalCallOverlay.razor).
// Blazor calls attach(el, kind) whenever the overlay element appears or is replaced
// ("window" <-> "pill", 1:1 <-> group) and detach() when it goes away. Only one overlay is
// live at a time, so the module tracks a single attachment.
//
// Position and size live in inline styles written here; the Razor markup renders no style
// attribute on these elements, so a Blazor re-render never overwrites them. Until the user
// moves or resizes something the stylesheet layout (bottom-right, mobile full-width) applies.
//
// Persisted per browser in localStorage: { pos: { ax, x, ay, y }, size: { w, h } }.
// pos is an anchor: the element edge nearest the viewport edge (ax "l"|"r", ay "t"|"b") and
// its distance from it. Pill and window share it, so switching keeps the anchored corner.
window.ipopCallDrag = (() => {
  const KEY = "ipop.callOverlay.v1";
  const THRESHOLD = 4;      // px of movement before a press becomes a drag
  const EDGE = 8;           // min gap to the viewport edges
  const MIN_W = 300, MIN_H = 320;
  const IGNORE = "button:not(.callx-pill-open), input, select, textarea, a, label, [data-no-drag]";

  let cur = null;           // { el, kind, cleanup[] }
  let suppressClick = false;

  const load = () => {
    try { return JSON.parse(localStorage.getItem(KEY)) || {}; } catch { return {}; }
  };
  const save = (s) => { try { localStorage.setItem(KEY, JSON.stringify(s)); } catch { /* private mode */ } };

  /// Usable viewport box: minus the edge gap and, on phones, the bottom tab bar.
  function bounds() {
    const vw = document.documentElement.clientWidth || window.innerWidth;
    const vh = window.innerHeight;
    let bottomInset = 0;
    const bar = document.querySelector(".m-tabbar");
    if (bar && !document.body.classList.contains("m-hide-tabbar")) {
      const r = bar.getBoundingClientRect();
      if (r.height > 0 && getComputedStyle(bar).display !== "none") bottomInset = Math.max(0, vh - r.top);
    }
    return { left: EDGE, top: EDGE, right: vw - EDGE, bottom: vh - bottomInset - EDGE, vw, vh };
  }

  function sizeLimits(b) {
    const availW = b.right - b.left, availH = b.bottom - b.top;
    const maxW = Math.max(Math.min(720, b.vw * 0.9, availW), Math.min(MIN_W, availW));
    const maxH = Math.max(Math.min(b.vh * 0.9, availH), Math.min(MIN_H, availH));
    return { minW: Math.min(MIN_W, maxW), minH: Math.min(MIN_H, maxH), maxW, maxH };
  }

  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  /// Anchor for a rect: the nearest horizontal / vertical viewport edge and the gap to it.
  function anchorOf(rect, b) {
    const ax = rect.left + rect.width / 2 < b.vw / 2 ? "l" : "r";
    const ay = rect.top + rect.height / 2 < b.vh / 2 ? "t" : "b";
    return {
      ax, x: Math.round(ax === "l" ? rect.left : b.vw - rect.right),
      ay, y: Math.round(ay === "t" ? rect.top : b.vh - rect.bottom),
    };
  }

  function setFree(el) {
    el.dataset.free = "";
    el.style.right = "auto";
    el.style.bottom = "auto";
  }

  /// Apply the stored size (window only) within the current viewport limits.
  function applySize(el, size, b) {
    if (!size) return;
    const lim = sizeLimits(b);
    el.dataset.sized = "";
    el.style.width = Math.round(clamp(size.w, lim.minW, lim.maxW)) + "px";
    el.style.height = Math.round(clamp(size.h, lim.minH, lim.maxH)) + "px";
  }

  /// Place el so its anchored corner sits at pos, then clamp it fully inside the viewport.
  function place(el, pos, b) {
    const avail = b.bottom - b.top;
    el.style.maxHeight = avail + "px";
    const r = el.getBoundingClientRect();
    const w = r.width, h = Math.min(r.height, avail);
    let left = pos.ax === "l" ? pos.x : b.vw - pos.x - w;
    let top = pos.ay === "t" ? pos.y : b.vh - pos.y - h;
    left = clamp(left, b.left, Math.max(b.left, b.right - w));
    top = clamp(top, b.top, Math.max(b.top, b.bottom - h));
    el.style.left = Math.round(left) + "px";
    el.style.top = Math.round(top) + "px";
  }

  /// Switch el from the stylesheet layout to explicit coordinates, keeping where it is now.
  function freeze(el) {
    if (el.dataset.free !== undefined) return;
    el.style.animation = "none";
    const r = el.getBoundingClientRect();
    // Mobile layout stretches the window with left+right; pin today's width first.
    if (cur?.kind === "window" && !el.style.width) el.style.width = Math.round(r.width) + "px";
    setFree(el);
    el.style.left = Math.round(r.left) + "px";
    el.style.top = Math.round(r.top) + "px";
  }

  const PROPS = ["left", "top", "right", "bottom", "width", "height", "maxHeight"];

  /// Back to the stylesheet layout (bottom-right; full width on phones).
  function unfree(el) {
    PROPS.forEach((p) => { el.style[p] = ""; });
    delete el.dataset.free;
    delete el.dataset.sized;
  }

  /// Re-apply the persisted state (on attach, viewport resize and content size changes).
  /// Starts from the stylesheet layout each time so a rotated / resized viewport gets the
  /// default width back (phones) before the saved size and anchor are clamped onto it.
  function restore() {
    if (!cur) return;
    const { el, kind } = cur;
    const s = load();
    const size = kind === "window" ? s.size : null;
    if (!s.pos && !size) { if (el.dataset.free !== undefined) unfree(el); return; }
    // No slide-in for a placed overlay: its transform would skew the measurements.
    el.style.animation = "none";
    unfree(el);
    const b = bounds();
    const def = el.getBoundingClientRect();
    // Pin the default width, or a phone's left+right stretch collapses to shrink-to-fit.
    if (kind === "window") el.style.width = Math.round(def.width) + "px";
    applySize(el, size, b);
    const pos = s.pos || { ax: "r", x: Math.round(b.vw - def.right), ay: "b", y: Math.round(b.vh - def.bottom) };
    setFree(el);
    place(el, pos, b);
  }

  function startDrag(e, el) {
    const sx = e.clientX, sy = e.clientY, id = e.pointerId;
    let dragging = false, ox = 0, oy = 0, w = 0, h = 0;

    const move = (ev) => {
      if (ev.pointerId !== id) return;
      const dx = ev.clientX - sx, dy = ev.clientY - sy;
      if (!dragging) {
        if (Math.abs(dx) < THRESHOLD && Math.abs(dy) < THRESHOLD) return;
        dragging = true;
        cur.busy = true;
        freeze(el);
        const r = el.getBoundingClientRect();
        ox = sx - r.left; oy = sy - r.top; w = r.width; h = r.height;
        // Capture only once it is a drag: capturing on press would retarget the click.
        try { el.setPointerCapture(id); } catch { /* pointer already gone */ }
        document.documentElement.classList.add("callx-dragging");
      }
      ev.preventDefault();
      const b = bounds();
      el.style.left = Math.round(clamp(ev.clientX - ox, b.left, Math.max(b.left, b.right - w))) + "px";
      el.style.top = Math.round(clamp(ev.clientY - oy, b.top, Math.max(b.top, b.bottom - h))) + "px";
    };
    const end = (ev) => {
      if (ev.pointerId !== id) return;
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", end);
      if (!dragging) return;
      document.documentElement.classList.remove("callx-dragging");
      try { el.releasePointerCapture(id); } catch { /* ignore */ }
      if (cur) cur.busy = false;
      // The press moved: swallow the click it would otherwise produce (pill = expand).
      if (ev.type === "pointerup") { suppressClick = true; setTimeout(() => { suppressClick = false; }, 0); }
      const s = load();
      s.pos = anchorOf(el.getBoundingClientRect(), bounds());
      save(s);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", end);
  }

  /// Top-left grip: the window is bottom-right anchored by default, so it grows up and left.
  function startResize(e, el, grip) {
    e.preventDefault();
    e.stopPropagation();
    const id = e.pointerId;
    freeze(el);
    cur.busy = true;
    const r0 = el.getBoundingClientRect();
    const sx = e.clientX, sy = e.clientY;
    try { grip.setPointerCapture(id); } catch { /* ignore */ }
    document.documentElement.classList.add("callx-dragging", "callx-resizing");
    el.dataset.sized = "";

    const move = (ev) => {
      if (ev.pointerId !== id) return;
      ev.preventDefault();
      const b = bounds();
      const lim = sizeLimits(b);
      // The opposite (bottom-right) corner stays put; the size can't push the top-left out.
      const w = clamp(r0.width - (ev.clientX - sx), lim.minW, Math.min(lim.maxW, r0.right - b.left));
      const h = clamp(r0.height - (ev.clientY - sy), lim.minH, Math.min(lim.maxH, r0.bottom - b.top));
      el.style.width = Math.round(w) + "px";
      el.style.height = Math.round(h) + "px";
      el.style.left = Math.round(r0.right - w) + "px";
      el.style.top = Math.round(r0.bottom - h) + "px";
    };
    const end = (ev) => {
      if (ev.pointerId !== id) return;
      grip.removeEventListener("pointermove", move);
      grip.removeEventListener("pointerup", end);
      grip.removeEventListener("pointercancel", end);
      try { grip.releasePointerCapture(id); } catch { /* ignore */ }
      document.documentElement.classList.remove("callx-dragging", "callx-resizing");
      if (cur) cur.busy = false;
      const r = el.getBoundingClientRect();
      const s = load();
      s.size = { w: Math.round(r.width), h: Math.round(r.height) };
      s.pos = anchorOf(r, bounds());
      save(s);
    };
    grip.addEventListener("pointermove", move);
    grip.addEventListener("pointerup", end);
    grip.addEventListener("pointercancel", end);
  }

  // Capture phase on window runs before Blazor's delegated click handler.
  window.addEventListener("click", (e) => {
    if (!suppressClick || !cur || !cur.el.contains(e.target)) return;
    suppressClick = false;
    e.preventDefault();
    e.stopPropagation();
  }, true);

  function detach() {
    if (!cur) return;
    cur.cleanup.forEach((f) => f());
    cur = null;
    document.documentElement.classList.remove("callx-dragging", "callx-resizing");
  }

  function attach(el, kind) {
    if (!el) return;
    if (cur && cur.el === el && cur.kind === kind) return;
    detach();
    cur = { el, kind: kind === "pill" ? "pill" : "window", cleanup: [], busy: false };

    const onDown = (e) => {
      if (e.button !== 0 || !cur || cur.el !== el) return;
      const grip = e.target.closest(".callx-resize");
      if (grip && el.contains(grip)) { startResize(e, el, grip); return; }
      if (e.target.closest(IGNORE)) return;
      if (cur.kind === "window" && !e.target.closest(".callx-drag-handle")) return;
      startDrag(e, el);
    };
    el.addEventListener("pointerdown", onDown);
    cur.cleanup.push(() => el.removeEventListener("pointerdown", onDown));

    let raf = 0;
    const onViewport = () => {
      if (raf) return;
      raf = requestAnimationFrame(() => { raf = 0; if (cur && !cur.busy) restore(); });
    };
    window.addEventListener("resize", onViewport);
    cur.cleanup.push(() => { window.removeEventListener("resize", onViewport); if (raf) cancelAnimationFrame(raf); });

    // Content changes (devices panel, timer text) change the size; keep the anchored corner.
    if ("ResizeObserver" in window) {
      const ro = new ResizeObserver(onViewport);
      ro.observe(el);
      cur.cleanup.push(() => ro.disconnect());
    }

    restore();
  }

  /// Forget the saved position/size (not wired to UI; handy from the console).
  function reset() { try { localStorage.removeItem(KEY); } catch { /* ignore */ } }

  return { attach, detach, reset, _bounds: bounds };
})();
