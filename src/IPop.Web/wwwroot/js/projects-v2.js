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
      if (keyHandler) { document.removeEventListener('keydown', keyHandler, true); keyHandler = null; }
    },
    attachKeys(dotNet) {
      if (keyHandler) document.removeEventListener('keydown', keyHandler, true);
      keyHandler = e => {
        if (e.defaultPrevented) return;
        const on = document.documentElement.classList.contains(ROOT_CLS);
        if (e.key === 'Escape') {
          if (document.querySelector(OVERLAYS) || document.getElementById('pv2-panel')) return;
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
      // Capture phase: decide before the drawer/modal's own Esc handler closes it (else one Esc closes both).
      document.addEventListener('keydown', keyHandler, true);
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

// ---- Gantt (Components/Projects/ProjectGanttView.razor) ----
// Pointer-capture drag of bars, resize handle, link drawing, hover tooltip, row/bar highlight and keyboard moves.
// Only the final day delta goes to C# (one call per drop), so dragging never round-trips per pixel.
(function () {
  const st = new WeakMap();
  const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

  function hideTip(root) { const t = root.querySelector(':scope > .pv2g-tip'); if (t) t.remove(); }
  function tipEl(root) {
    let t = root.querySelector(':scope > .pv2g-tip');
    if (!t) { t = document.createElement('div'); t.className = 'pv2g-tip'; t.setAttribute('role', 'tooltip'); root.appendChild(t); }
    return t;
  }
  function esc(s) { return String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }
  function placeTip(tip, x, y) {
    const w = tip.offsetWidth, h = tip.offsetHeight;
    let left = x + 14, top = y + 16;
    if (left + w > window.innerWidth - 8) left = x - w - 14;
    if (top + h > window.innerHeight - 8) top = y - h - 12;
    tip.style.left = Math.max(8, left) + 'px';
    tip.style.top = Math.max(8, top) + 'px';
  }
  function showTip(root, bar, e, html) {
    if (html == null) {
      let d = null;
      try { d = JSON.parse(bar.dataset.tip || 'null'); } catch (_) { d = null; }
      if (!d) { hideTip(root); return; }
      html = '<b>' + esc(d.t) + '</b>' + esc(d.l) + (d.s && d.s.length ? d.s.map(x => '<br><span>' + esc(x) + '</span>').join('') : '');
    }
    const tip = tipEl(root);
    tip.innerHTML = html;
    placeTip(tip, e.clientX, e.clientY);
  }
  function fmt(d) { return d.toLocaleDateString('en-US', { weekday: 'short', day: 'numeric', month: 'short' }).replace(',', ''); }
  function addDays(iso, n) { const p = iso.split('-').map(Number); return new Date(p[0], p[1] - 1, p[2] + n); }
  const q = (root, sel, id) => id ? root.querySelector(sel.replace('#', CSS.escape(id))) : null;
  function clearHl(root) {
    root.querySelectorAll('.pv2g-bar.hl-b, .pv2g-ms.hl-b').forEach(b => b.classList.remove('hl-b'));
    root.querySelectorAll('.pv2g-row.hl').forEach(r => r.classList.remove('hl'));
  }

  function init(root, dotnet) {
    const old = st.get(root);
    if (old) old.off();
    const s = { drag: null, hov: null };
    const px = () => parseFloat(root.dataset.px) || 13;
    const ro = () => root.dataset.ro === '1';
    const label = b => (b.getAttribute('aria-label') || '').split(',')[0];

    const down = e => {
      if (e.button !== 0) return;
      const b = e.target.closest('.pv2g-bar, .pv2g-ms');
      if (!b || !root.contains(b)) return;
      const handle = e.target.closest('.pv2g-hand'), rs = e.target.closest('.pv2g-rs');
      const kind = b.dataset.kind === 'milestone' ? 'milestone' : handle && !ro() ? 'link' : rs && !ro() ? 'resize' : 'move';
      if (ro() && kind !== 'move') return;
      s.drag = { b, kind, id: b.dataset.gid, x0: e.clientX, y0: e.clientY, left0: parseFloat(b.style.left), w0: parseFloat(b.style.width), days: 0, moved: false, start: b.dataset.start, end: b.dataset.end };
      try { b.setPointerCapture(e.pointerId); } catch (_) { }
      hideTip(root);
      e.preventDefault();
    };
    const move = e => {
      const d = s.drag;
      if (!d) {
        const b = e.target.closest && e.target.closest('.pv2g-bar, .pv2g-ms');
        if (b && root.contains(b)) showTip(root, b, e); else hideTip(root);
        return;
      }
      const dx = e.clientX - d.x0;
      if (!d.moved && Math.abs(dx) < 3 && Math.abs(e.clientY - d.y0) < 3) return;
      d.moved = true;
      if (d.kind === 'link') {
        d.b.classList.add('linking');
        let ln = root.querySelector('.pv2g-linkline');
        if (!ln) { ln = document.createElement('i'); ln.className = 'pv2g-linkline'; root.appendChild(ln); }
        const r = d.b.getBoundingClientRect();
        const x1 = r.right, y1 = r.top + r.height / 2, x2 = e.clientX, y2 = e.clientY;
        ln.style.cssText = 'left:' + x1 + 'px;top:' + y1 + 'px;width:' + Math.hypot(x2 - x1, y2 - y1) + 'px;transform:rotate(' + Math.atan2(y2 - y1, x2 - x1) + 'rad)';
        root.querySelectorAll('.pv2g-bar.target').forEach(x => x.classList.remove('target'));
        const under = document.elementFromPoint(x2, y2);
        const t = under && under.closest && under.closest('.pv2g-bar');
        if (t && t !== d.b) t.classList.add('target');
        return;
      }
      d.days = Math.round(dx / px());
      d.b.classList.add('dragging');
      if (d.kind === 'resize') {
        d.b.style.width = Math.max(d.w0 + d.days * px(), px()) + 'px';
        showTip(root, d.b, e, '<b>' + esc(label(d.b)) + '</b><span>' + (d.days > 0 ? '+' : '') + d.days + ' days → finishes ' + esc(fmt(addDays(d.end, d.days))) + '</span>');
      } else {
        d.b.style.left = (d.left0 + d.days * px()) + 'px';
        const lab = q(root, '.pv2g-glabel[data-lid="#"]', d.id);
        if (lab) lab.style.opacity = .25;
        showTip(root, d.b, e, '<b>' + esc(label(d.b)) + '</b><span>' + (d.days >= 0 ? '+' : '') + d.days + ' days → starts ' + esc(fmt(addDays(d.start, d.days))) + '</span>');
      }
    };
    const up = e => {
      const d = s.drag;
      if (!d) return;
      s.drag = null;
      d.b.classList.remove('dragging', 'linking');
      root.querySelectorAll('.pv2g-bar.target').forEach(x => x.classList.remove('target'));
      const ln = root.querySelector('.pv2g-linkline'); if (ln) ln.remove();
      hideTip(root);
      try { d.b.releasePointerCapture(e.pointerId); } catch (_) { }
      if (d.kind === 'link') {
        if (!d.moved) return;
        const under = document.elementFromPoint(e.clientX, e.clientY);
        const t = under && under.closest && under.closest('.pv2g-bar');
        if (t && t.dataset.gid !== d.id) dotnet.invokeMethodAsync('OnLinkDrawn', d.id, t.dataset.gid).catch(() => { });
        return;
      }
      if (!d.moved || d.days === 0) {
        d.b.style.left = d.left0 + 'px';
        if (!isNaN(d.w0)) d.b.style.width = d.w0 + 'px';
        const lab = q(root, '.pv2g-glabel[data-lid="#"]', d.id); if (lab) lab.style.opacity = '';
        if (!d.moved && d.kind !== 'milestone') dotnet.invokeMethodAsync('OnBarClick', d.id).catch(() => { });
        return;
      }
      dotnet.invokeMethodAsync('OnBarMoved', d.id, d.days, d.kind).catch(() => { });
    };
    const over = e => {
      const row = e.target.closest && e.target.closest('.pv2g-row[data-hid]');
      const bar = e.target.closest && e.target.closest('.pv2g-bar, .pv2g-ms');
      if (row && root.contains(row)) {
        const id = row.dataset.hid;
        if (s.hov === id) return;
        s.hov = id; clearHl(root);
        const b = root.querySelector('[data-gid="' + CSS.escape(id) + '"]');
        if (b) b.classList.add('hl-b');
      } else if (bar && root.contains(bar)) {
        const id = bar.dataset.gid;
        if (s.hov === id) return;
        s.hov = id; clearHl(root);
        const r = root.querySelector('.pv2g-row[data-hid="' + CSS.escape(id) + '"]'); if (r) r.classList.add('hl');
      } else if (s.hov) { s.hov = null; clearHl(root); }
    };
    const leave = () => { hideTip(root); s.hov = null; clearHl(root); };
    const key = e => {
      const b = e.target.closest && e.target.closest('.pv2g-bar, .pv2g-ms');
      if (!b || !root.contains(b)) return;
      if (e.key === 'Enter' || e.key === ' ') {
        if (b.dataset.kind !== 'milestone') { e.preventDefault(); dotnet.invokeMethodAsync('OnBarClick', b.dataset.gid).catch(() => { }); }
        return;
      }
      if (ro()) return;
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        e.preventDefault();
        const n = (e.shiftKey ? 7 : 1) * (e.key === 'ArrowLeft' ? -1 : 1);
        dotnet.invokeMethodAsync('OnBarMoved', b.dataset.gid, n, b.dataset.kind === 'milestone' ? 'milestone' : 'move').catch(() => { });
      }
    };
    root.addEventListener('pointerdown', down);
    root.addEventListener('pointermove', move);
    root.addEventListener('pointerup', up);
    root.addEventListener('pointercancel', up);
    root.addEventListener('mouseover', over);
    root.addEventListener('mouseleave', leave);
    root.addEventListener('keydown', key);
    s.off = () => {
      root.removeEventListener('pointerdown', down); root.removeEventListener('pointermove', move);
      root.removeEventListener('pointerup', up); root.removeEventListener('pointercancel', up);
      root.removeEventListener('mouseover', over); root.removeEventListener('mouseleave', leave);
      root.removeEventListener('keydown', key);
    };
    st.set(root, s);
  }

  Object.assign(window.ipopPv2, {
    ganttInit(root, dotnet) { if (root) init(root, dotnet); },
    ganttDispose(root) { const s = root && st.get(root); if (s) { s.off(); st.delete(root); } },
    ganttScrollTo(root, x, smooth) {
      const w = root && root.querySelector('.pv2g-wrap');
      if (w) w.scrollTo({ left: Math.max(0, x), behavior: smooth && !reduced() ? 'smooth' : 'auto' });
    },
    // Zoom changed: keep the same date under the left edge.
    ganttRescale(root, oldPx, newPx) {
      const w = root && root.querySelector('.pv2g-wrap');
      if (w && oldPx && newPx) w.scrollLeft = w.scrollLeft * newPx / oldPx;
    },
    ganttSeg(root) {
      root.querySelectorAll('.pv2g-seg').forEach(seg => {
        const k = seg.querySelector('.pv2g-knob'), on = seg.querySelector('button.on');
        if (k && on) { k.style.left = on.offsetLeft + 'px'; k.style.width = on.offsetWidth + 'px'; }
      });
    },
  });

  // ---- Stage 4: overview ring animation + task panel keys ----
  let panelHandler = null;
  Object.assign(window.ipopPv2, {
    // Count the progress ring up from 0 to its --pv2o-t target (skipped with reduced motion).
    ringAnim() {
      document.querySelectorAll('.pv2o-ring[style*="--pv2o-t"]').forEach(r => {
        if (r.dataset.anim === r.style.getPropertyValue('--pv2o-t')) return;
        const tgt = parseFloat(r.style.getPropertyValue('--pv2o-t')) || 0;
        r.dataset.anim = String(tgt);
        if (reduced()) { r.style.setProperty('--v', tgt); return; }
        let cur = 0; r.style.setProperty('--v', 0);
        const step = () => { cur += Math.max(.6, (tgt - cur) * .1); if (cur >= tgt) cur = tgt; r.style.setProperty('--v', cur); if (cur < tgt) requestAnimationFrame(step); };
        requestAnimationFrame(step);
      });
    },
    focusPanel() { const p = document.getElementById('pv2-panel'); if (p) p.focus({ preventScroll: true }); },
    // Esc closes the panel, Up/Down step to the neighbouring task. Leaves typing fields and open overlays alone.
    panelKeys(dotNet) {
      if (panelHandler) document.removeEventListener('keydown', panelHandler, true);
      panelHandler = e => {
        if (e.defaultPrevented || !document.getElementById('pv2-panel')) return;
        if (e.key !== 'Escape' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
        if (document.querySelector(OVERLAYS) || document.querySelector('.pv2-stpop, .pv2p-deppop, .quick-assign-popover, .plist-menu')) return;
        if (e.key !== 'Escape' && typing(e.target)) return;
        if (e.key === 'Escape' && typing(e.target) && e.target.tagName !== 'INPUT') return;
        e.preventDefault();
        dotNet.invokeMethodAsync('PanelKey', e.key).catch(() => {});
      };
      document.addEventListener('keydown', panelHandler, true);
    },
    panelKeysOff() { if (panelHandler) { document.removeEventListener('keydown', panelHandler, true); panelHandler = null; } },
  });
})();
