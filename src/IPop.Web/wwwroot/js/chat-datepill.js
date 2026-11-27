// Chat (BIGIS 12, owner revision): while the list scrolls, a small date pill floats in an empty
// 40px strip at the top of the messages (the list fades out under that strip via .dp-scrolling),
// so the pill never covers a bubble. It hides 1.2 s after scrolling stops. Pure part exported
// for tests/chat-datepill.test.cjs.
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
      const flow = list.parentElement;
      let timer = null, frame = 0, lastTop = list.scrollTop;
      const hide = () => {
        pill.classList.remove('on');
        if (flow) flow.classList.remove('dp-scrolling');
      };
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
        if (pill.textContent !== label) pill.textContent = label;
        pill.style.top = (list.offsetTop + 8) + 'px';
        pill.classList.add('on');
        if (flow) flow.classList.add('dp-scrolling');
        clearTimeout(timer);
        timer = setTimeout(hide, HIDE_MS);
      };
      list.addEventListener('scroll', () => { if (!frame) frame = root.requestAnimationFrame(update); }, { passive: true });
    },
  };
})(typeof window !== 'undefined' ? window : null);
