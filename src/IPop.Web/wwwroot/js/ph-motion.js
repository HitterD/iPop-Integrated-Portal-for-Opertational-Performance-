// Phone micro-interactions (approved demo: docs/mockups/mobile-micro-interactions.html, a–j). Decoration
// only: it observes what Blazor renders and adds classes/animations around it, never changes app state.
// Pull-to-refresh is the one exception: it calls the page's own refresh (ref.OnPullRefresh) or reloads.
// Pure decision functions are exported for tests/ph-motion.test.cjs.
(function (root) {
  /** Pull-to-refresh: rubber-band distance for a finger drag of dy px, and whether it arms. */
  function pullState(dy, threshold = 70) {
    const y = dy > 0 ? Math.min(110, dy * 0.5) : 0;
    return { y, progress: Math.min(1, y / threshold), armed: y >= threshold };
  }

  /** FAB: collapse while moving down past 40 px, expand again when moving up or near the top. */
  function fabMini(prevY, y, wasMini) {
    if (y <= 40) return false;
    if (y > prevY + 4) return true;
    if (y < prevY - 4) return false;
    return wasMini;
  }

  /** Screen transition direction for a route change: back when the new path is a prefix of the old one
      (detail → list) or the browser went back; forward otherwise; none for the same path. */
  function routeDirection(from, to, popped) {
    if (from === to) return null;
    if (popped) return 'back';
    const f = from.replace(/\/+$/, ''), t = to.replace(/\/+$/, '');
    if (f.startsWith(t + '/')) return 'back';
    return 'fwd';
  }

  /** Haptics preference: on unless the user switched it off (localStorage ipop.haptics = "off"). */
  function hapticsOn(stored) { return stored !== 'off'; }

  if (typeof module === 'object' && module.exports) module.exports = { pullState, fabMini, routeDirection, hapticsOn };
  if (!root || !root.document || !root.matchMedia) return;

  const PHONE = '(max-width: 767px), (max-height: 500px) and (max-width: 932px) and (pointer: coarse)';
  const mq = root.matchMedia(PHONE);
  const reduced = () => root.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const ipop = root.ipop = root.ipop || {};
  const doc = root.document;

  function buzz(pattern) {
    if (!mq.matches) return;
    let pref = null;
    try { pref = root.localStorage.getItem('ipop.haptics'); } catch { }
    if (!hapticsOn(pref)) return;
    try { root.navigator.vibrate && root.navigator.vibrate(pattern); } catch { }
  }
  function replay(el, cls) {
    if (!el || reduced()) return;
    el.classList.remove(cls); void el.offsetWidth; el.classList.add(cls);
    el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
  }

  // (a) press feedback — one delegated listener for every tappable thing.
  const PRESS = '.ph-fab, .ph-mt, .m-tab, .ph-ib, .ph-txtb, .ph-back, .ph-btn, .ph-seg button, .m-notes-card, .m-project-card,'
    + ' .m-call-room-card, .callx-btn, .callx-ctrl, .send-btn, .mlx-fab, .ph-prof, .ph-row, .room-item, .m-nf-row, .m-tickets-row,'
    + ' .m-news-row, .m-files-row, .m-directory-row, .m-dashboard-row, .m-notes-page-row, .m-admin-user-row, .calls-history-row,'
    + ' .notif-row, .m-calendar-event';
  let pressed = null, pressTimer = 0;
  const unpress = () => { clearTimeout(pressTimer); if (pressed) pressed.classList.remove('ph-pressed'); pressed = null; };
  doc.addEventListener('pointerdown', e => {
    if (!mq.matches || e.button > 0) return;
    const el = e.target.closest && e.target.closest(PRESS);
    if (!el || el.disabled) return;
    unpress(); pressed = el;
    // A short delay keeps a scroll that starts on a row from flashing it.
    pressTimer = setTimeout(() => el.classList.add('ph-pressed'), e.pointerType === 'touch' ? 60 : 0);
  }, { passive: true, capture: true });
  ['pointerup', 'pointercancel', 'dragstart'].forEach(n => doc.addEventListener(n, () => setTimeout(unpress, 90), { passive: true, capture: true }));
  doc.addEventListener('scroll', unpress, { passive: true, capture: true });

  // (d) tab bar bounce + light haptic on tab taps.
  doc.addEventListener('click', e => {
    if (!mq.matches) return;
    const tab = e.target.closest && e.target.closest('.m-tab');
    if (tab) { replay(tab, 'ph-bounce'); buzz(8); }
    if (e.target.closest && e.target.closest('.ph-seg button, [role=tab]')) buzz(6);
  }, true);

  // (d) badges pop when they appear or their number changes; (h) a new own chat bubble pops.
  const BADGES = '.m-tab-badge, .ph-bdg, .badge-count, .nav-badge';
  const seen = new WeakMap();
  function scanBadges(rootEl) {
    rootEl.querySelectorAll && rootEl.querySelectorAll(BADGES).forEach(b => {
      const t = b.textContent;
      if (seen.has(b) && seen.get(b) !== t) replay(b, 'ph-badge-pop');
      else if (!seen.has(b) && booted) replay(b, 'ph-badge-pop');
      seen.set(b, t);
    });
  }
  let booted = false;
  const mo = new MutationObserver(list => {
    if (!mq.matches) return;
    for (const m of list) {
      if (m.type === 'characterData') { const b = m.target.parentElement && m.target.parentElement.closest(BADGES); if (b) scanBadges(b.parentElement || b); continue; }
      for (const n of m.addedNodes) {
        if (n.nodeType !== 1) continue;
        if (n.matches(BADGES) || n.querySelector(BADGES)) scanBadges(n.parentElement || n);
        if (n.matches('[data-msg-id].mine, [data-msg-id].me, [data-msg-id].own') && n.classList.contains('fresh')) replay(n, 'ph-sent');
        if (n.matches('.ph-sheet')) buzz(6);
      }
    }
  });

  // (b) route transitions + (j) stagger: when Blazor swaps the page, animate the new screen once.
  let lastPath = root.location.pathname, popped = false;
  root.addEventListener('popstate', () => { popped = true; }, true);
  function screenEl() {
    return doc.querySelector('.main-panel .ph-screen, .main-panel [class*="m-"][class*="-page"], .main-panel > :not(.push-banner):not(.practice-bar)');
  }
  function onRoute() {
    const path = root.location.pathname;
    const dir = routeDirection(lastPath, path, popped);
    lastPath = path; popped = false;
    if (!dir || !mq.matches || reduced()) return;
    // Wait for Blazor to render the new page.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      const el = screenEl();
      if (!el) return;
      replay(el, dir === 'back' ? 'ph-enter-back' : 'ph-enter-fwd');
      if (dir === 'fwd') stagger();
    }));
  }
  // Content rises in order on lists that just rendered (first visible list of the screen).
  const LISTS = '.ph-screen .ph-scroll > .ph-group, .ph-screen .ph-scroll > .m-notes-recents, .room-list, .m-nf-list, .notif-list,'
    + ' .m-tickets-list, .m-news-list, .m-files-list, .m-project-list, .m-dashboard-grid, .m-directory-list';
  function stagger() {
    const list = doc.querySelector(LISTS);
    if (!list || list.children.length > 40) return;
    replay(list, 'ph-stagger');
  }
  const wrap = name => { const orig = root.history[name].bind(root.history); root.history[name] = function () { const r = orig.apply(null, arguments); setTimeout(onRoute); return r; }; };
  wrap('pushState'); wrap('replaceState');
  root.addEventListener('popstate', () => setTimeout(onRoute));

  // (e) FAB collapses while scrolling down.
  const lastY = new WeakMap();
  doc.addEventListener('scroll', e => {
    if (!mq.matches) return;
    const sc = e.target === doc ? doc.scrollingElement : e.target;
    if (!sc || !sc.scrollTop && sc.scrollTop !== 0) return;
    const fab = doc.querySelector('.ph-fab.is-extended');
    if (!fab) return;
    const prev = lastY.get(sc) || 0, y = sc.scrollTop;
    fab.classList.toggle('ph-fab-mini', fabMini(prev, y, fab.classList.contains('ph-fab-mini')));
    lastY.set(sc, y);
  }, { passive: true, capture: true });

  // (f) pull to refresh: only on routes whose page registered a refresh (ipop.motion.registerRefresh);
  // the spinner stays while that .NET method runs.
  const PTR_SCROLLERS = '.room-pane, .mlx-rows, .ph-screen .ph-scroll';
  const refreshers = new Map(); // path prefix -> DotNetObjectReference
  let pull = null;
  doc.addEventListener('touchstart', e => {
    if (!mq.matches || e.touches.length !== 1) return;
    const sc = e.target.closest && e.target.closest(PTR_SCROLLERS);
    if (!sc || sc.scrollTop > 0 || e.target.closest('[data-swipe], input, textarea, [contenteditable=true], .ph-sheet')) return;
    if (!refresherFor(root.location.pathname)) return; // only screens with a real refresh get the gesture
    pull = { sc, y0: e.touches[0].clientY, x0: e.touches[0].clientX, st: null, ind: null };
  }, { passive: true });
  doc.addEventListener('touchmove', e => {
    if (!pull) return;
    const dy = e.touches[0].clientY - pull.y0, dx = e.touches[0].clientX - pull.x0;
    if (dy <= 0 || Math.abs(dx) > dy || pull.sc.scrollTop > 0) { if (!pull.st) pull = null; return; }
    if (!pull.ind) {
      pull.ind = doc.createElement('div'); pull.ind.className = 'ph-ptr'; pull.ind.innerHTML = '<i></i>';
      pull.sc.prepend(pull.ind); pull.sc.classList.add('ph-ptr-pull');
    }
    const st = pullState(dy);
    if (st.armed && !(pull.st && pull.st.armed)) buzz(10);
    pull.st = st;
    pull.sc.style.setProperty('--ph-ptr-y', st.y + 'px');
    pull.sc.style.setProperty('--ph-ptr-p', st.progress);
  }, { passive: true });
  doc.addEventListener('touchend', async () => {
    const p = pull; pull = null;
    if (!p || !p.ind) return;
    p.sc.classList.add('ph-ptr-release');
    if (p.st && p.st.armed) {
      p.ind.classList.add('is-loading');
      p.sc.style.setProperty('--ph-ptr-y', '48px');
      try { await refreshNow(); } catch { }
    }
    p.sc.style.setProperty('--ph-ptr-y', '0px');
    setTimeout(() => { p.sc.classList.remove('ph-ptr-pull', 'ph-ptr-release'); p.ind.remove(); p.sc.style.removeProperty('--ph-ptr-p'); }, 360);
  }, { passive: true });
  function refresherFor(path) {
    let ref = null;
    // "/chat" covers /chat/…; "=/chat" matches that path only (a list screen, not its detail).
    for (const [prefix, r] of refreshers) if (prefix.startsWith('=') ? path === prefix.slice(1) : path === prefix || path.startsWith(prefix + '/')) ref = r;
    return ref;
  }
  async function refreshNow() {
    const ref = refresherFor(root.location.pathname);
    if (!ref) return;
    const min = new Promise(r => setTimeout(r, 600)); // keep the spinner visible long enough to be seen
    await Promise.all([ref.invokeMethodAsync('OnPullRefresh').catch(() => {}), min]);
  }

  // (g) swipe rows: arm feedback while dragging past the threshold (ph-kit.js handles the swipe itself).
  doc.addEventListener('pointermove', e => {
    if (!mq.matches || e.pointerType === 'mouse') return;
    const fg = e.target.closest && e.target.closest('[data-swipe] .sw-fg');
    if (!fg) return;
    const row = fg.closest('[data-swipe]');
    const m = /translateX\((-?[\d.]+)px\)/.exec(fg.style.transform || '');
    const dx = m ? Math.abs(parseFloat(m[1])) : 0;
    const t = Math.min(96, Math.max(64, row.getBoundingClientRect().width * 0.22));
    const armed = dx >= t;
    if (armed && !row.classList.contains('ph-sw-armed')) buzz(10);
    row.classList.toggle('ph-sw-armed', armed);
  }, { passive: true });
  ['pointerup', 'pointercancel'].forEach(n => doc.addEventListener(n, () => doc.querySelectorAll('.ph-sw-armed').forEach(r => r.classList.remove('ph-sw-armed')), { passive: true }));

  // (i) incoming call: ring pattern while the full-screen call is up (stops when it leaves the DOM).
  let ringTimer = 0;
  function watchRing() {
    const ringing = mq.matches && !!doc.querySelector('.callx-incoming.mi-ringing-call');
    if (ringing && !ringTimer) { buzz([400, 200, 400]); ringTimer = setInterval(() => buzz([400, 200, 400]), 1600); }
    if (!ringing && ringTimer) { clearInterval(ringTimer); ringTimer = 0; try { root.navigator.vibrate && root.navigator.vibrate(0); } catch { } }
  }
  const ringMo = new MutationObserver(watchRing);

  function start() {
    mo.observe(doc.body, { childList: true, subtree: true, characterData: true });
    ringMo.observe(doc.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    scanBadges(doc.body);
    setTimeout(() => { booted = true; }, 1500);
  }
  if (doc.body) start(); else doc.addEventListener('DOMContentLoaded', start);

  ipop.motion = {
    buzz,
    /** Settings toggle. */
    hapticsEnabled: () => { try { return hapticsOn(root.localStorage.getItem('ipop.haptics')); } catch { return true; } },
    setHaptics: on => { try { root.localStorage.setItem('ipop.haptics', on ? 'on' : 'off'); } catch { } if (on) buzz(10); },
    /** Pull-to-refresh handler for a route prefix: ref.OnPullRefresh() is awaited while the spinner shows. */
    registerRefresh: (prefix, ref) => { refreshers.set(prefix, ref); },
    unregisterRefresh: prefix => { refreshers.delete(prefix); },
    pop: el => replay(el, 'ph-pop'),
  };
})(typeof window !== 'undefined' ? window : null);
