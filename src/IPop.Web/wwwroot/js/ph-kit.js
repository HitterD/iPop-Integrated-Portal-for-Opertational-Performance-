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

  if (typeof module === 'object' && module.exports) module.exports = { dismissDecision, collapseState, reorderTarget, platform, keyboardOpen };
  if (!root || !root.document) return;

  const ipop = root.ipop = root.ipop || {};

  // Keyboard flag for the FAB (and anything else that must step aside).
  if (root.visualViewport) {
    const kb = () => document.body && document.body.classList.toggle('ph-kb-open', keyboardOpen(root.innerHeight, root.visualViewport.height));
    root.visualViewport.addEventListener('resize', kb);
  }

  ipop.phone = {
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
