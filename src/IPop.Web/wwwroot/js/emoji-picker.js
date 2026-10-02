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
  const wrapper = el.closest('.quick-emoji-pop');
  const place = () => {
    if (!wrapper) return;
    const anchor = wrapper.parentElement.getBoundingClientRect();
    const width = Math.min(344, innerWidth - 16);
    const height = el.getBoundingClientRect().height;
    const left = Math.max(8, Math.min(anchor.left, innerWidth - width - 8));
    const above = anchor.top - height - 6;
    const top = above >= 8 ? above : Math.min(anchor.bottom + 6, Math.max(8, innerHeight - height - 8));
    wrapper.style.cssText = `position:fixed;left:${left}px;top:${top}px;bottom:auto;width:${width}px;z-index:125`;
  };
  place();
  window.addEventListener('resize', place);
  document.addEventListener('scroll', place, true);
  active.set(el, { handler, place });
}

export function detach(el) {
  const state = active.get(el);
  if (state) {
    document.removeEventListener("pointerdown", state.handler, true);
    window.removeEventListener('resize', state.place);
    document.removeEventListener('scroll', state.place, true);
  }
  active.delete(el);
}
