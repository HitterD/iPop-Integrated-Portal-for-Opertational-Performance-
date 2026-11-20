// Where the block drag handle (+ and grip) goes for list rows.
//
// TipTap places the handle against the hovered block's box. A list item's marker (1. / a. / • / the to-do checkbox) is drawn
// outside that box, in the list's left padding, so the handle landed on top of the number. This floating-ui middleware moves the
// handle further left by the marker's width for list items only; every other block keeps the default position.
const LIST_ITEMS = new Set(['listItem', 'taskItem']);

/// Gap between the handle and the marker, in CSS px.
export const MARKER_GAP = 4;

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
