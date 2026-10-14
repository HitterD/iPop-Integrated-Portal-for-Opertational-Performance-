// EmojiPicker outside-click + anchoring helper (loaded on demand by Components/EmojiPicker.razor via import()).
// A capture-phase pointerdown listener closes the picker without a scrim, so the first click on
// the editor still lands. Clicks inside the picker, or inside an element matching `ignore`
// (the caller's toggle button / wrapper), are left alone.
const active = new Map();
const MARGIN = 8, GAP = 6, MAX_WIDTH = 344;

/** Where the popup goes, in viewport px, or null while the anchor can't be seen (zero rect,
 *  scrolled away): the caller then keeps the popup hidden instead of parking it at 0,0.
 *  mode "quick": left-aligned to the button, above it, flipping below.
 *  mode "compose": right-aligned to the button, below it, flipping above.
 *  Always clamped inside the viewport. */
export function placement(anchor, popHeight, view, mode) {
  if (!anchor || !(anchor.width > 0) || !(anchor.height > 0)) return null;
  if (anchor.bottom <= 0 || anchor.top >= view.height || anchor.right <= 0 || anchor.left >= view.width) return null;
  const width = Math.min(MAX_WIDTH, view.width - 2 * MARGIN);
  const height = Math.min(popHeight || 0, view.height - 2 * MARGIN);
  const clamp = (v, lo, hi) => Math.max(lo, Math.min(v, Math.max(lo, hi)));
  const left = clamp(mode === 'compose' ? anchor.right - width : anchor.left, MARGIN, view.width - width - MARGIN);
  const above = anchor.top - GAP - height, below = anchor.bottom + GAP;
  const fitsAbove = above >= MARGIN, fitsBelow = below + height <= view.height - MARGIN;
  const top = mode === 'compose'
    ? (fitsBelow || !fitsAbove ? below : above)
    : (fitsAbove || !fitsBelow ? above : below);
  return { left, top: clamp(top, MARGIN, view.height - height - MARGIN), width };
}

/** CSS left/top that land the box at `want` when a transformed ancestor shifted the box drawn at
 *  `css` to `got` (a transform makes that ancestor the containing block for position:fixed). */
export function corrected(want, css, got) {
  return { left: css.left + (want.left - got.left), top: css.top + (want.top - got.top) };
}

function anchorOf(wrapper) {
  const sel = wrapper.dataset && wrapper.dataset.emojiAnchor;
  if (sel) return wrapper.parentElement && wrapper.parentElement.querySelector(sel);
  if (wrapper.classList.contains('mrte-emoji-pop')) return wrapper.parentElement && wrapper.parentElement.querySelector('.mrte-emoji-btn');
  return wrapper.parentElement && wrapper.parentElement.querySelector('button');
}

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
  const wrapper = el.closest('.quick-emoji-pop, .mrte-emoji-pop, [data-emoji-anchor]');
  const compose = !!wrapper && wrapper.classList.contains('mrte-emoji-pop');
  const place = () => {
    if (!wrapper || !wrapper.isConnected) return;
    const button = anchorOf(wrapper);
    const view = { width: innerWidth, height: innerHeight };
    const want = button && placement(button.getBoundingClientRect(), el.getBoundingClientRect().height, view, compose ? 'compose' : 'quick');
    if (!want) {
      // Anchor not laid out yet (or scrolled away): stay hidden next to it, never at 0,0.
      wrapper.style.visibility = 'hidden';
      return;
    }
    const css = (l, t) => `position:fixed;left:${l}px;top:${t}px;right:auto;bottom:auto;width:${want.width}px;z-index:125;visibility:visible`;
    wrapper.style.cssText = css(want.left, want.top);
    // A transformed ancestor (the maximized composer is centred with translate, a pressed button
    // row scales) becomes the containing block for position:fixed; shift by whatever offset it adds.
    const r = wrapper.getBoundingClientRect();
    if (Math.abs(r.left - want.left) > 0.5 || Math.abs(r.top - want.top) > 0.5) {
      const c = corrected(want, want, r);
      wrapper.style.cssText = css(c.left, c.top);
    }
  };
  // Ancestors may still be animating (page enter, press feedback, composer slide): follow them
  // for a moment, and again whenever an animation or transition ends anywhere above us.
  let frames = 0, raf = 0;
  const follow = () => { place(); if (++frames < 30) raf = requestAnimationFrame(follow); };
  const settle = e => { if (!(e.target instanceof Node) || !el.contains(e.target)) place(); };
  place();
  raf = requestAnimationFrame(follow);
  window.addEventListener('resize', place);
  // Re-anchor when the page/thread scrolls, but not for scrolling inside the picker itself.
  document.addEventListener('scroll', settle, true);
  document.addEventListener('transitionend', settle, true);
  document.addEventListener('animationend', settle, true);
  active.set(el, { handler, place, settle, stop: () => cancelAnimationFrame(raf) });
}

export function detach(el) {
  const state = active.get(el);
  if (state) {
    document.removeEventListener("pointerdown", state.handler, true);
    window.removeEventListener('resize', state.place);
    document.removeEventListener('scroll', state.settle, true);
    document.removeEventListener('transitionend', state.settle, true);
    document.removeEventListener('animationend', state.settle, true);
    state.stop();
  }
  active.delete(el);
}
