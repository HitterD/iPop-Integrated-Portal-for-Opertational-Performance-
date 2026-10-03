// EmojiPicker outside-click helper (loaded on demand by Components/EmojiPicker.razor via import()).
// A capture-phase pointerdown listener closes the picker without a scrim, so the first click on
// the editor still lands. Clicks inside the picker, or inside an element matching `ignore`
// (the caller's toggle button / wrapper), are left alone.
const active = new Map();

export function attach(el, dotnet, ignore) {
  detach(el);
  const handler = e => {
    const t = e.target;
    if (!(t instanceof Element) || el.contains(t)) return;
    if (ignore && t.closest(ignore)) return;
    dotnet.invokeMethodAsync("OutsidePointerDown");
  };
  document.addEventListener("pointerdown", handler, true);
  // Quick reply opens above its smile button; the compose editor's popup opens below the toolbar's
  // emoji button. Both are placed in viewport coordinates so the composer's overflow:hidden body
  // cannot clip them.
  const wrapper = el.closest('.quick-emoji-pop, .mrte-emoji-pop');
  const compose = !!wrapper && wrapper.classList.contains('mrte-emoji-pop');
  const place = () => {
    if (!wrapper) return;
    const button = compose ? wrapper.parentElement.querySelector('.mrte-emoji-btn') : wrapper.parentElement;
    if (!button) return;
    const anchor = button.getBoundingClientRect();
    const width = Math.min(344, innerWidth - 16);
    const height = el.getBoundingClientRect().height;
    const left = Math.max(8, Math.min(compose ? anchor.right - width : anchor.left, innerWidth - width - 8));
    const above = anchor.top - height - 6;
    const below = anchor.bottom + 6;
    const top = compose
      ? (below + height <= innerHeight - 8 || above < 8 ? Math.max(8, Math.min(below, innerHeight - height - 8)) : above)
      : (above >= 8 ? above : Math.min(below, Math.max(8, innerHeight - height - 8)));
    wrapper.style.cssText = `position:fixed;left:${left}px;top:${top}px;right:auto;bottom:auto;width:${width}px;z-index:125`;
    // A transformed ancestor (the maximized composer is centred with translate) becomes the
    // containing block for position:fixed; shift by whatever offset that introduced.
    const r = wrapper.getBoundingClientRect();
    if (Math.abs(r.left - left) > 0.5 || Math.abs(r.top - top) > 0.5)
      wrapper.style.cssText = `position:fixed;left:${2 * left - r.left}px;top:${2 * top - r.top}px;right:auto;bottom:auto;width:${width}px;z-index:125`;
  };
  place();
  window.addEventListener('resize', place);
  // Re-anchor when the page/thread scrolls, but not for scrolling inside the picker itself.
  const onScroll = e => { if (!(e.target instanceof Node) || !el.contains(e.target)) place(); };
  document.addEventListener('scroll', onScroll, true);
  active.set(el, { handler, place, onScroll });
}

export function detach(el) {
  const state = active.get(el);
  if (state) {
    document.removeEventListener("pointerdown", state.handler, true);
    window.removeEventListener('resize', state.place);
    document.removeEventListener('scroll', state.onScroll, true);
  }
  active.delete(el);
}
