// Chat (BIGIS 12, owner revision): while the list scrolls, a small date pill floats in an empty
// 40px strip at the top of the messages (the list fades out under that strip while the pill is on),
// so the pill never covers a bubble. It hides 1.2 s after scrolling stops. Pure part exported
// for tests/chat-datepill.test.cjs.
// Perf: only the pill itself changes while scrolling. A class on an ancestor of the list, or new
// text in the pill, restyles all ~16k nodes of a long chat (Radzen's `[class*=" rz-"] *` and
// `:has()` rules), ~150 ms per scroll start/stop. So the mask hangs off `.date-float.on + .messages`
// and the label is an attribute drawn by CSS (`content: attr(data-label)`).
(function (root) {
  /** Label of the day the top of the viewport is in: the last separator at or above `top`. */
  function currentLabel(seps, top) {
    let label = null;
    for (const s of seps) {
      if (s.y <= top) label = s.label; else break;
    }
    return label;
  }

  if (typeof module === 'object' && module.exports) module.exports = { currentLabel };
  if (!root || !root.document) return;

  const HIDE_MS = 1200;
  const ipop = root.ipop = root.ipop || {};
  ipop.chatDatePill = {
    /** Bind once per messages element; `pill` is the empty .date-float the page renders. */
    bind(list, pill) {
      if (!list || !pill || list._dp) return;
      list._dp = true;
      let timer = null, frame = 0, lastTop = list.scrollTop;
      const hide = () => { if (pill.classList.contains('on')) pill.classList.remove('on'); };
      const update = () => {
        frame = 0;
        const top = list.scrollTop;
        const moved = Math.abs(top - lastTop) > 2;
        lastTop = top;
        if (!moved) return;
        const seps = [...list.querySelectorAll(':scope > .date-sep')].map(el => ({ y: el.offsetTop, label: el.textContent.trim() }));
        const label = currentLabel(seps, top + 8);
        // At the very top the real separator is visible: no floating copy needed.
        if (!label || top < 24) { hide(); return; }
        if (pill.dataset.label !== label) pill.dataset.label = label;
        const y = (list.offsetTop + 8) + 'px';
        if (pill.style.top !== y) pill.style.top = y;
        if (!pill.classList.contains('on')) pill.classList.add('on');
        clearTimeout(timer);
        timer = setTimeout(hide, HIDE_MS);
      };
      list.addEventListener('scroll', () => { if (!frame) frame = root.requestAnimationFrame(update); }, { passive: true });
    },
  };
})(typeof window !== 'undefined' ? window : null);
