// Old Excalidraw scenes -> iPop canvas elements. Pure and DETERMINISTIC: the same Excalidraw scene always gives the same
// elements (same ids, versions and nonces), so when two people open an old board at the same moment and both convert it,
// they write identical values and nothing doubles up.
//
// What the user agreed (grill 2026-11-16, Q26): shapes become the clean ClickUp look, pen strokes stay exactly as drawn,
// text keeps the handwriting font it was written in. The old scene ("draw:<id>") is left untouched; the new one lives in
// "wb:<id>".

import { STICKY_COLORS } from './model.js';

// Stable 31-bit hash so the nonce is the same on every client.
function hash(str) {
  let h = 2166136261;
  for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619); }
  return (h >>> 0) % 2 ** 31;
}

// Template stickies were rectangles with these fills and no outline (see templates.js); they become real stickies.
const STICKY_FILLS = {
  '#ffe27a': 'yellow', '#b6f0c0': 'green', '#bfe0ff': 'blue', '#ffc2d4': 'pink', '#e3d2ff': 'purple', '#ffd3a8': 'orange',
  '#ffec99': 'yellow', '#b2f2bb': 'green', '#a5d8ff': 'blue', '#ffc9c9': 'pink', '#d0bfff': 'purple', '#ffd8a8': 'orange',
};
const isTransparent = c => !c || c === 'transparent' || /^#?0{0,6}00$/i.test(c);
// Excalidraw font ids: 1 Virgil, 5 Excalifont (both handwriting), 3 / 8 Cascadia / Comic Shanns (mono), others normal.
const fontOf = id => (id === 1 || id === 5 ? 'hand' : id === 3 || id === 8 ? 'mono' : 'ipop');
const SHAPE = { rectangle: 'rect', ellipse: 'ellipse', diamond: 'diamond' };

/// Converts a list of Excalidraw elements. Deleted ones are skipped (a fresh scene has no history to keep).
/// Returns iPop elements ordered like the original (z keys follow Excalidraw's fractional index).
export function convertScene(excal) {
  const live = (excal || []).filter(e => e && typeof e === 'object' && typeof e.id === 'string' && !e.isDeleted);
  const byId = new Map(live.map(e => [e.id, e]));
  // Text that sits inside a shape (Excalidraw "containerId") becomes that shape's own text.
  const boundText = new Map();
  for (const e of live) if (e.type === 'text' && e.containerId && byId.has(e.containerId)) boundText.set(e.containerId, e);

  const out = [];
  let order = 0;
  const z = e => (typeof e.index === 'string' && e.index ? e.index : `a${String(order).padStart(5, '0')}`);
  const base = (e, type, extra) => {
    order++;
    return {
      id: e.id, type, version: 1, versionNonce: hash(`${e.id}:${type}`), isDeleted: false, z: z(e),
      ...(e.locked ? { locked: true } : {}), ...(e.link ? { link: String(e.link) } : {}), ...extra,
    };
  };
  const stroke = e => (isTransparent(e.strokeColor) ? null : e.strokeColor);
  const fill = e => (isTransparent(e.backgroundColor) ? null : e.backgroundColor);

  for (const e of live) {
    const x = Number(e.x) || 0, y = Number(e.y) || 0, w = Math.abs(Number(e.width) || 0), h = Math.abs(Number(e.height) || 0);
    const inner = boundText.get(e.id);
    const text = inner ? String(inner.text ?? inner.originalText ?? '') : undefined;

    if (SHAPE[e.type]) {
      const sticky = e.type === 'rectangle' && isTransparent(e.strokeColor) && STICKY_FILLS[String(e.backgroundColor).toLowerCase()];
      if (sticky) { out.push(base(e, 'sticky', { x, y, w, h, color: sticky, text: text ?? '', font: inner ? fontOf(inner.fontFamily) : 'ipop' })); continue; }
      out.push(base(e, SHAPE[e.type], {
        x, y, w, h, stroke: stroke(e), fill: fill(e), strokeWidth: Math.min(8, Math.max(1, Number(e.strokeWidth) || 2)),
        ...(text !== undefined ? { text, font: fontOf(inner.fontFamily), size: Number(inner.fontSize) || 18 } : {}),
      }));
      continue;
    }
    if (e.type === 'text') {
      if (e.containerId && byId.has(e.containerId)) continue;    // already merged into its shape
      out.push(base(e, 'text', { x, y, w: Math.max(w, 20), h: Math.max(h, 20), text: String(e.text ?? ''), size: Number(e.fontSize) || 20, font: fontOf(e.fontFamily), stroke: stroke(e) }));
      continue;
    }
    if (e.type === 'freedraw') {
      const pts = (e.points || []).map((p, i) => [Number(p[0]) || 0, Number(p[1]) || 0, Array.isArray(e.pressures) && e.pressures.length ? Number(e.pressures[i] ?? 0.5) : 0.5]);
      // Excalidraw drew a pen line with a pressure-like taper when no real pressure was recorded; keep that look.
      const simulate = !(Array.isArray(e.pressures) && e.pressures.length) || undefined;
      out.push(base(e, 'path', { x, y, points: pts, stroke: stroke(e) || '#1D1D1F', width: Math.max(1, (Number(e.strokeWidth) || 2) * 1.5), simulate, opacity: e.opacity != null && e.opacity < 100 ? e.opacity / 100 : undefined }));
      continue;
    }
    if (e.type === 'arrow' || e.type === 'line') {
      const pts = (e.points || []).map(p => [Number(p[0]) || 0, Number(p[1]) || 0]);
      if (pts.length < 2) continue;
      const startId = e.startBinding?.elementId, endId = e.endBinding?.elementId;
      const first = pts[0], last = pts.at(-1);
      // A two-point line or an arrow attached to something is a connector; a free multi-point line keeps its exact shape.
      if (pts.length === 2 || startId || endId) {
        const end = (id, p) => (id && byId.has(id) ? { id, x: x + p[0], y: y + p[1] } : { x: x + p[0], y: y + p[1] });
        out.push(base(e, 'connector', {
          from: end(startId, first), to: end(endId, last),
          route: e.elbowed ? 'elbow' : pts.length > 2 ? 'curved' : 'straight',
          startHead: e.startArrowhead ? 'arrow' : 'none', endHead: e.type === 'arrow' ? (e.endArrowhead === null ? 'none' : 'arrow') : 'none',
          stroke: stroke(e), label: text,
        }));
      } else {
        out.push(base(e, 'path', { x, y, points: pts.map(p => [p[0], p[1], 0.5]), stroke: stroke(e) || '#1D1D1F', width: Math.max(1, Number(e.strokeWidth) || 2), straight: true }));
      }
      continue;
    }
    if (e.type === 'frame' || e.type === 'magicframe') { out.push(base(e, 'frame', { x, y, w, h, name: String(e.name || 'Frame') })); continue; }
    if (e.type === 'image') { out.push(base(e, 'image', { x, y, w, h, fileId: e.fileId ?? null })); continue; }
    // anything else (embeds, unknown future types) is skipped rather than drawn wrongly
  }
  return out;
}

/// Writes the converted scene into the new map, unless it already has content. Returns how many elements were written.
/// `oldMap` is the Excalidraw Y.Map ("draw:<id>"), `newMap` the canvas Y.Map ("wb:<id>"). Run inside a Yjs transaction.
export function convertInto(oldMap, newMap) {
  if (newMap.size > 0) return 0;
  const old = [];
  oldMap.forEach(v => old.push(v));
  const els = convertScene(old);
  for (const el of els) newMap.set(el.id, el);
  return els.length;
}

export { STICKY_COLORS };
