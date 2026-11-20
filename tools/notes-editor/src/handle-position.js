// Where the block drag handle (+ and grip) goes for list rows.
//
// TipTap places the handle against the hovered block's box. A list item's marker (1. / a. / • / the to-do checkbox) is drawn
// outside that box, in the list's left padding, so the handle landed on top of the number. This floating-ui middleware moves the
// handle further left by the marker's width for list items only; every other block keeps the default position.
const LIST_ITEMS = new Set(['listItem', 'taskItem']);

/// Where the handle's right edge sits relative to the list's left edge, in CSS px. NEGATIVE = a small overlap into the list
/// padding (left of the marker, which is drawn at the item edge). It must never leave a gap: TipTap hides the handle on the
/// editor's mouseleave, so a gap between the editor and the handle made it vanish while the pointer travelled left to it.
/// The handle's own padding-right (notes.css) keeps it visually clear of the number.
export const MARKER_GAP = -2;

/// How far left of the item box the marker reaches: the list's left padding (where ::marker / the checkbox lives).
export function markerWidth(itemEl, getStyle = el => getComputedStyle(el)) {
  const list = itemEl?.parentElement;
  if (!list) return 0;
  const pad = parseFloat(getStyle(list).paddingLeft) || 0;
  return pad > 0 ? pad : 0;
}

/// floating-ui middleware. `current()` returns { type, el }: the hovered node's type name and its DOM element.
export function handleClearsMarker(current, { gap = MARKER_GAP, getStyle } = {}) {
  return {
    name: 'handleClearsMarker',
    fn({ x }) {
      const { type, el } = current() ?? {};
      if (!LIST_ITEMS.has(type) || !el) return {};
      const w = markerWidth(el, getStyle);
      return w > 0 ? { x: x - w - gap } : {};
    },
  };
}
