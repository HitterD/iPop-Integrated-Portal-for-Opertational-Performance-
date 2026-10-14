// Mail recipients line (QA 3, spec 2026-10-14 §4). Each To/Cc line shows as many WHOLE items — "you", names,
// group chips (each capped at ~220 px by CSS) — as fit on one line, then "+N". Never squeezes every chip.
//
// Markup contract (MailRecipientsHeader.razor):
//   .mrh-line[data-field][data-shown]        the visible line; data-shown = items Blazor currently shows
//     > em                                   the "To"/"Cc" label
//     > .mrh-bccnote (optional)              fixed tail
//     > .mrh-msr[aria-hidden]                hidden measuring row: every item ([data-fit]) and a "+N" sample ([data-more])
// The browser measures, Blazor renders: OnFit(field, count) is called only when the count changes.
(function () {
  /// Pure: how many items (in order) fit in `available` px, leaving room for "+N" when some are left out.
  function fitCount({ available, widths, gap, moreWidth }) {
    const n = widths.length;
    if (n === 0) return 0;
    if (!(available > 0) || widths.some(w => !(w >= 0))) return 1;
    let used = 0;
    let fit = 0;
    for (let i = 0; i < n; i++) {
      used += (i ? gap : 0) + widths[i];
      const last = i === n - 1;
      if (used + (last ? 0 : gap + moreWidth) <= available) fit = i + 1;
      else if (last && used <= available) fit = n;
    }
    return Math.max(1, fit);
  }

  const api = { fitCount };
  if (typeof module !== 'undefined') { module.exports = api; return; }

  const bound = new Map();   // root element -> { ro, ref }

  function fit(root, ref) {
    if (!root || !root.isConnected) return;
    for (const line of root.querySelectorAll('.mrh-line[data-field]')) {
      const msr = line.querySelector(':scope > .mrh-msr');
      if (!msr) continue;
      const cs = getComputedStyle(line);
      const gap = parseFloat(cs.columnGap) || 4;
      let available = line.clientWidth - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
      for (const fixed of line.querySelectorAll(':scope > em, :scope > .mrh-bccnote')) {
        available -= fixed.getBoundingClientRect().width + gap;
      }
      const widths = [...msr.querySelectorAll(':scope > [data-fit]')].map(e => e.getBoundingClientRect().width);
      const more = msr.querySelector(':scope > [data-more]');
      const n = fitCount({ available: available - 1, widths, gap, moreWidth: more ? more.getBoundingClientRect().width : 40 });
      if (String(n) !== line.getAttribute('data-shown')) {
        ref.invokeMethodAsync('OnFit', line.getAttribute('data-field'), n).catch(() => { });
      }
    }
  }

  window.ipopMailFit = {
    /// Watches the header's width (pane resize, phone rotation) and fits now. Safe to call after every render.
    bind(root, ref) {
      if (!root) return;
      let b = bound.get(root);
      if (!b) {
        let raf = 0;
        const ro = typeof ResizeObserver === 'function'
          ? new ResizeObserver(() => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => fit(root, b.ref)); })
          : null;
        b = { ro, ref };
        bound.set(root, b);
        if (ro) ro.observe(root);
        if (document.fonts && document.fonts.ready) document.fonts.ready.then(() => fit(root, b.ref));
      }
      b.ref = ref;
      fit(root, ref);
    },
    unbind(root) {
      const b = bound.get(root);
      if (!b) return;
      if (b.ro) b.ro.disconnect();
      bound.delete(root);
    },
  };
})();
