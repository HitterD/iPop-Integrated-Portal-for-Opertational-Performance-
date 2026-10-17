// Phone Notes keyboard accessory. Does not move or replace the collaborative editor DOM.
(function (root) {
  const toolbarOffset = (vv, innerHeight) => Math.min(0, vv.offsetTop + vv.height - innerHeight);
  const shouldShow = (focusInEditor, kbHeight) => !!focusInEditor && kbHeight > 120;
  const baselineHeight = (previous, width, height, rotated = false) => ({ width, height: !previous ? height : Math.abs(previous.width - width) > 40 ? Math.max(height, rotated ? previous.width : 0) : Math.max(previous.height, height) });
  if (typeof module === 'object' && module.exports) module.exports = { toolbarOffset, shouldShow, baselineHeight };
  if (!root) return;
  // Start observing at script load, before any editor focus or component attachment can shrink
  // Android's layout viewport. Keep one shared measurement when a toolbar is recreated mid-edit.
  let viewportBaseline = baselineHeight(null, root.innerWidth, root.innerHeight);
  let orientation = root.screen?.orientation?.type;
  const measureViewport = () => {
    const nextOrientation = root.screen?.orientation?.type;
    const rotated = !!orientation && !!nextOrientation && orientation !== nextOrientation;
    const widthChanged = Math.abs(viewportBaseline.width - root.innerWidth) > 40;
    viewportBaseline = baselineHeight(viewportBaseline, root.innerWidth, root.innerHeight, rotated);
    // orientationchange may precede resize; consume the new orientation only with its dimensions.
    if (widthChanged || !orientation) orientation = nextOrientation;
  };
  root.addEventListener('resize', measureViewport);
  root.addEventListener('orientationchange', measureViewport);
  const handles = new WeakMap();
  root.ipop = root.ipop || {};
  root.ipop.notesKb = {
    attach(bar) {
      if (!bar || handles.has(bar)) return;
      const vv = root.visualViewport;
      const update = () => {
        const focused = document.activeElement?.closest('.nt-editor.ProseMirror');
        measureViewport();
        const visible = vv || { offsetTop: 0, height: root.innerHeight };
        const keyboard = viewportBaseline.height - visible.height;
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
