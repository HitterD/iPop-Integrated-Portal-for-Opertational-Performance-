// Tickets module motion (docs/mockups/ipop-apple/tickets.html, tab 7). Decoration only: Blazor owns
// the state; this places the selection highlight, plays FLIP when rows change section, and runs
// one-shot effects. Every effect is skipped under prefers-reduced-motion.
window.ipopTickets = (() => {
  const reduced = () => !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);
  const $ = s => (typeof s === 'string' ? document.querySelector(s) : s);
  const smooth = 'cubic-bezier(0.32,0.72,0,1)';

  function replay(sel, cls) {
    const el = $(sel);
    if (!el || reduced()) return;
    el.classList.remove(cls);
    void el.offsetWidth;
    el.classList.add(cls);
    el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
  }

  /** The blue selection highlight glides to the selected row (380 ms). */
  function glide(rootSel, instant) {
    const root = $(rootSel);
    if (!root) return;
    const ind = root.querySelector(':scope > .tk-selind');
    const row = root.querySelector('.tk.sel');
    if (!ind) return;
    if (!row || !row.offsetParent) { ind.style.opacity = '0'; root.classList.remove('tk-glide'); return; }
    const jump = instant || reduced() || ind.style.opacity !== '1';
    if (jump) ind.style.transition = 'none';
    ind.style.opacity = '1';
    ind.style.top = row.offsetTop + 'px';
    ind.style.height = row.offsetHeight + 'px';
    root.classList.add('tk-glide');
    if (jump) { void ind.offsetWidth; ind.style.transition = ''; }
  }

  /** FLIP, step 1: remember where each row is before Blazor re-renders the list. */
  function flipFirst(rootSel) {
    const root = $(rootSel);
    if (!root || reduced()) return;
    root.__tkFlip = new Map([...root.querySelectorAll('.tk[data-id]')].map(e => [e.dataset.id, e.getBoundingClientRect().top]));
  }

  /** FLIP, step 2: rows that moved slide from their old spot (450 ms). */
  function flipPlay(rootSel) {
    const root = $(rootSel);
    if (!root) return;
    const before = root.__tkFlip;
    root.__tkFlip = null;
    if (!before || reduced()) return;
    for (const e of root.querySelectorAll('.tk[data-id]')) {
      const old = before.get(e.dataset.id);
      if (old == null) continue;
      const d = old - e.getBoundingClientRect().top;
      if (Math.abs(d) < 1 || !e.animate) continue;
      e.animate([{ transform: `translateY(${d}px)` }, { transform: 'none' }], { duration: 450, easing: smooth });
    }
    // The highlight follows the selected row once it has landed.
    setTimeout(() => glide(root, false), 20);
  }

  /** Validation: scroll to the field, ring it (CSS), shake once and focus its input. */
  function focusError(sel) {
    const el = $(sel);
    if (!el) return;
    el.scrollIntoView({ block: 'center', behavior: reduced() ? 'auto' : 'smooth' });
    setTimeout(() => {
      replay(el, 'mi-shake');
      const i = el.querySelector('input:not([type=file]), textarea');
      if (i) i.focus({ preventScroll: true });
    }, reduced() ? 0 : 280);
  }

  function focus(sel, delay) {
    setTimeout(() => { const el = $(sel); if (el) el.focus({ preventScroll: true }); }, reduced() ? 0 : (delay || 0));
  }

  /// After a quick template fills the description: focus it with the caret after the text, where
  /// the template leaves a blank to complete ("Area/Lantai: ").
  function caretEnd(sel) {
    requestAnimationFrame(() => {
      const el = $(sel); if (!el) return;
      el.focus({ preventScroll: true });
      const n = el.value.length; el.setSelectionRange(n, n);
    });
  }

  function scrollTop(sel) {
    const el = $(sel);
    if (el) el.scrollTo({ top: 0, behavior: reduced() ? 'auto' : 'smooth' });
  }

  /** Row/chip exit before Blazor drops it (ipopMicro's collapse). */
  function leave(sel) {
    return window.ipopMicro ? ipopMicro.leaveSelector(sel) : Promise.resolve();
  }

  /** Open/All switch: rows and section labels re-enter staggered (25 ms apart). */
  function stagger(sel) {
    const el = $(sel);
    if (!el || reduced()) return;
    el.classList.remove('tk-stagger');
    void el.offsetWidth;
    el.classList.add('tk-stagger');
    clearTimeout(el.__tkStagger);
    el.__tkStagger = setTimeout(() => el.classList.remove('tk-stagger'), 480);
  }

  /** Dashboard widget numbers count up once per visit. */
  function countWidget(animate) {
    document.querySelectorAll('.tkw-n b[data-count]:not([data-done])').forEach(el => {
      el.dataset.done = '1';
      const to = +el.dataset.count || 0;
      if (!animate || reduced() || !to || !window.ipopMicro) { el.textContent = to; return; }
      el.textContent = '0';
      ipopMicro.countUp(el, to, 650);
    });
  }

  return { replay, stagger, glide, flipFirst, flipPlay, focusError, focus, caretEnd, scrollTop, leave, countWidget, reduced };
})();
