// Mobile helpers. See .scratch/backup-mobile/CONTRACT.md.
// Phone layout = narrower than 768 px, OR a short touch landscape phone (<= 500 px tall, <= 932 px wide,
// coarse pointer). 768-1024 px is a compact desktop (sidebar visible). Every stylesheet uses exactly
// PHONE_QUERY / DESKTOP_QUERY (tests/mobile-breakpoints.test.cjs keeps them in step).
(function (root) {
  const PHONE_QUERY = '(max-width: 767px), (max-height: 500px) and (max-width: 932px) and (pointer: coarse)';
  // Exact complement of PHONE_QUERY, for desktop-only rules.
  const DESKTOP_QUERY = '(min-width: 768px) and (min-height: 501px), (min-width: 933px), (min-width: 768px) and (pointer: fine), (min-width: 768px) and (pointer: none)';
  const TABLET_QUERY = '(max-width: 1024px)';

  /**
   * Phone Back closes the top layer. Every layer (a list/detail "detail", or an overlay such as a sheet,
   * drawer or menu) pushes one same-URL history entry tagged with an increasing sequence number. On
   * popstate, every layer above the entry we landed on is closed, top first, through its .NET callback.
   * Closing in-app drops the layer's own entry (history.back) only while that entry is still current.
   * There is at most one "detail": pushDetail on an existing detail just retargets it (old behaviour).
   */
  function createOverlayHistory({ history, isPhone, navigatingSoon = () => false }) {
    let seq = 0, backing = 0;
    const stack = []; // { seq, key, ref, method, detail }
    const queued = [];
    const cur = () => (history.state && history.state.ipopLayer) || 0;

    function push(entry) {
      if (backing) { queued.push(entry); stack.push(entry); return; }
      // After a reload, history can still hold this tab's old layer entries: number above them.
      seq = Math.max(seq, cur());
      entry.seq = ++seq;
      history.pushState({ ipopLayer: entry.seq, ipopDetail: !!entry.detail }, '');
      stack.push(entry);
    }
    // navigating: the layer closes because a link inside it is opening another page. Do not step back
    // over its entry then — the browser applies back() after the link's pushState and would undo the
    // navigation. The stale same-URL entry is harmless: Back from the new page returns to that URL.
    function remove(entry, navigating) {
      const i = stack.indexOf(entry);
      if (i < 0) return;
      stack.splice(i, 1);
      const q = queued.indexOf(entry);
      if (q >= 0) { queued.splice(q, 1); return; }
      if (!navigating && entry.seq && entry.seq === cur()) { backing++; history.back(); }
    }
    return {
      /** Opens an overlay layer; Back calls ref[method](). Returns its key (null on desktop). */
      open(ref, method = 'OnBack', key) {
        if (!isPhone()) return null;
        const entry = { key: key || 'L' + (seq + 1) + '-' + Math.random().toString(36).slice(2, 7), ref, method, detail: false };
        push(entry);
        return entry.key;
      },
      /** The overlay closed in-app (button, scrim, Escape): forget it and drop its entry. */
      close(key, opts) { const e = stack.find(x => x.key === key); if (e) remove(e, !!(opts && opts.navigating) || navigatingSoon()); },
      pushDetail(ref, method) {
        if (!isPhone()) return false;
        const d = stack.find(x => x.detail);
        if (d) { d.ref = ref; d.method = method; return true; }
        push({ key: 'detail', ref, method, detail: true });
        return true;
      },
      popDetail() { const d = stack.find(x => x.detail); if (d) remove(d); },
      clearDetail() { const i = stack.findIndex(x => x.detail); if (i >= 0) stack.splice(i, 1); },
      onPopState() {
        if (backing) {
          backing--;
          if (!backing) for (const e of queued.splice(0)) { seq = Math.max(seq, cur()); e.seq = ++seq; history.pushState({ ipopLayer: e.seq, ipopDetail: !!e.detail }, ''); }
          return;
        }
        const at = cur();
        for (let i = stack.length - 1; i >= 0; i--) {
          const e = stack[i];
          if (!e.seq || e.seq <= at) continue;
          stack.splice(i, 1);
          try { const r = e.ref.invokeMethodAsync(e.method); r && r.catch && r.catch(() => {}); } catch { /* disposed */ }
        }
      },
      count: () => stack.length,
    };
  }


  /**
   * Where to scroll so a caret/field rect is visible inside the visible viewport (keyboard open).
   * Returns the signed distance to scroll (0 when already visible). `top`/`bottom` are the visible
   * band in client coordinates; `pad` keeps a little context around the caret.
   */
  function revealDelta(rect, top, bottom, pad = 12) {
    if (!rect) return 0;
    if (rect.top - pad < top) return rect.top - pad - top;          // above the band: scroll up
    if (rect.bottom + pad > bottom) {                                // below: scroll down, but never past its top
      return Math.max(0, Math.min(rect.bottom + pad - bottom, rect.top - pad - top));
    }
    return 0;
  }


  if (typeof module === 'object' && module.exports) module.exports = { PHONE_QUERY, DESKTOP_QUERY, TABLET_QUERY, createOverlayHistory, revealDelta };
  if (!root || !root.matchMedia) return;

  const ipop = root.ipop = root.ipop || {};
  const mq = root.matchMedia(PHONE_QUERY);
  // A tap on an in-app link (More tile, sheet row) is about to navigate: overlays closed by that tap
  // must not history.back() (see remove()). Flag it from the capture phase, before Blazor's handlers run.
  let linkTapAt = 0;
  document.addEventListener('click', e => {
    const a = e.target && e.target.closest && e.target.closest('a[href]');
    if (!a || a.target === '_blank' || a.hasAttribute('download') || e.defaultPrevented) return;
    const href = a.getAttribute('href');
    if (!href || href.startsWith('#') || /^(mailto|tel|javascript):/i.test(href)) return;
    try { if (new URL(a.href, root.location.href).origin !== root.location.origin) return; } catch { return; }
    linkTapAt = Date.now();
  }, true);
  const layers = createOverlayHistory({
    history: { get state() { return root.history.state; }, pushState: (s, t) => root.history.pushState(s, t, root.location.href), back: () => root.history.back() },
    isPhone: () => mq.matches,
    navigatingSoon: () => Date.now() - linkTapAt < 800,
  });
  root.addEventListener('popstate', () => layers.onPopState());

  // Keyboard: keep the focused field / caret visible above the on-screen keyboard. Android resizes the
  // page (interactive-widget=resizes-content in index.html); iOS only shrinks visualViewport. Either
  // way, after focus or a viewport change we scroll the nearest scrollable ancestors by the overlap.
  const editable = el => el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
  function caretRect(el) {
    if (el.isContentEditable) {
      const sel = root.getSelection && root.getSelection();
      if (sel && sel.rangeCount) {
        const r = sel.getRangeAt(0).getBoundingClientRect();
        if (r.height) return r;
        const n = sel.focusNode && (sel.focusNode.nodeType === 1 ? sel.focusNode : sel.focusNode.parentElement);
        if (n && n.getBoundingClientRect) { const b = n.getBoundingClientRect(); return { top: b.top, bottom: Math.min(b.bottom, b.top + 28) }; }
      }
    }
    const b = el.getBoundingClientRect();
    // Tall fields (editors, textareas) only need their first lines visible.
    return { top: b.top, bottom: Math.min(b.bottom, b.top + (el.isContentEditable ? 40 : 72)) };
  }
  function reveal() {
    const el = document.activeElement;
    if (!mq.matches || !editable(el)) return;
    const vv = root.visualViewport;
    const top = vv ? vv.offsetTop : 0;
    let bottom = vv ? vv.offsetTop + vv.height : root.innerHeight;
    // The fixed tab bar (and any fixed footer) also covers the page: keep the caret above it.
    const bar = document.querySelector('.m-tabbar');
    if (bar && getComputedStyle(bar).display !== 'none') bottom = Math.min(bottom, bar.getBoundingClientRect().top);
    let d = revealDelta(caretRect(el), top, bottom);
    for (let a = el.parentElement; d && a; a = a.parentElement) {
      const cs = getComputedStyle(a);
      if (!/(auto|scroll)/.test(cs.overflowY) || a.scrollHeight <= a.clientHeight) continue;
      const before = a.scrollTop; a.scrollTop += d; d -= a.scrollTop - before;
    }
    if (d) root.scrollBy(0, d);
  }
  let revealT = 0;
  const soon = () => { clearTimeout(revealT); revealT = setTimeout(reveal, 120); };
  document.addEventListener('focusin', soon);
  document.addEventListener('selectionchange', () => { if (document.activeElement && document.activeElement.isContentEditable) soon(); });
  root.addEventListener('resize', soon);
  if (root.visualViewport) root.visualViewport.addEventListener('resize', soon);


  ipop.mobile = {
    PHONE_QUERY, DESKTOP_QUERY,
    isMobile: () => mq.matches,
    /** PhoneState: answers the current value and calls ref.OnPhoneChanged(bool) on every change. */
    watch(ref) {
      const on = e => { try { ref.invokeMethodAsync('OnPhoneChanged', e.matches).catch(() => {}); } catch { /* disposed */ } };
      if (mq.addEventListener) mq.addEventListener('change', on); else if (mq.addListener) mq.addListener(on);
      return mq.matches;
    },
    /** Compact desktop (768-1024 px, not a phone): the sidebar starts as the icon rail. */
    isTablet: () => !mq.matches && root.matchMedia(TABLET_QUERY).matches,
    hideTabbar: on => document.body.classList.toggle('m-hide-tabbar', !!on),
    /** Push a history entry for an opened detail; Back calls ref.method() to close it. */
    pushDetail: (ref, method) => layers.pushDetail(ref, method),
    /** Close via the in-app back button: drop our history entry without calling back. */
    popDetail: () => layers.popDetail(),
    /** Forget the entry without navigating (e.g. the page is being disposed). */
    clearDetail: () => layers.clearDetail(),
    /** Overlay opened (OverlayStack): on phones Back closes it via ref.OnBack(). */
    overlayOpen: (ref, key) => layers.open(ref, 'OnBack', key),
    overlayClose: key => layers.close(key),
    /** Scroll the focused field/caret into the visible viewport (also runs on its own). */
    reveal,
  };
})(typeof window !== 'undefined' ? window : null);
