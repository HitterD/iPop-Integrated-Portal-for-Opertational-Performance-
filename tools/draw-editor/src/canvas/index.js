// Entry of iPop's own whiteboard bundle (wwwroot/js/notes/canvas/canvas.js). Same interface the Notes editor already uses for
// the Excalidraw bundle (mountDraw / renderPng / renderSvg), so the two can be switched without touching the page code.
//
// Scenes: the new canvas keeps its elements in "wb:<id>". The first time a drawing that only has an old Excalidraw scene
// ("draw:<id>") is opened with edit rights, it is converted (deterministically, see convert.js); the old scene is kept
// untouched for Version history. People who can only view see a converted copy without anything being written.
import { mountCanvas } from './board.js';
import { convertInto, convertScene } from './convert.js';
import { isElement } from './model.js';
import { toPng, toSvg } from './render.js';
import { TEMPLATE_INFO } from './templates.js';

export const ENGINE = 'canvas';
export { TEMPLATE_INFO, convertScene };
export const TEMPLATE_NAMES = TEMPLATE_INFO.map(t => t[0]);

/// The canvas scene for a drawing id, converting the old Excalidraw scene first when needed and allowed.
/// Returns { ymap, converted } where `ymap` may be a private in-memory copy for read-only viewers of an old drawing.
export function sceneFor(doc, id, { readOnly = false, Y } = {}) {
  const fresh = doc.getMap(`wb:${id}`);
  if (fresh.size > 0 || !doc.share.has(`draw:${id}`)) return { ymap: fresh, converted: 0 };
  const old = doc.getMap(`draw:${id}`);
  if (old.size === 0) return { ymap: fresh, converted: 0 };
  if (!readOnly) {
    let n = 0;
    doc.transact(() => { n = convertInto(old, fresh); }, 'ipop-canvas-convert');
    return { ymap: fresh, converted: n };
  }
  // view only: show a converted copy, write nothing
  const tmp = new Y.Doc().getMap('wb');
  const list = [];
  old.forEach(v => list.push(v));
  for (const e of convertScene(list)) tmp.set(e.id, e);
  return { ymap: tmp, converted: 0, preview: true };
}

/// opts as in mountCanvas plus { ymap?, doc, id? }. When `id` is given the scene is resolved (and converted) here.
/// Whether opening this drawing with edit rights would convert an old Excalidraw scene (the page saves a named version first).
export function needsConversion(doc, id) {
  return !(doc.share.has(`wb:${id}`) && doc.getMap(`wb:${id}`).size > 0) && doc.share.has(`draw:${id}`) && doc.getMap(`draw:${id}`).size > 0;
}

export function mountDraw(el, opts) {
  let { ymap } = opts;
  if (opts.id != null || !ymap) ymap = sceneFor(opts.doc, opts.id, { readOnly: opts.readOnly, Y: opts.Y }).ymap;
  const doc = ymap.doc || opts.doc;
  return mountCanvas(el, { ...opts, ymap, doc });
}

const pickElements = list => (list || []).filter(isElement);

/// PNG of stored elements without any UI (exports). Accepts canvas elements, or an Excalidraw list which is converted first.
export async function renderPng(elements, { scale = 2, dark = false, loadFile } = {}) {
  const els = toCanvasElements(elements);
  if (!els.length) return null;
  // pictures on the board are embedded as data URLs (an SVG rasterised from a data URL cannot load blob: URLs)
  const files = new Map();
  if (loadFile) for (const e of els) if (e.type === 'image' && e.fileId && !files.has(e.fileId)) {
    try { const b = await (await fetch(await loadFile(e.fileId))).blob(); files.set(e.fileId, await new Promise(ok => { const r = new FileReader(); r.onload = () => ok(r.result); r.readAsDataURL(b); })); } catch { /* drawn as a placeholder */ }
  }
  return toPng(els, { scale, theme: dark ? 'dark' : 'light', files });
}
export async function renderSvg(elements, { dark = false } = {}) {
  const els = toCanvasElements(elements);
  if (!els.length) return null;
  const o = toSvg(els, { theme: dark ? 'dark' : 'light' });
  return o ? new XMLSerializer().serializeToString(o.svg) : null;
}
function toCanvasElements(list) {
  const arr = (list || []).filter(Boolean);
  const looksOld = arr.some(e => e && ('strokeColor' in e || 'roughness' in e || e.type === 'freedraw' || e.type === 'rectangle'));
  return (looksOld ? convertScene(arr) : pickElements(arr)).filter(e => !e.isDeleted);
}
