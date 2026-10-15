// Mail's three panes, Outlook-style (mockup docs/mockups/mail-layout-outlook.html v3).
// Mailboxes | list | reading pane, joined by hairline splitters: drag (live px badge), arrow keys (16 px),
// double-click resets. Limits: Mailboxes 180–320, list 280–560, reading pane ≥ 420. Mailboxes collapses to a
// 56 px icon rail (‹ / ›). Widths and the rail are saved per user per device (ipopUserStore, "<key>@<nik>").
//
// Markup contract (Pages/Mail.razor):
//   [data-mlx-surf]                       the grid; this script sets --mlx-w1 / --mlx-w2 and data-mlx-rail
//     [data-mlx-split="1"|"2"]            splitters (role=separator), positioned here
//     [data-mlx-act="collapse"|"expand"|"reset"]  rail toggles / reset (Mail settings)
//     .mlx-rscroll > .mlx-slim             reading pane scroller + slim sticky header (data-show when the newest
//                                          message's sender row has scrolled away; click goes back to the top)
// Blazor never renders the attributes this script owns, so a re-render cannot undo them.
(function () {
  const KEY = 'ipop.mail.panes.v1';
  const DEF = Object.freeze({ w1: 232, w2: 380, collapsed: false });
  const LIM = Object.freeze({ w1: [180, 320], w2: [280, 560], read: 420, rail: 56, step: 16 });

  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const num = v => (typeof v === 'number' && isFinite(v) ? v : null);

  /// Saved JSON → panes. Anything unreadable falls back to the defaults, field by field.
  function parse(json) {
    let o = null;
    try { o = json ? JSON.parse(json) : null; } catch { o = null; }
    if (!o || typeof o !== 'object') return { ...DEF };
    return {
      w1: num(o.w1) ?? DEF.w1,
      w2: num(o.w2) ?? DEF.w2,
      collapsed: typeof o.collapsed === 'boolean' ? o.collapsed : DEF.collapsed,
    };
  }
  const serialize = p => JSON.stringify({ w1: Math.round(p.w1), w2: Math.round(p.w2), collapsed: !!p.collapsed });

  /// Effective widths for a surface `avail` px wide. The reading pane keeps 420: the list gives first, then
  /// Mailboxes; when even the minimums do not fit, Mailboxes shows as the rail.
  function layout(p, avail) {
    let rail = !!p.collapsed;
    let w1 = rail ? LIM.rail : clamp(p.w1, LIM.w1[0], LIM.w1[1]);
    let w2 = clamp(p.w2, LIM.w2[0], LIM.w2[1]);
    let over = LIM.read - (avail - w1 - w2);
    if (over > 0) { const r = Math.min(over, w2 - LIM.w2[0]); w2 -= r; over -= r; }
    if (over > 0 && !rail) { const r = Math.min(over, w1 - LIM.w1[0]); w1 -= r; over -= r; }
    if (over > 0 && !rail) {
      rail = true; w1 = LIM.rail;
      w2 = clamp(avail - w1 - LIM.read, LIM.w2[0], clamp(p.w2, LIM.w2[0], LIM.w2[1]));
    }
    return { w1: Math.round(w1), w2: Math.round(w2), rail };
  }

  /// Width of the pane left of splitter `k` after dragging it by dx, from its width w0 at drag start.
  function dragTo(k, w0, dx, eff, avail) {
    const lim = k === '1' ? LIM.w1 : LIM.w2;
    const other = k === '1' ? eff.w2 : eff.w1;
    const max = Math.max(lim[0], Math.min(lim[1], avail - other - LIM.read));
    return Math.round(clamp(w0 + dx, lim[0], max));
  }

  /// Arrow keys on a focused splitter: ±16 px.
  function step(k, eff, dir) {
    const lim = k === '1' ? LIM.w1 : LIM.w2;
    return clamp((k === '1' ? eff.w1 : eff.w2) + dir * LIM.step, lim[0], lim[1]);
  }

  const reset = p => ({ ...DEF, collapsed: !!p.collapsed });

  /// store: { get(nik, key), set(nik, key, value) } (ipopUserStore). No signed-in user: nothing is written.
  const load = (store, nik) => (nik ? parse(store.get(nik, KEY)) : { ...DEF });
  function save(store, nik, p) { if (nik) store.set(nik, KEY, serialize(p)); }

  const api = { KEY, DEF, LIM, parse, serialize, layout, dragTo, step, reset, load, save };
  if (typeof module !== 'undefined' && module.exports) { module.exports = api; return; }

  // ───────────────────────── browser ─────────────────────────
  const store = () => window.ipopUserStore;
  const nik = () => store() && store().user;
  let st = null;   // { root, surf, panes, eff, ro, drag, off: [] }

  function apply() {
    if (!st || !st.surf.isConnected) return;
    const avail = st.surf.clientWidth;
    if (!avail) return;
    const e = layout(st.panes, avail);
    st.eff = e;
    st.surf.style.setProperty('--mlx-w1', e.w1 + 'px');
    st.surf.style.setProperty('--mlx-w2', e.w2 + 'px');
    st.surf.toggleAttribute('data-mlx-rail', e.rail);
    const s1 = st.surf.querySelector('[data-mlx-split="1"]');
    const s2 = st.surf.querySelector('[data-mlx-split="2"]');
    if (s1) {
      s1.style.left = e.w1 + 'px';
      s1.hidden = e.rail;
      s1.setAttribute('aria-valuenow', String(e.w1));
      s1.setAttribute('aria-valuemin', String(LIM.w1[0]));
      s1.setAttribute('aria-valuemax', String(LIM.w1[1]));
    }
    if (s2) {
      s2.style.left = (e.w1 + e.w2) + 'px';
      s2.setAttribute('aria-valuenow', String(e.w2));
      s2.setAttribute('aria-valuemin', String(LIM.w2[0]));
      s2.setAttribute('aria-valuemax', String(LIM.w2[1]));
    }
  }
  const persist = () => { if (store()) save(store(), nik(), st.panes); };

  function badge(sp) {
    const b = sp.querySelector('.mlx-wbadge');
    if (b) b.textContent = (sp.dataset.mlxSplit === '1' ? 'Mailboxes ' + st.eff.w1 : 'List ' + st.eff.w2) + ' px';
  }

  function onDown(e) {
    const sp = e.target.closest && e.target.closest('[data-mlx-split]');
    if (!sp || !st || !st.surf.contains(sp) || e.button !== 0) return;
    e.preventDefault();
    const k = sp.dataset.mlxSplit;
    st.drag = { k, sp, x0: e.clientX, w0: k === '1' ? st.eff.w1 : st.eff.w2, id: e.pointerId };
    try { sp.setPointerCapture(e.pointerId); } catch { }
    sp.classList.add('drag');
    document.body.classList.add('mlx-resizing');
    badge(sp);
  }
  function onMove(e) {
    const d = st && st.drag;
    if (!d) return;
    const w = dragTo(d.k, d.w0, e.clientX - d.x0, st.eff, st.surf.clientWidth);
    if (d.k === '1') st.panes.w1 = w; else st.panes.w2 = w;
    apply();
    badge(d.sp);
  }
  function onUp() {
    const d = st && st.drag;
    if (!d) return;
    d.sp.classList.remove('drag');
    document.body.classList.remove('mlx-resizing');
    st.drag = null;
    persist();
  }
  function onDbl(e) {
    const sp = e.target.closest && e.target.closest('[data-mlx-split]');
    if (!sp || !st) return;
    if (sp.dataset.mlxSplit === '1') st.panes.w1 = DEF.w1; else st.panes.w2 = DEF.w2;
    persist(); apply();
  }
  function onKey(e) {
    const sp = e.target.closest && e.target.closest('[data-mlx-split]');
    if (!sp || !st) return;
    const k = sp.dataset.mlxSplit;
    let w = null;
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') w = step(k, st.eff, e.key === 'ArrowLeft' ? -1 : 1);
    else if (e.key === 'Home') w = (k === '1' ? LIM.w1 : LIM.w2)[0];
    else if (e.key === 'End') w = (k === '1' ? LIM.w1 : LIM.w2)[1];
    else if (e.key === 'Enter') w = k === '1' ? DEF.w1 : DEF.w2;
    if (w === null) return;
    e.preventDefault();
    e.stopPropagation();
    if (k === '1') st.panes.w1 = w; else st.panes.w2 = w;
    persist(); apply();
  }
  function onClick(e) {
    const a = e.target.closest && e.target.closest('[data-mlx-act]');
    if (!a || !st || !st.root.contains(a)) return;
    const act = a.dataset.mlxAct;
    if (act === 'collapse') st.panes.collapsed = true;
    else if (act === 'expand') st.panes.collapsed = false;
    else if (act === 'reset') st.panes = reset(st.panes);
    else if (act === 'totop') {
      const sc = a.closest('.mlx-rscroll');
      if (sc) sc.scrollTo({ top: 0, behavior: 'smooth' });
      return;
    }
    else return;
    persist(); apply();
    if (act === 'expand' || act === 'collapse') {
      // Keep focus on the matching toggle in the pane that is now shown.
      const other = st.surf.querySelector(act === 'collapse' ? '[data-mlx-act="expand"]' : '[data-mlx-act="collapse"]');
      if (other) requestAnimationFrame(() => other.focus({ preventScroll: true }));
    }
  }

  // Slim sticky header: shown once the newest message's sender row is under the toolbar.
  function slim(sc) {
    const bar = sc.querySelector(':scope > .mlx-slim');
    const head = sc.querySelector('.thr-msg.newest .thr-head');
    if (!bar || !head) return;
    const show = head.getBoundingClientRect().bottom < bar.getBoundingClientRect().top + 8;
    bar.toggleAttribute('data-show', show);
  }
  // Scrollbars: thin overlay thumbs show while hovering a pane or scrolling it.
  const scrollTimers = new WeakMap();
  function onScroll(e) {
    const t = e.target;
    if (!(t instanceof Element) || !st || !st.root.contains(t)) return;
    if (t.classList.contains('mlx-rscroll')) slim(t);
    t.setAttribute('data-mlx-scrolling', '');
    clearTimeout(scrollTimers.get(t));
    scrollTimers.set(t, setTimeout(() => t.removeAttribute('data-mlx-scrolling'), 900));
  }

  function bind(rootSelector) {
    unbind();
    const root = typeof rootSelector === 'string' ? document.querySelector(rootSelector) : rootSelector;
    const surf = root && root.querySelector('[data-mlx-surf]');
    if (!surf) return false;
    st = { root, surf, panes: store() ? load(store(), nik()) : { ...DEF }, eff: { w1: DEF.w1, w2: DEF.w2 }, drag: null, off: [] };
    const on = (el, ev, fn, opt) => { el.addEventListener(ev, fn, opt); st.off.push(() => el.removeEventListener(ev, fn, opt)); };
    on(root, 'pointerdown', onDown);
    on(document, 'pointermove', onMove);
    on(document, 'pointerup', onUp);
    on(document, 'pointercancel', onUp);
    on(root, 'dblclick', onDbl);
    on(root, 'keydown', onKey);
    on(root, 'click', onClick);
    on(root, 'scroll', onScroll, true);
    if (typeof ResizeObserver === 'function') {
      st.ro = new ResizeObserver(() => apply());
      st.ro.observe(surf);
    }
    apply();
    return true;
  }
  function unbind() {
    if (!st) return;
    st.off.forEach(f => f());
    if (st.ro) st.ro.disconnect();
    document.body.classList.remove('mlx-resizing');
    st = null;
  }
  /// After Blazor re-renders the reading pane (another mail opened): re-check the slim header.
  function refresh() {
    if (!st) return;
    apply();
    st.root.querySelectorAll('.mlx-rscroll').forEach(slim);
  }
  /// The reading pane starts at the top for every newly opened mail.
  function scrollReaderTop() {
    if (!st) return;
    st.root.querySelectorAll('.mlx-rscroll').forEach(sc => { sc.scrollTop = 0; slim(sc); });
  }

  window.ipopMailLayout = { ...api, bind, unbind, refresh, scrollReaderTop, state: () => (st ? { panes: { ...st.panes }, eff: { ...st.eff } } : null) };
})();
