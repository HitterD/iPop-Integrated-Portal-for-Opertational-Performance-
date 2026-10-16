// Phone Notes keyboard accessory. Does not move or replace the collaborative editor DOM.
(function (root) {
  const toolbarOffset = (vv, innerHeight) => Math.min(0, vv.offsetTop + vv.height - innerHeight);
  const shouldShow = (focusInEditor, kbHeight) => !!focusInEditor && kbHeight > 120;
  const baselineHeight = (previous, width, height) => ({ width, height: !previous || Math.abs(previous.width - width) > 40 ? height : Math.max(previous.height, height) });
  if (typeof module === 'object' && module.exports) module.exports = { toolbarOffset, shouldShow, baselineHeight };
  if (!root) return;
  const handles = new WeakMap();
  root.ipop = root.ipop || {};
  root.ipop.notesKb = {
    attach(bar) {
      if (!bar || handles.has(bar)) return;
      let baseline = baselineHeight(null, root.innerWidth, root.innerHeight);
      const vv = root.visualViewport;
      const update = () => {
        const focused = document.activeElement?.closest('.nt-editor.ProseMirror');
        baseline = baselineHeight(baseline, root.innerWidth, root.innerHeight);
        const visible = vv || { offsetTop: 0, height: root.innerHeight };
        const keyboard = baseline.height - visible.height;
        const show = shouldShow(!!focused, keyboard) && root.ipop.mobile.isMobile();
        bar.classList.toggle('is-visible', show);
        bar.style.transform = `translateY(${toolbarOffset(visible, root.innerHeight)}px)`;
        if (!show) return;
        const sel = root.getSelection();
        const rect = sel?.rangeCount ? sel.getRangeAt(0).getBoundingClientRect() : null;
        const scroll = focused.closest('.nt-doc-scroll');
        if (rect?.height && scroll) {
          const overlap = rect.bottom + 12 - (visible.offsetTop + visible.height - bar.offsetHeight);
          if (overlap > 0) scroll.scrollTop += overlap;
        }
      };
      const prevent = e => e.preventDefault(); // retain ProseMirror selection on toolbar taps
      bar.addEventListener('pointerdown', prevent);
      document.addEventListener('focusin', update);
      document.addEventListener('focusout', update);
      document.addEventListener('selectionchange', update);
      root.addEventListener('resize', update);
      vv?.addEventListener('resize', update);
      vv?.addEventListener('scroll', update);
      update();
      handles.set(bar, () => {
        bar.removeEventListener('pointerdown', prevent);
        document.removeEventListener('focusin', update);
        document.removeEventListener('focusout', update);
        document.removeEventListener('selectionchange', update);
        root.removeEventListener('resize', update);
        vv?.removeEventListener('resize', update);
        vv?.removeEventListener('scroll', update);
      });
    },
    detach(bar) { handles.get(bar)?.(); handles.delete(bar); }
  };
})(typeof window !== 'undefined' ? window : null);
