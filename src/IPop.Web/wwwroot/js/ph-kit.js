// Phone kit helpers (Wave 1). Pure decision functions are exported for tests/ph-kit.test.cjs;
// the browser half wires them to the components in Components/Phone/.
(function (root) {
  /** Drag-down on a sheet's grab handle: dismiss past 30 % of its height or on a fast flick. */
  function dismissDecision(dy, height, velocity) {
    if (!(dy > 0) || !(height > 0)) return false;
    return dy > height * 0.3 || velocity > 0.6;
  }

  /** The large title has scrolled under the nav bar: show the small title instead. */
  function collapseState({ titleBottom, navBottom }) {
    return titleBottom <= navBottom;
  }

  /** Index a dragged row should move to, given the other rows' vertical centres and the pointer y. */
  function reorderTarget(centres, from, y) {
    let to = 0;
    for (let i = 0; i < centres.length; i++) if (i !== from && y > centres[i]) to++;
    // `to` counts rows above the pointer excluding the dragged one = its new index.
    return Math.max(0, Math.min(centres.length - 1, to));
  }

  /** android | ios | desktop, for the /app install page. */
  function platform(ua) {
    const s = String(ua || '');
    if (/android/i.test(s)) return 'android';
    if (/iphone|ipad|ipod/i.test(s)) return 'ios';
    return 'desktop';
  }

  /** On-screen keyboard: the visual viewport lost more than a quarter of the layout height. */
  function keyboardOpen(layoutHeight, visualHeight) {
    return layoutHeight > 0 && visualHeight < layoutHeight * 0.75;
  }

  /** Long-press (Wave 2, CH3): held 450 ms without moving more than 10 px (a move is a scroll). */
  function longPress(ms, dx, dy) {
    return ms >= 450 && Math.abs(dx) <= 10 && Math.abs(dy) <= 10;
  }

  /** Row swipe (NF1): past 22 % of the row width (64–96 px) opens that side's actions. */
  function swipeDecision(dx, width) {
    if (!(width > 0)) return null;
    const t = Math.min(96, Math.max(64, width * 0.22));
    return dx <= -t ? 'left' : dx >= t ? 'right' : null;
  }

  /** Pixels the on-screen keyboard hides at the bottom of the layout viewport (iOS keeps the
      layout height; Android with interactive-widget=resizes-content shrinks it, so 0 there). */
  function keyboardInset(layoutHeight, visualHeight, visualTop) {
    return Math.max(0, Math.round(layoutHeight - visualHeight - (visualTop || 0)));
  }

  if (typeof module === 'object' && module.exports) module.exports = { dismissDecision, collapseState, reorderTarget, platform, keyboardOpen, longPress, swipeDecision, keyboardInset };
  if (!root || !root.document) return;

  const ipop = root.ipop = root.ipop || {};

  // Keyboard flag for the FAB (and anything else that must step aside).
  if (root.visualViewport) {
    const kb = () => {
      if (!document.body) return;
      const vv = root.visualViewport;
      document.body.classList.toggle('ph-kb-open', keyboardOpen(root.innerHeight, vv.height));
      // Phone composers (chat, mail quick reply, meeting chat) sit on the keyboard: --ph-kb lifts them.
      document.documentElement.style.setProperty('--ph-kb', keyboardInset(root.innerHeight, vv.height, vv.offsetTop) + 'px');
    };
    root.visualViewport.addEventListener('resize', kb);
    root.visualViewport.addEventListener('scroll', kb);
  }

  const calendarSwipes = new WeakMap();
  ipop.phone = {
    calendarSwipe(element, ref) {
      ipop.phone.calendarSwipeDispose(element);
      let start, active = true;
      const onStart = e => { if (!active) return; const t = e.touches[0]; start = { x: t.clientX, y: t.clientY }; };
      const onEnd = e => {
        if (!active || !start) return;
        const t = e.changedTouches[0], dx = t.clientX - start.x, dy = t.clientY - start.y;
        start = null;
        if (Math.abs(dx) > 64 && Math.abs(dx) > Math.abs(dy) * 1.5) ref.invokeMethodAsync('SwipeWeek', dx < 0 ? 1 : -1).catch(() => {});
      };
      const onCancel = () => { start = null; };
      element.addEventListener('touchstart', onStart, { passive: true });
      element.addEventListener('touchend', onEnd, { passive: true });
      element.addEventListener('touchcancel', onCancel, { passive: true });
      calendarSwipes.set(element, () => {
        active = false; start = null;
        element.removeEventListener('touchstart', onStart);
        element.removeEventListener('touchend', onEnd);
        element.removeEventListener('touchcancel', onCancel);
      });
    },
    calendarSwipeDispose(element) {
      calendarSwipes.get(element)?.();
      calendarSwipes.delete(element);
    },
    directoryJump(letter) {
      document.getElementById('directory-letter-' + letter)?.scrollIntoView({ block: 'start', behavior: 'smooth' });
    },
    platform: () => platform(root.navigator.userAgent),
    standalone: () => !!(root.matchMedia && root.matchMedia('(display-mode: standalone)').matches) || root.navigator.standalone === true,
    /** The browser's install prompt is captured once by ipop.js (installState / install). */
    installState: () => (ipop.installState ? ipop.installState() : 'unsupported'),
    install: () => (ipop.install ? ipop.install() : Promise.resolve('unsupported')),
    /** Does a URL answer (HEAD)? Used by /app to show the APK button only when the file exists. */
    async exists(url) {
      try { const r = await fetch(url, { method: 'HEAD', cache: 'no-store' }); return r.ok; } catch { return false; }
    },

    /** Large-title collapse: one IntersectionObserver per screen, no scroll listeners. */
    collapse(scrollEl, navEl) {
      if (!scrollEl || !navEl || !('IntersectionObserver' in root)) return;
      const title = scrollEl.querySelector('.ph-large-title');
      if (!title) return;
      const io = new IntersectionObserver(([e]) => {
        const navBottom = navEl.getBoundingClientRect().bottom;
        navEl.classList.toggle('is-collapsed', collapseState({ titleBottom: e.boundingClientRect.bottom, navBottom }));
      }, { root: scrollEl, threshold: [0, 0.5, 1] });
      io.observe(title);
    },

    /** Sheet drag-to-dismiss on the grab handle (pointer events on the handle only). */
    sheet(sheetEl, grabEl, ref, scaleBack) {
      if (!sheetEl || !grabEl) return;
      if (scaleBack) document.body.classList.add('ph-scaled');
      const unscale = () => document.body.classList.remove('ph-scaled');
      new MutationObserver((_, mo) => { if (!sheetEl.isConnected) { unscale(); mo.disconnect(); } })
        .observe(sheetEl.parentNode || document.body, { childList: true });
      let y0 = 0, t0 = 0, dy = 0, id = null;
      grabEl.style.touchAction = 'none';
      grabEl.addEventListener('pointerdown', e => {
        id = e.pointerId; y0 = e.clientY; t0 = performance.now(); dy = 0;
        grabEl.setPointerCapture(id); sheetEl.style.transition = 'none';
      });
      grabEl.addEventListener('pointermove', e => {
        if (e.pointerId !== id) return;
        dy = Math.max(0, e.clientY - y0);
        sheetEl.style.transform = `translateY(${dy}px)`;
      });
      const end = e => {
        if (e.pointerId !== id) return;
        id = null;
        const v = dy / Math.max(1, performance.now() - t0);
        sheetEl.style.transition = '';
        if (dismissDecision(dy, sheetEl.getBoundingClientRect().height, v)) {
          sheetEl.style.transform = 'translateY(105%)';
          setTimeout(() => ref.invokeMethodAsync('OnDragDismiss').catch(() => {}), 160);
        } else sheetEl.style.transform = '';
      };
      grabEl.addEventListener('pointerup', end);
      grabEl.addEventListener('pointercancel', end);
    },

    /** Edit tabs: drag rows by their ≡ handle; calls ref.OnReorder(from, to) on drop. */
    sortable(listEl, ref) {
      if (!listEl || listEl._phSortable) return;
      listEl._phSortable = true;
      listEl.addEventListener('pointerdown', e => {
        const handle = e.target.closest('.ph-grip');
        if (!handle) return;
        const row = handle.closest('[data-sort]');
        const rows = [...listEl.querySelectorAll('[data-sort]')];
        const from = rows.indexOf(row);
        if (from < 0) return;
        e.preventDefault();
        const centres = rows.map(r => { const b = r.getBoundingClientRect(); return b.top + b.height / 2; });
        const y0 = e.clientY;
        handle.setPointerCapture(e.pointerId);
        row.classList.add('lift');
        let to = from;
        const move = ev => {
          row.style.transform = `translateY(${ev.clientY - y0}px)`;
          to = reorderTarget(centres, from, ev.clientY);
        };
        const up = () => {
          handle.removeEventListener('pointermove', move);
          handle.removeEventListener('pointerup', up);
          handle.removeEventListener('pointercancel', up);
          row.classList.remove('lift'); row.style.transform = '';
          if (to !== from) ref.invokeMethodAsync('OnReorder', from, to).catch(() => {});
        };
        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
      });
    },

    /** Long-press on a child matching `selector` inside `container` → ref[method](key). The key is the
        element's data-lp attribute. Haptic tick where the browser has one. Text selection still works
        with a plain tap; the bubble CSS turns off the iOS callout only. */
    longPress(container, selector, ref, method) {
      if (!container || container._phLp) return;
      container._phLp = true;
      let t = null, x0 = 0, y0 = 0, el = null, fired = false;
      const cancel = () => { if (t) clearTimeout(t); t = null; el = null; };
      container.addEventListener('pointerdown', e => {
        if (e.pointerType === 'mouse') return;
        el = e.target.closest(selector);
        if (!el || !container.contains(el)) return;
        x0 = e.clientX; y0 = e.clientY; fired = false;
        const target = el;
        t = setTimeout(() => {
          t = null; fired = true;
          try { root.navigator.vibrate && root.navigator.vibrate(10); } catch { }
          // The finger lifts over the sheet that just opened: the click that follows must not
          // press whatever is under it (Pin, Delete…). Swallow exactly one click, anywhere.
          const eat = ev => { ev.stopPropagation(); ev.preventDefault(); done(); };
          const done = () => { document.removeEventListener('click', eat, true); clearTimeout(tid); fired = false; };
          const tid = setTimeout(done, 900);
          document.addEventListener('click', eat, true);
          ref.invokeMethodAsync(method, target.getAttribute('data-lp') || '').catch(() => {});
        }, 450);
      }, { passive: true });
      container.addEventListener('pointermove', e => {
        if (t && !longPress(450, e.clientX - x0, e.clientY - y0)) cancel();
      }, { passive: true });
      ['pointerup', 'pointercancel', 'pointerleave'].forEach(n => container.addEventListener(n, cancel, { passive: true }));
      // Touch long-press also raises contextmenu: keep it from opening the desktop menu as well.
      container.addEventListener('contextmenu', e => {
        if (e.target.closest(selector) && root.matchMedia('(pointer: coarse)').matches) { e.preventDefault(); e.stopPropagation(); }
      }, true);
    },

    /** Horizontal swipe on rows ([data-swipe] inside `container`): the row follows the finger; past
        the threshold it stays open (.sw-left / .sw-right) to show its actions; a tap elsewhere closes. */
    swipeRows(container) {
      if (!container || container._phSw) return;
      container._phSw = true;
      let row = null, x0 = 0, y0 = 0, dx = 0, horiz = null;
      const closeAll = except => container.querySelectorAll('[data-swipe].sw-left,[data-swipe].sw-right').forEach(r => { if (r !== except) r.classList.remove('sw-left', 'sw-right'); });
      container.addEventListener('pointerdown', e => {
        if (e.pointerType === 'mouse') return;
        const r = e.target.closest('[data-swipe]');
        if (!r || e.target.closest('.sw-acts')) return;
        closeAll(r);
        row = r; x0 = e.clientX; y0 = e.clientY; dx = 0; horiz = null;
      }, { passive: true });
      container.addEventListener('pointermove', e => {
        if (!row) return;
        const mx = e.clientX - x0, my = e.clientY - y0;
        if (horiz === null && (Math.abs(mx) > 8 || Math.abs(my) > 8)) horiz = Math.abs(mx) > Math.abs(my);
        if (!horiz) return;
        dx = mx;
        const fg = row.querySelector('.sw-fg');
        if (fg) { fg.style.transition = 'none'; fg.style.transform = `translateX(${dx}px)`; }
      }, { passive: true });
      const end = () => {
        if (!row) return;
        const fg = row.querySelector('.sw-fg');
        if (fg) { fg.style.transition = ''; fg.style.transform = ''; }
        if (horiz) {
          const side = swipeDecision(dx, row.getBoundingClientRect().width);
          row.classList.toggle('sw-left', side === 'left');
          row.classList.toggle('sw-right', side === 'right');
          // A swipe can end in a synthetic click on the row (mouse/pen emulation): ignore one within 350 ms.
          if (side) { row._swJust = true; setTimeout(() => { row._swJust = false; }, 350); }
        }
        row = null;
      };
      container.addEventListener('pointerup', end, { passive: true });
      container.addEventListener('pointercancel', end, { passive: true });
      // A finished swipe is not a tap on the row.
      container.addEventListener('click', e => {
        const r = e.target.closest('[data-swipe]');
        if (r && r._swJust && !e.target.closest('.sw-acts')) { r._swJust = false; e.stopPropagation(); e.preventDefault(); return; }
        if (!e.target.closest('.sw-acts')) closeAll(null);
      }, true);
    },

    /** QR code as an SVG path (qrcode-generator, vendored in lib/qrcode). */
    qrSvg(text) {
      if (!root.qrcode) return '';
      const qr = root.qrcode(0, 'M');
      qr.addData(text); qr.make();
      const n = qr.getModuleCount(), q = 2;
      let d = '';
      for (let r = 0; r < n; r++) for (let c = 0; c < n; c++) if (qr.isDark(r, c)) d += `M${c + q} ${r + q}h1v1h-1z`;
      return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${n + 2 * q} ${n + 2 * q}" shape-rendering="crispEdges" role="img" aria-label="QR code"><rect width="100%" height="100%" fill="#fff"/><path d="${d}" fill="#111"/></svg>`;
    },
  };
})(typeof window !== 'undefined' ? window : null);
