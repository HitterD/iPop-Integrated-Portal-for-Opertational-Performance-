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
  const FS_KEY = 'ipop.projects.fs2'; // fs → fs2: full screen is no longer the default, old saved "on" is dropped
  const ROOT_CLS = 'proj-fs-open';
  // Anything that is open on top of the workspace: Esc and F must leave those alone.
  const OVERLAYS = '.proj-modal, .pv2-scrim, .rep-scrim, .evidence-modal-backdrop, .task-drawer, .confirm-center, .confirm-backdrop, .pcal-pop, .cs-sheet, .modal-backdrop';
  // Light popovers: Esc closes them by clicking their click-catcher.
  const POPOVERS = '.pv2-pick-backdrop, .ph-menu-backdrop, .plist-backdrop, .kc-menu-backdrop, .pview-backdrop, .pbulk-backdrop, .pv2-tbback, .td-menu-backdrop';
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
          // v2 modals close on Esc via their own keydown, which needs focus inside; after a re-render focus can sit on <body>.
          const v2m = document.querySelector('.pv2-scrim .pv2-modal');
          if (v2m && !v2m.contains(document.activeElement)) { const x = v2m.querySelector('.pv2-mh [aria-label="Close"]'); if (x) { e.preventDefault(); x.click(); return; } }
          if (document.querySelector(OVERLAYS) || document.getElementById('pv2-panel') || document.getElementById('tdp')) return;
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
      let ro = null;
      // Self-cleaning: once this tab strip has left the page, drop its observer and window listener
      // (the component is re-created on every visit, so they would otherwise pile up).
      const redo = () => {
        if (!tabs.isConnected) {
          window.removeEventListener('resize', redo);
          if (ro) ro.disconnect();
          tabObservers.delete(tabs);
          return;
        }
        place(tabs, true);
      };
      if ('ResizeObserver' in window) {
        ro = new ResizeObserver(redo);
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
  // Own copies: OVERLAYS / typing() live in the first IIFE above (the task panel keys below ran into a ReferenceError).
  const OVERLAYS = '.proj-modal, .pv2-scrim, .rep-scrim, .evidence-modal-backdrop, .task-drawer, .confirm-center, .confirm-backdrop, .pcal-pop, .cs-sheet, .modal-backdrop';
  const typing = t => !!t && !!t.tagName && (/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName) || t.isContentEditable);
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
        // A held arrow key auto-repeats ~30/s; each move is a preview + apply + reload, so one at a time.
        if (s.keyBusy) return;
        const n = (e.shiftKey ? 7 : 1) * (e.key === 'ArrowLeft' ? -1 : 1);
        s.keyBusy = true;
        dotnet.invokeMethodAsync('OnBarMoved', b.dataset.gid, n, b.dataset.kind === 'milestone' ? 'milestone' : 'move')
          .catch(() => { }).finally(() => {
            s.keyBusy = false;
            // The bars are re-created after a move; keep keyboard focus on the same task so the next key press works.
            const gid = b.dataset.gid;
            requestAnimationFrame(() => {
              if (b.isConnected && document.activeElement === b) return;
              const nb = root.querySelector('[data-gid="' + CSS.escape(gid) + '"]');
              const a = document.activeElement;
              if (nb && (!a || a === document.body || root.contains(a))) nb.focus({ preventScroll: true });
            });
          });
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
  let panelHandler = null, pageHandler = null, lastPanel = null;
  const reducedMotion = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
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
    focusDialog(sel) { const d = document.querySelector(sel); if (d && !d.contains(document.activeElement)) d.focus({ preventScroll: true }); },
    focusPanel() { const p = document.getElementById('pv2-panel'); if (p) p.focus({ preventScroll: true }); },
    // Esc closes the panel, Up/Down step to the neighbouring task. Leaves typing fields and open overlays alone.
    panelKeys(dotNet) {
      if (panelHandler) document.removeEventListener('keydown', panelHandler, true);
      panelHandler = e => {
        if (e.defaultPrevented || !document.getElementById('pv2-panel')) return;
        if (e.key !== 'Escape' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
        // Date picker and AppSelect popups own their arrow keys and Esc (Esc closes the popup, not the panel).
        if (document.querySelector(OVERLAYS) || document.querySelector('.pv2-stpop, .pv2p-deppop, .quick-assign-popover, .plist-menu, .dp-pop, .app-select-pop.on')) return;
        if (e.key !== 'Escape' && typing(e.target)) return;
        if (e.key === 'Escape' && typing(e.target) && e.target.tagName !== 'INPUT') return;
        e.preventDefault();
        dotNet.invokeMethodAsync('PanelKey', e.key).catch(() => {});
      };
      document.addEventListener('keydown', panelHandler, true);
    },
    panelKeysOff() { if (panelHandler) { document.removeEventListener('keydown', panelHandler, true); panelHandler = null; } },

    // ---- Full-page task (Components/Projects/ProjectTaskPage.razor, #tdp) ----
    // Esc goes back to the board, Up/Down step through the view; typing fields and open popups keep their keys.
    taskPageKeys(dotNet) {
      if (pageHandler) { document.removeEventListener('keydown', pageHandler, true); pageHandler = null; }
      if (!dotNet) return;
      pageHandler = e => {
        if (e.defaultPrevented || !document.getElementById('tdp')) return;
        if (e.key !== 'Escape' && e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return;
        if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
        if (document.querySelector('.proj-modal, .pv2-scrim, .rep-scrim, .evidence-modal-backdrop, .confirm-center, .confirm-backdrop, .modal-backdrop, .av-backdrop, .attachment-viewer, .nas-picker'))
          return;
        const pop = document.querySelector('#tdp .tp-catch, .dp-pop, .app-select-pop.on');
        if (pop) { if (e.key === 'Escape' && pop.classList.contains('tp-catch')) { e.preventDefault(); pop.click(); } return; }
        if (typing(e.target)) { if (e.key === 'Escape') e.target.blur(); return; }
        e.preventDefault();
        dotNet.invokeMethodAsync('PageKey', e.key).catch(() => {});
      };
      document.addEventListener('keydown', pageHandler, true);
    },
    // Every render: the status pill slides under the selected segment (spring, like the mockup).
    taskPageSync(root) {
      if (!root) return;
      const seg = root.querySelector('.tp-seg'), ind = seg && seg.querySelector('.tp-seg-ind'), on = seg && seg.querySelector('button.on');
      if (!seg || !ind) return;
      if (!on) { ind.style.height = '0px'; return; }
      const first = !seg.classList.contains('ready');
      if (first) ind.style.transition = 'none';
      ind.style.left = on.offsetLeft + 'px'; ind.style.top = on.offsetTop + 'px';
      ind.style.width = on.offsetWidth + 'px'; ind.style.height = on.offsetHeight + 'px';
      ind.style.setProperty('--c', getComputedStyle(on).getPropertyValue('--c'));
      if (first) { void ind.offsetWidth; ind.style.transition = ''; seg.classList.add('ready'); }
    },
    // Quick view -> full page (mockup timeline): the sheet grows from the side panel's rectangle to the whole canvas
    // (560 ms, corners flatten), the board behind steps back, the task title glides from the panel header into place
    // (19 -> 29 px) and the sections rise in 45 ms apart (.tp-in, CSS).
    taskPageEnter(root) {
      if (!root || !root.animate) return;
      const from = lastPanel; lastPanel = null;
      if (reducedMotion()) return;
      root.classList.add('tp-in');
      setTimeout(() => root.classList.remove('tp-in'), 1300);
      const ease = 'cubic-bezier(.32,.72,0,1)';
      if (!from) { root.animate([{ opacity: 0, transform: 'scale(.985)' }, { opacity: 1, transform: 'none' }], { duration: 320, easing: ease }); return; }
      const r = root.getBoundingClientRect();
      const top = Math.max(0, from.rect.top - r.top), left = Math.max(0, from.rect.left - r.left);
      const right = Math.max(0, r.right - from.rect.right), bottom = Math.max(0, r.bottom - from.rect.bottom);
      root.animate([
        { clipPath: `inset(${top}px ${right}px ${bottom}px ${left}px round 18px)`, boxShadow: '0 24px 60px -20px rgba(20,24,22,.3)' },
        { clipPath: 'inset(0px 0px 0px 0px round 0px)', boxShadow: '0 0 0 0 rgba(20,24,22,0)' }
      ], { duration: 560, easing: ease });
      const board = root.parentElement && root.parentElement.querySelector('.proj-canvas-body');
      if (board) board.animate([{ transform: 'none', filter: 'none', opacity: 1 }, { transform: 'scale(.97)', filter: 'blur(2px)', opacity: .55 }], { duration: 560, easing: ease });
      const title = root.querySelector('.tp-title');
      if (title && from.title) {
        const tr = title.getBoundingClientRect();
        const g = document.createElement('div');
        g.className = 'tp-ghost';
        g.textContent = from.title.text;
        g.style.cssText = `left:${from.title.rect.left}px;top:${from.title.rect.top}px;font-size:${from.title.size}px;line-height:1.3;`;
        document.body.appendChild(g);
        const k = parseFloat(getComputedStyle(title).fontSize) / from.title.size;
        title.style.opacity = '0';
        g.animate([{ transform: 'translate(0,0) scale(1)' }, { transform: `translate(${tr.left - from.title.rect.left}px,${tr.top - from.title.rect.top}px) scale(${k})` }],
          { duration: 560, easing: ease, fill: 'forwards' })
          .finished.catch(() => {}).then(() => { g.remove(); title.style.opacity = ''; });
      }
    },
    // Full page -> quick view: content fades fast, the sheet shrinks back to the side panel's place on the right.
    taskPageLeave(root) {
      if (!root || !root.animate || reducedMotion()) return Promise.resolve();
      const r = root.getBoundingClientRect();
      const left = Math.max(0, r.width - Math.min(480, r.width));
      const ease = 'cubic-bezier(.32,.72,0,1)';
      root.querySelectorAll('.tp-main-in, .tp-side, .tp-foot').forEach(el => el.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 140, fill: 'forwards' }));
      const a = root.animate([
        { clipPath: 'inset(0px 0px 0px 0px round 0px)' },
        { clipPath: `inset(10px 10px 10px ${left}px round 18px)` }
      ], { duration: 340, easing: ease, fill: 'forwards' });
      return a.finished.catch(() => {});
    },
    // ↑/↓ on the full page: the work column slides in from the direction of travel.
    taskPageStep(root, delta) {
      if (!root || reducedMotion()) return;
      root.style.setProperty('--tp-dir', delta > 0 ? '14px' : '-14px');
      root.classList.remove('tp-swap'); void root.offsetWidth; root.classList.add('tp-swap');
      setTimeout(() => root.classList.remove('tp-swap'), 450);
    },
    blurActive() { const a = document.activeElement; if (a && a.blur) a.blur(); },
    focusById(id) { const el = document.getElementById(id); if (el) { el.focus(); const v = el.value || ''; try { el.setSelectionRange(v.length, v.length); } catch (_) { } } },
    // Called by the side panel's maximize button just before the page swaps it out: remember where it was.
    rememberPanel() {
      const p = document.getElementById('pv2-panel');
      if (!p) { lastPanel = null; return; }
      const h = p.querySelector('.pv2p-h h2');
      lastPanel = {
        rect: p.getBoundingClientRect(),
        title: h ? { rect: h.getBoundingClientRect(), text: h.textContent, size: parseFloat(getComputedStyle(h).fontSize) || 18 } : null
      };
    },
  });

  // ---- Table field-column resize: pointer capture on the handle, widths live in a CSS variable while dragging,
  // C# only hears the final width (one call per drop), so moving the mouse never renders the table. ----
  const colSt = new WeakMap();
  Object.assign(window.ipopPv2, {
    colResize(root, dotNet) {
      if (!root) return;
      const old = colSt.get(root); if (old) old.off();
      let d = null;
      const down = e => {
        const h = e.target.closest && e.target.closest('.plist-col-resize[data-fid]');
        if (!h || !root.contains(h) || e.button !== 0) return;
        const head = h.closest('.pv2-field-heading');
        d = { h, fid: h.dataset.fid, x0: e.clientX, w0: Math.round(head ? head.getBoundingClientRect().width : 140), w: null };
        try { h.setPointerCapture(e.pointerId); } catch (_) { }
        root.classList.add('is-resizing');
        e.preventDefault();
      };
      const move = e => {
        if (!d) return;
        const w = Math.max(80, Math.min(480, Math.round(d.w0 + e.clientX - d.x0)));
        if (w === d.w) return;
        d.w = w;
        root.style.setProperty('--pv2fw-' + d.fid, w + 'px');
      };
      const up = e => {
        if (!d) return;
        const x = d; d = null;
        try { x.h.releasePointerCapture(e.pointerId); } catch (_) { }
        root.classList.remove('is-resizing');
        if (x.w == null || e.type === 'pointercancel') { root.style.removeProperty('--pv2fw-' + x.fid); return; }
        // Keep the dragged width until .NET has rendered it inline, then drop the override.
        dotNet.invokeMethodAsync('ColumnResized', x.fid, x.w)
          .catch(() => { }).finally(() => root.style.removeProperty('--pv2fw-' + x.fid));
      };
      root.addEventListener('pointerdown', down);
      root.addEventListener('pointermove', move);
      root.addEventListener('pointerup', up);
      root.addEventListener('pointercancel', up);
      colSt.set(root, { off() { root.removeEventListener('pointerdown', down); root.removeEventListener('pointermove', move); root.removeEventListener('pointerup', up); root.removeEventListener('pointercancel', up); colSt.delete(root); } });
    },
    colResizeOff(root) { const s = root && colSt.get(root); if (s) s.off(); },
  });

  // ---- Stage 4 (web B): sliding knob for the page segmented controls (.pv2pg-seg) ----
  Object.assign(window.ipopPv2, {
    pgSeg() {
      document.querySelectorAll('.pv2pg-seg').forEach(seg => {
        const k = seg.querySelector('.pv2pg-knob'), on = seg.querySelector('button.on');
        if (!k || !on || on.offsetWidth === 0) return;
        k.style.left = on.offsetLeft + 'px';
        k.style.width = on.offsetWidth + 'px';
      });
    },
  });
  // ---- Task panel evidence: Ctrl+V and drag & drop feed the panel's own file input (same limits, progress, errors) ----
  (function () {
    if (window.__pv2Evidence) return;
    window.__pv2Evidence = true;
    const input = () => document.querySelector('#pv2-panel [data-sec="files"] input[type="file"]');
    const isTyping = t => !!t && !!t.tagName && (/^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName) || t.isContentEditable);
    const give = files => {
      const inp = input();
      if (!inp || !files || !files.length) return false;
      const dt = new DataTransfer();
      Array.from(files).forEach((f, i) => {
        // Clipboard screenshots arrive as "image.png"; give them a unique name.
        const named = (f.name === 'image.png' || !f.name) ? new File([f], 'pasted-' + Date.now() + '-' + i + '.' + ((f.type.split('/')[1] || 'png').replace('jpeg', 'jpg')), { type: f.type }) : f;
        dt.items.add(named);
      });
      inp.files = dt.files;
      inp.dispatchEvent(new Event('change', { bubbles: true }));
      return true;
    };
    const hasFiles = e => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
    let depth = 0;
    const panel = () => document.getElementById('pv2-panel');
    const clear = () => { depth = 0; const p = panel(); if (p) p.classList.remove('pv2p-dropping'); };
    document.addEventListener('paste', e => {
      if (!input() || isTyping(e.target)) return;
      const files = e.clipboardData && e.clipboardData.files;
      if (files && files.length && give(files)) e.preventDefault();
    });
    document.addEventListener('dragenter', e => {
      const p = panel();
      if (!p || !input() || !hasFiles(e) || !p.contains(e.target)) return;
      depth++; p.classList.add('pv2p-dropping');
    });
    document.addEventListener('dragover', e => {
      const p = panel();
      if (p && input() && hasFiles(e) && p.contains(e.target)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
    });
    document.addEventListener('dragleave', e => {
      const p = panel();
      if (!p || !p.contains(e.target)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) p.classList.remove('pv2p-dropping');
    });
    document.addEventListener('drop', e => {
      const p = panel();
      if (!p || !input() || !hasFiles(e) || !p.contains(e.target)) { clear(); return; }
      e.preventDefault();
      give(e.dataTransfer.files);
      clear();
    });
    document.addEventListener('dragend', clear);
  })();
})();
