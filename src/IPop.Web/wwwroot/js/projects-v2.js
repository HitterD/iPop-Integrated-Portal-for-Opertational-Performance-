// Projects v2 table: after a status change the cell flashes (spring), Done adds a burst ring.
(function () {
  const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  function replay(el, cls) {
    el.classList.remove(cls);
    void el.offsetWidth;
    el.classList.add(cls);
    el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
  }
  // ---- full screen workspace (class proj-fs on .proj-workspace, html.proj-fs-open) ----
  const FS_KEY = 'ipop.projects.fs';
  const ROOT_CLS = 'proj-fs-open';
  // Anything that is open on top of the workspace: Esc and F must leave those alone.
  const OVERLAYS = '.proj-modal, .pv2-scrim, .rep-scrim, .evidence-modal-backdrop, .task-drawer, .confirm-center, .confirm-backdrop, .pcal-pop, .cs-sheet, .modal-backdrop';
  // Light popovers: Esc closes them by clicking their click-catcher.
  const POPOVERS = '.pv2-pick-backdrop, .ph-menu-backdrop, .plist-backdrop, .kc-menu-backdrop, .pview-backdrop, .pbulk-backdrop';
  let keyHandler = null;

  function typing(t) {
    if (!t || !t.tagName) return false;
    return /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName) || t.isContentEditable;
  }

  // Sliding underline under the active view tab.
  const tabObservers = new WeakMap();
  function place(tabs, instant) {
    const ind = tabs.querySelector('.pv2-ind');
    if (!ind) return;
    const a = tabs.querySelector('.pv2-vt.on');
    if (!a) { ind.style.width = '0px'; return; }
    const tr = tabs.getBoundingClientRect(), ar = a.getBoundingClientRect();
    if (ar.width === 0) return;
    if (instant) ind.style.transition = 'none';
    ind.style.left = (ar.left - tr.left + tabs.scrollLeft + 10) + 'px';
    ind.style.width = Math.max(0, ar.width - 20) + 'px';
    if (instant) { void ind.offsetWidth; ind.style.transition = ''; }
  }

  window.ipopPv2 = {
    fullScreenPref() { try { return localStorage.getItem(FS_KEY); } catch { return null; } },
    setFullScreen(on, persist) {
      document.documentElement.classList.toggle(ROOT_CLS, !!on);
      if (persist) { try { localStorage.setItem(FS_KEY, on ? '1' : '0'); } catch { /* private mode */ } }
    },
    // Page left: never leave the rest of the app covered.
    releaseFullScreen() {
      document.documentElement.classList.remove(ROOT_CLS);
      if (keyHandler) { document.removeEventListener('keydown', keyHandler); keyHandler = null; }
    },
    attachKeys(dotNet) {
      if (keyHandler) document.removeEventListener('keydown', keyHandler);
      keyHandler = e => {
        if (e.defaultPrevented) return;
        const on = document.documentElement.classList.contains(ROOT_CLS);
        if (e.key === 'Escape') {
          if (document.querySelector(OVERLAYS)) return;
          const pop = document.querySelector(POPOVERS);
          if (pop) { pop.click(); return; }
          if (on) dotNet.invokeMethodAsync('FullScreenKey', false).catch(() => {});
          return;
        }
        if ((e.key === 'f' || e.key === 'F') && !e.ctrlKey && !e.metaKey && !e.altKey && !on) {
          if (typing(e.target) || document.querySelector(OVERLAYS) || document.querySelector(POPOVERS)) return;
          if (!document.querySelector('.pv2-ws')) return;
          dotNet.invokeMethodAsync('FullScreenKey', true).catch(() => {});
        }
      };
      document.addEventListener('keydown', keyHandler);
    },
    moveInd(tabs, instant) {
      if (!tabs) return;
      place(tabs, !!instant);
      let ro = tabObservers.get(tabs);
      if (ro) tabs.querySelectorAll('.pv2-vt').forEach(b => ro.observe(b));
    },
    watchTabs(tabs) {
      if (!tabs || tabObservers.has(tabs)) return;
      const redo = () => place(tabs, true);
      if ('ResizeObserver' in window) {
        const ro = new ResizeObserver(redo);
        ro.observe(tabs);
        tabs.querySelectorAll('.pv2-vt').forEach(b => ro.observe(b));
        tabObservers.set(tabs, ro);
      } else tabObservers.set(tabs, null);
      window.addEventListener('resize', redo);
      if (document.fonts && document.fonts.ready) document.fonts.ready.then(redo);
    },
    // "+ View" picker: anchored under the button, right edges aligned, kept inside the window; search is focused.
    openPick(pick, anchor) {
      if (!pick || !anchor) return;
      const place2 = () => {
        const r = anchor.getBoundingClientRect();
        const w = pick.offsetWidth, h = pick.offsetHeight;
        let left = r.right - w;
        left = Math.max(8, Math.min(left, window.innerWidth - w - 8));
        let top = r.bottom + 6;
        if (top + h > window.innerHeight - 8) top = Math.max(8, window.innerHeight - h - 8);
        pick.style.left = left + 'px';
        pick.style.top = top + 'px';
        pick.style.maxHeight = (window.innerHeight - top - 8) + 'px';
      };
      place2();
      const q = pick.querySelector('input[type="search"]');
      if (q) setTimeout(() => q.focus({ preventScroll: true }), 40);
    },

    statusChanged(taskId, stage) {
      if (reduced()) return;
      const el = document.querySelector(`.pv2 [data-pv2-st="${CSS.escape(taskId)}"]`);
      if (!el) return;
      replay(el, 'pv2-flash');
      if (stage === 'done') replay(el.parentElement, 'pv2-burst');
    },
  };
})();
