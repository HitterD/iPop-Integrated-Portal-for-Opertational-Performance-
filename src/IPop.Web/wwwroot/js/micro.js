// App-wide micro-interactions that need the DOM (approved demo: docs/mockups/ipop-apple/micro-interactions.html).
// Everything here is decoration: it observes what Blazor renders and adds motion around it, never
// changes state. Respects prefers-reduced-motion. Exposed as window.ipopMicro for components that
// want a one-off effect (pop, bump, row exit, fly, count-up).
window.ipopMicro = (() => {
  const reduced = () => !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);
  const ease = 'cubic-bezier(.16,1,.3,1)';

  /** Restart a CSS animation class on an element. */
  function replay(el, cls) {
    if (!el || reduced()) return;
    el.classList.remove(cls);
    void el.offsetWidth;
    el.classList.add(cls);
    el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
  }

  // ── 5. Sliding indicator for segmented controls and tabs ──
  const SLIDERS = [
    ['.apple-segmented', '.seg-btn', '.active', ''],
    ['.cal-seg', '.cal-seg-item', '.on', ''],
    ['.seg', '.seg-item, button', '.on, .active', ''],
    ['.nm-tabs', '.nm-tab:not(.cat)', '.on', 'mi-pill'],
    ['.size-seg', 'button', '.on', ''],
    ['.fx-seg', 'button', '.on, .active', ''],
    ['.adm-seg', 'button', '.on, [aria-checked="true"]', ''],
    ['.nt-seg', 'button', '.on, .active', ''],
    ['.td-seg', 'button', '.on, .active', ''],
  ];
  function placeSlider(root, itemSel, onSel) {
    let ind = root.querySelector(':scope > .mi-ind');
    const on = [...root.querySelectorAll(':scope > ' + itemSel.split(',').map(s => s.trim()).join(', :scope > '))].find(b => b.matches(onSel));
    if (!on) { if (ind) ind.style.opacity = '0'; root.classList.remove('mi-live'); return; }
    if (!ind) {
      ind = document.createElement('span');
      ind.className = 'mi-ind';
      ind.setAttribute('aria-hidden', 'true');
      ind.style.transition = 'none';
      root.prepend(ind);
      requestAnimationFrame(() => { ind.style.transition = ''; });
    }
    ind.style.opacity = '1';
    ind.style.left = on.offsetLeft + 'px';
    ind.style.top = on.offsetTop + 'px';
    ind.style.width = on.offsetWidth + 'px';
    ind.style.height = on.offsetHeight + 'px';
    root.classList.add('mi-live');
  }
  function scanSliders() {
    for (const [rootSel, itemSel, onSel, extra] of SLIDERS) {
      for (const root of document.querySelectorAll(rootSel)) {
        if (!root.offsetParent) continue;
        if (!root.classList.contains('mi-slider')) { root.classList.add('mi-slider'); if (extra) root.classList.add(extra); }
        placeSlider(root, itemSel, onSel);
      }
    }
  }

  // ── 21. Sidebar glide ──
  function placeGlide() {
    const side = document.querySelector('.sidebar');
    if (!side) return;
    const active = side.querySelector('.nav-item.active');
    let glide = side.querySelector(':scope > .nav-glide');
    if (!active) { if (glide) glide.style.opacity = '0'; return; }
    if (!glide) {
      glide = document.createElement('span');
      glide.className = 'nav-glide';
      glide.setAttribute('aria-hidden', 'true');
      glide.style.transition = 'none';
      side.style.position = side.style.position || 'relative';
      side.prepend(glide);
      side.classList.add('mi-glide');
      requestAnimationFrame(() => { glide.style.transition = ''; });
    }
    const s = side.getBoundingClientRect(), a = active.getBoundingClientRect();
    glide.style.opacity = '1';
    glide.style.left = (a.left - s.left) + 'px';
    glide.style.right = 'auto';
    glide.style.width = a.width + 'px';
    glide.style.height = a.height + 'px';
    glide.style.transform = `translateY(${a.top - s.top + side.scrollTop}px)`;
  }

  // ── 2. Badge bump when the number changes ──
  const BADGES = '.nav-count, .bell-btn .badge, .nm-badge, .tab-badge, .seg-badge, .nav-badge';
  const lastCount = new WeakMap();
  function scanBadges() {
    for (const b of document.querySelectorAll(BADGES)) {
      const v = b.textContent.trim();
      const prev = lastCount.get(b);
      lastCount.set(b, v);
      if (prev !== undefined && prev !== v && (parseInt(v, 10) || 0) > (parseInt(prev, 10) || 0)) replay(b, 'mi-bump');
    }
  }

  // ── 25. Tooltips: icon-only buttons with a title show a styled tip after a short pause ──
  let tipHost, tipTimer, tipFor;
  function tipTarget(el) {
    const btn = el && el.closest && el.closest('button[title], a[title], [data-mi-tip]');
    if (!btn) return null;
    const text = btn.getAttribute('data-mi-tip') || btn.getAttribute('title');
    if (!text) return null;
    // Only icon-only controls: a visible text label already explains itself.
    const label = [...btn.childNodes].some(n => n.nodeType === 3 && n.textContent.trim()) ||
      [...btn.children].some(c => !c.matches('svg, i, .lucide, .badge, .nav-count') && c.textContent.trim());
    if (label && !btn.hasAttribute('data-mi-tip')) return null;
    if (btn.title) { btn.setAttribute('data-mi-tip', btn.title); btn.setAttribute('aria-label', btn.getAttribute('aria-label') || btn.title); btn.removeAttribute('title'); }
    return btn;
  }
  function showTip(btn) {
    if (!tipHost) { tipHost = document.createElement('div'); tipHost.className = 'mi-tip-host'; tipHost.setAttribute('role', 'tooltip'); document.body.appendChild(tipHost); }
    tipHost.textContent = btn.getAttribute('data-mi-tip');
    const r = btn.getBoundingClientRect();
    tipHost.style.left = '0px'; tipHost.style.top = '0px';
    tipHost.classList.add('on');
    const w = tipHost.offsetWidth, h = tipHost.offsetHeight;
    let x = r.left + r.width / 2 - w / 2, y = r.top - h - 8;
    if (y < 6) y = r.bottom + 8;
    x = Math.max(6, Math.min(innerWidth - w - 6, x));
    tipHost.style.left = x + 'px'; tipHost.style.top = y + 'px';
  }
  function hideTip() { clearTimeout(tipTimer); tipFor = null; if (tipHost) tipHost.classList.remove('on'); }
  function onOver(e) {
    const btn = tipTarget(e.target);
    if (btn === tipFor) return;
    hideTip();
    if (!btn || matchMedia('(hover: none)').matches) return;
    tipFor = btn;
    tipTimer = setTimeout(() => { if (tipFor === btn && btn.isConnected) showTip(btn); }, 420);
  }

  // ── 22. Page enter on module switch ──
  let lastPath = location.pathname.split('/')[1];
  function onNavigated() {
    const seg = location.pathname.split('/')[1];
    if (seg === lastPath) return;
    lastPath = seg;
    const main = document.querySelector('.main');
    const page = main && [...main.children].find(c => !c.matches('header, .topbar, .float-stack, .push-banner, .offline-banner, script'));
    hideTip();
    if (page) replay(page, 'mi-page-enter');
  }

  // ── 29. Presence: ripple when a person turns online ──
  const lastPresence = new WeakMap();
  function scanPresence() {
    for (const d of document.querySelectorAll('.presence, .sdot, .pdot, .status-dot')) {
      const on = d.classList.contains('on');
      const was = lastPresence.get(d);
      lastPresence.set(d, on);
      if (was === false && on) replay(d, 'mi-online');
    }
  }

  // ── 16. Copy feedback: buttons labelled "Copy …" swap to a tick briefly ──
  function onClick(e) {
    const btn = e.target.closest && e.target.closest('button');
    if (!btn) return;
    const label = (btn.getAttribute('aria-label') || btn.getAttribute('data-mi-tip') || btn.textContent || '').trim();
    if (/^copy\b/i.test(label) && !btn.classList.contains('mi-copied')) {
      const icon = btn.querySelector('svg.lucide');
      btn.classList.add('mi-copied');
      let restore;
      if (icon && window.lucide && lucide.icons && lucide.icons.Check) {
        const tick = lucide.createElement(lucide.icons.Check);
        tick.setAttribute('class', icon.getAttribute('class'));
        icon.replaceWith(tick);
        restore = () => tick.replaceWith(icon);
      }
      setTimeout(() => { btn.classList.remove('mi-copied'); restore && restore(); }, 1500);
    }
    // 26. Theme toggle spin
    if (btn.matches('.theme-btn:not(.tut-help-btn)')) { btn.classList.add('mi-theme'); setTimeout(() => replay(btn, 'mi-spin'), 30); }
  }

  // ── 12. Windows file drag: highlight any registered drop zone ──
  const dropZones = new Set();
  function overlayFor(zone) {
    let ov = zone.querySelector(':scope > .mi-drop-ov');
    if (!ov) {
      ov = document.createElement('div');
      ov.className = 'mi-drop-ov';
      ov.setAttribute('aria-hidden', 'true');
      ov.innerHTML = `<div><div class="mi-drop-ic"></div><b>${zone.dataset.miDropTitle || 'Drop to attach'}</b><small>${zone.dataset.miDropHint || 'Images, PDF, Office files'}</small></div>`;
      if (window.lucide && lucide.icons && lucide.icons.Upload) ov.querySelector('.mi-drop-ic').appendChild(lucide.createElement(lucide.icons.Upload));
      zone.appendChild(ov);
    }
    return ov;
  }
  /** Mark an element as a file drop target (Chat composer, Mail composer, task drawer, Notes, News). */
  function registerDrop(zone, title, hint) {
    if (!zone || dropZones.has(zone)) return;
    zone.classList.add('mi-drop-zone');
    if (title) zone.dataset.miDropTitle = title;
    if (hint) zone.dataset.miDropHint = hint;
    dropZones.add(zone);
    let depth = 0;
    const isFiles = e => e.dataTransfer && [...(e.dataTransfer.types || [])].includes('Files');
    zone.addEventListener('dragenter', e => { if (!isFiles(e)) return; depth++; overlayFor(zone); zone.classList.add('mi-drag-over'); });
    zone.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; zone.classList.remove('mi-drag-over'); } });
    zone.addEventListener('drop', () => { depth = 0; zone.classList.remove('mi-drag-over'); }, true);
    window.addEventListener('dragend', () => { depth = 0; zone.classList.remove('mi-drag-over'); });
  }
  function unregisterDrop(zone) { if (zone) dropZones.delete(zone); }

  // ── 14. Paste placeholder (callers insert it while an image is processed) ──
  function pastePlaceholder(container) {
    const ph = document.createElement('span');
    ph.className = 'mi-paste-ph';
    ph.textContent = 'Adding image…';
    ph.contentEditable = 'false';
    return ph;
  }

  // ── Row exit for keyed lists: run the slide-out, then let Blazor remove it ──
  function leave(el) {
    return new Promise(res => {
      if (!el || reduced()) return res();
      el.style.maxHeight = el.offsetHeight + 'px';
      el.classList.add('mi-leaving');
      setTimeout(res, 300);
    });
  }
  function leaveSelector(sel) { return leave(document.querySelector(sel)); }

  /** Arc an element's clone into a target (favorites panel, bell). */
  function fly(fromEl, toEl, opts) {
    return new Promise(res => {
      if (!fromEl || !toEl || reduced() || !fromEl.animate) return res();
      const a = fromEl.getBoundingClientRect(), b = toEl.getBoundingClientRect();
      const ghost = fromEl.cloneNode(true);
      Object.assign(ghost.style, { position: 'fixed', left: a.left + 'px', top: a.top + 'px', width: a.width + 'px', height: a.height + 'px', margin: '0', zIndex: 10040, pointerEvents: 'none' });
      document.body.appendChild(ghost);
      const tx = b.left + b.width / 2 - (a.left + a.width / 2), ty = b.top + b.height / 2 - (a.top + a.height / 2);
      const end = (opts && opts.endScale) || .4;
      ghost.animate([
        { transform: 'none', opacity: 1 },
        { transform: `translate(${tx * .5}px, ${ty * .5 - 40}px) scale(1.1)`, offset: .5 },
        { transform: `translate(${tx}px, ${ty}px) scale(${end})`, opacity: .6 },
      ], { duration: 620, easing: ease }).onfinish = () => { ghost.remove(); replay(toEl, 'mi-bump'); res(); };
    });
  }

  /** Count a number up inside an element (Dashboard/Admin stats). */
  function countUp(el, to, ms) {
    if (!el) return;
    const target = Number(to) || 0;
    if (reduced()) { el.textContent = target.toLocaleString('en-US'); return; }
    const t0 = performance.now(), dur = ms || 750;
    const f = t => { const k = Math.min(1, (t - t0) / dur), v = Math.round(target * (1 - Math.pow(1 - k, 3)));
      el.textContent = v.toLocaleString('en-US'); if (k < 1) requestAnimationFrame(f); };
    requestAnimationFrame(f);
  }

  /** FLIP: call before a reorder, then call the returned function after the DOM moved. */
  function flipPrepare(container) {
    if (!container || reduced()) return () => {};
    const first = new Map([...container.children].map(e => [e.dataset.flipKey || e, e.getBoundingClientRect()]));
    return () => {
      for (const e of container.children) {
        const a = first.get(e.dataset.flipKey || e); if (!a) continue;
        const b = e.getBoundingClientRect(), dx = a.left - b.left, dy = a.top - b.top;
        if (dx || dy) e.animate([{ transform: `translate(${dx}px,${dy}px)` }, { transform: 'none' }], { duration: 380, easing: ease });
      }
    };
  }

  /** Photo grows from its thumbnail. */
  function zoomFrom(thumb, big) {
    if (!thumb || !big || reduced() || !big.animate) return;
    const a = thumb.getBoundingClientRect(), b = big.getBoundingClientRect();
    if (!b.width || !b.height) return;
    big.animate([
      { transform: `translate(${a.left - b.left}px,${a.top - b.top}px) scale(${a.width / b.width},${a.height / b.height})`, opacity: .6 },
      { transform: 'none', opacity: 1 },
    ], { duration: 320, easing: ease });
  }

  // ── Global search: the active result glides between rows (Spotlight) ──
  function placePaletteSel() {
    const box = document.querySelector('.cp-results');
    if (!box) return;
    const sel = box.querySelector(':scope > .cp-sel'), on = box.querySelector('.cp-row.active');
    if (!sel) return;
    if (!on) { sel.style.opacity = '0'; box.classList.remove('mi-live'); return; }
    sel.style.top = on.offsetTop + 'px';
    sel.style.height = on.offsetHeight + 'px';
    sel.style.opacity = '1';
    box.classList.add('mi-live');
  }

  // ── Observe renders (debounced to one frame) ──
  let queued = false;
  function tick() {
    queued = false;
    scanSliders();
    placeGlide();
    placePaletteSel();
    scanBadges();
    scanPresence();
    onNavigated();
  }
  function schedule() { if (!queued) { queued = true; requestAnimationFrame(tick); } }

  function start() {
    const mo = new MutationObserver(schedule);
    mo.observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'aria-selected', 'aria-checked'], characterData: true });
    addEventListener('resize', schedule);
    document.addEventListener('mouseover', onOver, true);
    document.addEventListener('focusin', e => { const b = tipTarget(e.target); if (b && b.matches(':focus-visible')) { hideTip(); tipFor = b; showTip(b); } }, true);
    document.addEventListener('focusout', hideTip, true);
    document.addEventListener('mousedown', hideTip, true);
    document.addEventListener('scroll', hideTip, true);
    document.addEventListener('click', onClick, true);
    schedule();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();

  return { replay, registerDrop, unregisterDrop, pastePlaceholder, leave, leaveSelector, fly, countUp, flipPrepare, zoomFrom, reduced,
    pop: el => replay(el, 'mi-pop'), bump: el => replay(el, 'mi-bump'), shake: el => replay(el, 'mi-shake'), land: el => replay(el, 'mi-land'),
    saved: el => replay(el, 'mi-saved'), popSelector: sel => replay(document.querySelector(sel), 'mi-pop'), landSelector: sel => replay(document.querySelector(sel), 'mi-land') };
})();
