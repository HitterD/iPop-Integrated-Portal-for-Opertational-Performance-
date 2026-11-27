// Phone chat (BIGIS 12): swipe a message right = reply, left = Message info. The bubble follows the
// finger, an icon grows in behind it and turns solid past the threshold (ph-kit swipeDecision:
// 22 % of the row, 64-96 px), one haptic tick, then it snaps back. Long-press (ph-kit longPress)
// keeps opening the actions sheet; it cancels itself once the finger moves more than 10 px.
// Ignored: mostly-vertical moves (a scroll) and touches starting < 24 px from a screen edge
// (system back gesture). Pure parts are exported for tests/chat-swipe.test.cjs.
(function (root) {
  const EDGE = 24;

  /** A touch that starts this close to the left/right screen edge belongs to the OS back gesture. */
  function edgeStart(x, viewportWidth) {
    return x < EDGE || x > viewportWidth - EDGE;
  }

  /** First 8 px decide: horizontal (true), vertical/diagonal (false) or not yet (null). */
  function axis(mx, my) {
    if (Math.abs(mx) <= 8 && Math.abs(my) <= 8) return null;
    return Math.abs(mx) > Math.abs(my) * 1.2;
  }

  /** Rubber band past the threshold so the bubble never runs off with the finger. */
  function follow(dx, t) {
    const a = Math.abs(dx);
    return a > t ? Math.sign(dx) * (t + (a - t) * 0.35) : dx;
  }

  /** Threshold in px for a row of this width (same rule as ph-kit swipeDecision). */
  function threshold(width) {
    return Math.min(96, Math.max(64, width * 0.22));
  }

  if (typeof module === 'object' && module.exports) module.exports = { edgeStart, axis, follow, threshold };
  if (!root || !root.document) return;

  const REPLY_SVG = '<svg viewBox="0 0 24 24"><path d="M9 17 4 12l5-5"/><path d="M20 18v-2a4 4 0 0 0-4-4H4"/></svg>';
  const INFO_SVG = '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/></svg>';

  const ipop = root.ipop = root.ipop || {};
  ipop.chatSwipe = {
    /** Bind once per messages element. Calls ref[replyMethod](id) or ref[infoMethod](id). */
    bind(container, selector, ref, replyMethod, infoMethod) {
      if (!container || container._chatSw) return;
      container._chatSw = true;
      const kit = ipop.phone && ipop.phone.kit;
      const decide = kit ? kit.swipeDecision
        : (d, w) => (d >= threshold(w) ? 'right' : d <= -threshold(w) ? 'left' : null);
      let row = null, fg = null, ic = null, x0 = 0, y0 = 0, dx = 0, horiz = null, armed = false;

      const icon = side => {
        if (ic && ic.dataset.side === side) return ic;
        if (ic) ic.remove();
        ic = document.createElement('span');
        ic.dataset.side = side;
        ic.className = 'sw-ic ' + (side === 'reply' ? 'l' : 'r');
        ic.setAttribute('aria-hidden', 'true');
        ic.innerHTML = side === 'reply' ? REPLY_SVG : INFO_SVG;
        row.appendChild(ic);
        return ic;
      };

      const reset = () => {
        if (fg) { fg.style.transition = ''; fg.style.transform = ''; }
        if (row) row.classList.remove('sw-active');
        if (ic) { const old = ic; old.style.opacity = '0'; setTimeout(() => old.remove(), 240); }
        row = fg = ic = null; horiz = null; dx = 0; armed = false;
      };

      container.addEventListener('pointerdown', e => {
        if (e.pointerType === 'mouse' || !e.isPrimary) return;
        const r = e.target.closest(selector);
        if (!r || !container.contains(r) || r.classList.contains('system')) return;
        if (e.target.closest('textarea, input, .seen-pop, .v6-react-pop')) return;
        if (edgeStart(e.clientX, root.innerWidth)) return;
        row = r; fg = r.querySelector(':scope > .msg-inner') || r;
        x0 = e.clientX; y0 = e.clientY; dx = 0; horiz = null; armed = false;
      }, { passive: true });

      container.addEventListener('pointermove', e => {
        if (!row) return;
        const mx = e.clientX - x0, my = e.clientY - y0;
        if (horiz === null) {
          horiz = axis(mx, my);
          if (horiz === false) { reset(); return; }
          if (horiz === null) return;
          row.classList.add('sw-active');
        }
        dx = Math.max(-140, Math.min(140, mx));
        const t = threshold(row.getBoundingClientRect().width);
        fg.style.transition = 'none';
        fg.style.transform = 'translateX(' + follow(dx, t) + 'px)';
        const el = icon(dx > 0 ? 'reply' : 'info');
        const p = Math.min(1, Math.abs(dx) / t);
        el.style.opacity = String(p);
        el.style.transform = 'scale(' + (0.4 + 0.6 * p) + ')';
        el.classList.toggle('arm', p >= 1);
        if (p >= 1 && !armed) { armed = true; try { if (root.navigator.vibrate) root.navigator.vibrate(8); } catch (err) { /* no haptics */ } }
        if (p < 1) armed = false;
      }, { passive: true });

      const end = e => {
        if (!row) return;
        const r = row, wasHoriz = horiz, d = dx;
        const side = wasHoriz && e.type === 'pointerup' ? decide(d, r.getBoundingClientRect().width) : null;
        reset();
        if (!side) return;
        // The swipe ends in a click on the row: swallow exactly that one (it would toggle the tools).
        const eat = ev => { ev.stopPropagation(); ev.preventDefault(); };
        document.addEventListener('click', eat, { capture: true, once: true });
        setTimeout(() => document.removeEventListener('click', eat, true), 350);
        const id = r.getAttribute('data-lp') || r.getAttribute('data-msg-id') || '';
        ref.invokeMethodAsync(side === 'right' ? replyMethod : infoMethod, id).catch(() => { });
      };
      container.addEventListener('pointerup', end, { passive: true });
      container.addEventListener('pointercancel', end, { passive: true });
    },
  };
})(typeof window !== 'undefined' ? window : null);
