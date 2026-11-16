// iPop's own whiteboard editor. One instance per board or /draw block.
//
// Storage: the scene's Y.Map ("wb:<id>") is the single source of truth; every element is one key. The editor draws straight
// from it, so what everyone sees converges with Yjs and needs no reconciliation layer. While a pointer is down, changes go to
// a local draft that is written to the map at most every 60 ms (others see the move live); undo is a Y.UndoManager that only
// tracks this editor's own writes, so Ctrl Z never undoes somebody else's work.
import {
  create, bump, isElement, zBetween, topZ, bottomZ, bounds, unionBounds, hitTest, insideBox, frameOf, moved, resizeBox,
  snap, guides, recognise, centre, SHAPES, STICKY_COLORS,
  cutStroke, strokePieces, alignBoxes, distributeBoxes, tidyRow, expandGroups, recogniseLine, findOnBoard,
} from './model.js';
import { svgEl, defs, drawElement, PALETTE, toPng, toSvg, measure, FONT_STACK, layoutText, pngToPdf } from './render.js';
import { CANVAS_TEMPLATES, TEMPLATE_INFO } from './templates.js';

const ICONS = {
  select: '<path d="m4 4 7 17 2.5-7.5L21 11z"/>',
  hand: '<path d="M18 11V6a2 2 0 0 0-4 0v5M14 10V4a2 2 0 0 0-4 0v6M10 10.5V6a2 2 0 0 0-4 0v8"/><path d="M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.9-6-2.4l-3.6-3.6a2 2 0 0 1 2.8-2.8L7 15"/>',
  pen: '<path d="M12 20h9"/><path d="M16.4 3.6a2 2 0 0 1 2.9 2.9L7 18.8l-4 1 1-4z"/>',
  highlighter: '<path d="m9 11-6 6v3h9l3-3"/><path d="m22 12-4.6 4.6a2 2 0 0 1-2.8 0l-5.2-5.2a2 2 0 0 1 0-2.8L14 4"/>',
  eraser: '<path d="m7 21-4.3-4.3a1 1 0 0 1 0-1.4l10-10a1 1 0 0 1 1.4 0l5.6 5.6a1 1 0 0 1 0 1.4L13 21"/><path d="M22 21H7M5 11l9 9"/>',
  text: '<path d="M4 7V4h16v3M9 20h6M12 4v16"/>',
  rect: '<rect x="3" y="5" width="18" height="14" rx="3"/>',
  ellipse: '<circle cx="12" cy="12" r="9"/>',
  diamond: '<path d="M12 2 22 12 12 22 2 12z"/>',
  triangle: '<path d="M12 3 22 20H2z"/>',
  star: '<path d="m12 2 3.1 6.3 6.9 1-5 4.9 1.2 6.8L12 17.8 5.8 21l1.2-6.8-5-4.9 6.9-1z"/>',
  cloud: '<path d="M17.5 19H9a7 7 0 1 1 6.7-9h1.8a4.5 4.5 0 1 1 0 9z"/>',
  arrowShape: '<path d="M3 9h11V5l7 7-7 7v-4H3z"/>',
  cylinder: '<ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/>',
  bubble: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
  connector: '<path d="M5 19 19 5M9 5h10v10"/>',
  frame: '<path d="M22 6H2M22 18H2M6 2v20M18 2v20"/>',
  shapes: '<rect x="3" y="3" width="8" height="8" rx="2"/><circle cx="17" cy="7" r="4"/><path d="m7 14 4 7H3z"/><path d="M14 14h7v7h-7z"/>',
  undo: '<path d="M3 7v6h6"/><path d="M21 17a9 9 0 0 0-15-6.7L3 13"/>',
  redo: '<path d="M21 7v6h-6"/><path d="M3 17a9 9 0 0 1 15-6.7L21 13"/>',
  copy: '<rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>',
  trash: '<path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/>',
  front: '<rect x="8" y="8" width="13" height="13" rx="2"/><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3"/>',
  back: '<rect x="3" y="3" width="13" height="13" rx="2"/><path d="M8 16v3a2 2 0 0 0 2 2h9a2 2 0 0 0 2-2v-9a2 2 0 0 0-2-2h-3"/>',
  lock: '<rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
  unlock: '<rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
  keys: '<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10"/>',
  more: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
};
ICONS.book = '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5z"/><path d="M4 19.5A2.5 2.5 0 0 0 6.5 22H20v-5"/>';
ICONS.bookplus = '<path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20V3H6.5A2.5 2.5 0 0 0 4 5.5z"/><path d="M12 7v6M9 10h6"/>';
ICONS.image = '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="m21 15-5-5L5 21"/>';
ICONS.file = '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>';
ICONS.laser = '<circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9 7 7M17 17l2.1 2.1M4.9 19.1 7 17M17 7l2.1-2.1"/>';
ICONS.play = '<path d="m6 4 14 8-14 8z"/>';
ICONS.timer = '<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M9 2h6"/>';
ICONS.frames = '<rect x="3" y="4" width="7" height="7" rx="1"/><rect x="14" y="4" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 17h7"/>';
ICONS.comment = '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>';
ICONS.vote = '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="3.2" fill="currentColor"/>';
ICONS.task = '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="m8 12 3 3 5-6"/>';
ICONS.note = '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6M8 13h8M8 17h5"/>';
ICONS.download = '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="m7 10 5 5 5-5M12 15V3"/>';
ICONS.stamp = '<circle cx="12" cy="12" r="9"/><path d="M8 14s1.5 2 4 2 4-2 4-2M9 9h.01M15 9h.01"/>';
ICONS.mind = '<circle cx="12" cy="12" r="3"/><circle cx="4" cy="5" r="2"/><circle cx="20" cy="5" r="2"/><circle cx="4" cy="19" r="2"/><circle cx="20" cy="19" r="2"/><path d="M6 6l4 4M18 6l-4 4M6 18l4-4M18 18l-4-4"/>';
ICONS.card = '<rect x="3" y="5" width="18" height="14" rx="2"/><path d="M7 9h10M7 13h6"/>';
ICONS.marker = '<path d="m9 15-4 5h5l2-2"/><path d="m9 15 7-11 4 3-7 11z"/>';
ICONS.pencil = '<path d="M15 4l5 5L9 20H4v-5z"/><path d="m4 15 5 5"/>';
ICONS.emoji = '<circle cx="12" cy="12" r="9"/><path d="M8.5 14.5a4.5 4.5 0 0 0 7 0M9 9.5h.01M15 9.5h.01"/>';
ICONS.link = '<path d="M10 14a4 4 0 0 0 6 .5l3-3a4 4 0 0 0-5.7-5.7l-1.5 1.5"/><path d="M14 10a4 4 0 0 0-6-.5l-3 3a4 4 0 0 0 5.7 5.7l1.5-1.5"/>';
ICONS.search = '<circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/>';
ICONS.newtab = '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>';
ICONS.group = '<rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/><path d="M3 15v6h6M21 9V3h-6" stroke-dasharray="2 2"/>';
ICONS.ungroup = '<rect x="3" y="3" width="8" height="8" rx="1"/><rect x="13" y="13" width="8" height="8" rx="1"/>';
ICONS.arrange = '<path d="M4 3v18M8 7h12M8 15h7"/>';
ICONS.alignleft = '<path d="M4 3v18M8 7h10M8 15h6"/>';
ICONS.alignhc = '<path d="M12 3v18M6 7h12M8 15h8"/>';
ICONS.alignright = '<path d="M20 3v18M6 7h10M10 15h6"/>';
ICONS.aligntop = '<path d="M3 4h18M7 8v10M15 8v6"/>';
ICONS.alignvc = '<path d="M3 12h18M7 6v12M15 8v8"/>';
ICONS.alignbottom = '<path d="M3 20h18M7 6v10M15 10v6"/>';
ICONS.distx = '<rect x="3" y="8" width="4" height="8"/><rect x="10" y="8" width="4" height="8"/><rect x="17" y="8" width="4" height="8"/>';
ICONS.disty = '<rect x="8" y="3" width="8" height="4"/><rect x="8" y="10" width="8" height="4"/><rect x="8" y="17" width="8" height="4"/>';
const icon = (name, cls = 'wb-i') => `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ''}</svg>`;

const SIZES = [['S', 14], ['M', 18], ['L', 26], ['XL', 40]];
// Colours (grill round 2): 8 bright + 8 soft; "auto" = black on a light board, white on a dark one.
const BRIGHT = ['auto', '#FF3B30', '#FF9500', '#FFCC00', '#34C759', '#30B0C7', '#0A84FF', '#AF52DE'];
const SOFT = ['#8E8E93', '#FFB3AE', '#FFD29A', '#FFE88A', '#A8E6B4', '#A6E3EC', '#A9CFFF', '#DCC1F2'];
const ALL_COLOURS = [...BRIGHT, ...SOFT];
// Pen types: sizes are the S/M/L/XL presets in screen px at 100 %; the slider goes 1–40.
const PEN_TYPES = {
  pen: { label: 'Pen', sizes: [2, 4, 8, 14], size: 4, color: 'auto' },
  marker: { label: 'Marker', sizes: [4, 8, 14, 24], size: 8, color: '#FF3B30' },
  highlighter: { label: 'Highlight', sizes: [10, 16, 24, 36], size: 16, color: '#FFCC00' },
  pencil: { label: 'Pencil', sizes: [1, 2, 4, 7], size: 2, color: 'auto' },
};
const ERASER_SIZES = [8, 16, 32, 56];
const SIZE_NAMES = ['Small', 'Medium', 'Large', 'Extra large'];
const PREFS_KEY = 'ipop.wb.prefs';
/// Per person, per browser: the last pen, sizes, colours. Never shared, so a broken entry only resets the defaults.
function loadPrefs() {
  const base = { ptype: 'pen', size: { eraser: 16 }, color: {}, smooth: 50, erMode: 'whole', snap: true, recent: [], opacity: 100 };
  for (const [k, v] of Object.entries(PEN_TYPES)) { base.size[k] = v.size; base.color[k] = v.color; }
  try {
    const s = JSON.parse(localStorage.getItem(PREFS_KEY) || 'null');
    if (s && typeof s === 'object') {
      if (PEN_TYPES[s.ptype]) base.ptype = s.ptype;
      for (const k of Object.keys(base.size)) if (Number.isFinite(s.size?.[k])) base.size[k] = Math.max(1, Math.min(k === 'eraser' ? 80 : 40, s.size[k]));
      for (const k of Object.keys(PEN_TYPES)) if (typeof s.color?.[k] === 'string' && /^(auto|#[0-9a-f]{6})$/i.test(s.color[k])) base.color[k] = s.color[k];
      if (Number.isFinite(s.smooth)) base.smooth = Math.max(0, Math.min(100, s.smooth));
      if (s.erMode === 'part') base.erMode = 'part';
      if (s.snap === false) base.snap = false;
      if (Array.isArray(s.recent)) base.recent = s.recent.filter(c => /^#[0-9a-f]{6}$/i.test(c)).slice(0, 8);
    }
  } catch { /* private window or bad entry: defaults */ }
  return base;
}
function savePrefs(p) { try { localStorage.setItem(PREFS_KEY, JSON.stringify(p)); } catch { /* blocked */ } }
const isHex = c => /^#[0-9a-f]{6}$/i.test(c || '');

// Letter shortcuts like ClickUp; digits keep the old Excalidraw meaning (grill Q18).
const KEYS = { n: 'card', c: 'comment', k: 'stamp', i: 'image', v: 'select', l: 'laser', h: 'hand', p: 'pen', m: 'highlighter', e: 'eraser', s: 'sticky', t: 'text', r: 'rect', o: 'ellipse', d: 'diamond', a: 'connector', f: 'frame' };
const DIGITS = { 0: 'eraser', 1: 'select', 2: 'rect', 3: 'diamond', 4: 'ellipse', 5: 'connector', 6: 'connector', 7: 'pen', 8: 'text', 9: 'select' };
const SHAPE_LABEL = { rect: 'Rectangle', ellipse: 'Circle', diamond: 'Diamond', triangle: 'Triangle', star: 'Star', cloud: 'Cloud', arrowShape: 'Block arrow', cylinder: 'Cylinder', bubble: 'Speech bubble' };
const MAX_ELEMENTS = 20000;

const el = (tag, cls, html) => { const e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; };
let clipboard = null;   // copy / paste between boards on the same page (the system clipboard also gets a JSON copy)

/// opts: { ymap, doc, readOnly, theme, mode: 'block'|'board', onLinkOpen(url), onChange(count), toast(msg), onFullscreen() }
export function mountCanvas(container, opts) {
  const { ymap, doc, Y } = opts;
  if (!Y?.UndoManager) throw new Error('mountCanvas needs the page\'s Yjs (opts.Y)');
  const mode = opts.mode || 'block';
  let readOnly = !!opts.readOnly, theme = opts.theme || 'light', destroyed = false;
  const ORIGIN = {};
  const undo = new Y.UndoManager(ymap, { trackedOrigins: new Set([ORIGIN]), captureTimeout: 500 });

  // ── DOM ──────────────────────────────────────────────────────────────────
  const root = el('div', `wb wb-${mode}`);
  root.tabIndex = 0;
  root.setAttribute('role', 'application');
  root.setAttribute('aria-label', mode === 'board' ? 'Whiteboard' : 'Drawing');
  const svg = svgEl('svg', { class: 'wb-svg' });
  defs(svg);
  const world = svgEl('g', { class: 'wb-world' }, svg);
  const overlay = svgEl('g', { class: 'wb-overlay' }, svg);
  root.append(svg);
  const toolbar = el('div', 'wb-tools'); toolbar.setAttribute('role', 'toolbar'); toolbar.setAttribute('aria-label', 'Drawing tools');
  const props = el('div', 'wb-props'); props.hidden = true;
  const zoomBar = el('div', 'wb-zoom');
  const shapeMenu = el('div', 'wb-menu wb-shapemenu'); shapeMenu.hidden = true;
  const keysPanel = el('div', 'wb-panel wb-keys'); keysPanel.hidden = true;
  const neat = el('div', 'wb-neat'); neat.hidden = true;
  const toastEl = el('div', 'wb-toast');
  const topLeft = el('div', 'wb-corner wb-tl'), topRight = el('div', 'wb-corner wb-tr');
  const gallery = el('div', 'wb-panel wb-gallery'); gallery.hidden = true;
  const emptyGallery = el('div', 'wb-emptygal'); emptyGallery.hidden = true;
  const libPanel = el('div', 'wb-panel wb-lib'); libPanel.hidden = true; libPanel.setAttribute('role', 'dialog'); libPanel.setAttribute('aria-label', 'Library');
  root.append(toolbar, props, zoomBar, shapeMenu, keysPanel, neat, toastEl, topLeft, topRight, gallery, emptyGallery, libPanel);
  container.append(root);

  // ── State ────────────────────────────────────────────────────────────────
  let view = { x: 40, y: 40, z: 1 };
  let tool = 'select', lastShape = 'rect', stickyColor = 'yellow';
  const prefs = loadPrefs();
  // colour new text, shapes and connectors take: the pen's colour (null = follows the theme, like before)
  const inkNow = () => { const c = prefs.color[prefs.ptype]; return isHex(c) ? c : null; };
  const penKind = () => (tool === 'highlighter' ? 'highlighter' : prefs.ptype);
  let sel = new Set();
  const draft = new Map();       // id -> element being changed under the pointer (not yet written)
  let flushTimer = null;
  let editing = null;            // { id, ta }
  const cache = new Map();       // id -> { key, node }

  const stored = id => { const v = ymap.get(id); return isElement(v) ? v : undefined; };
  const get = id => draft.get(id) ?? stored(id);
  const all = () => { const out = []; ymap.forEach((v, k) => { const d = draft.get(k); if (d) out.push(d); else if (isElement(v)) out.push(v); }); for (const [k, d] of draft) if (!ymap.has(k)) out.push(d); return out; };
  const live = () => all().filter(e => !e.isDeleted);
  const pal = () => PALETTE[theme] || PALETTE.light;
  const toast = msg => { if (opts.toast) { opts.toast(msg); return; } toastEl.textContent = msg; toastEl.classList.add('show'); clearTimeout(toast.t); toast.t = setTimeout(() => toastEl.classList.remove('show'), 2600); };

  const write = (els, { now = false } = {}) => {
    for (const e of els) draft.set(e.id, e);
    if (now) flush(); else if (!flushTimer) flushTimer = setTimeout(flush, 60);
    render();
  };
  function flush() {
    clearTimeout(flushTimer); flushTimer = null;
    if (!draft.size || destroyed) return;
    const batch = [...draft.values()];
    draft.clear();
    if (readOnly) { render(); return; }
    doc.transact(() => { for (const e of batch) ymap.set(e.id, e); }, ORIGIN);
    opts.onChange?.(count());
  }
  const commitStop = () => { flush(); undo.stopCapturing(); };
  const count = () => { let n = 0; ymap.forEach(v => { if (isElement(v) && !v.isDeleted) n++; }); return n; };

  const onY = () => { if (!destroyed) render(); };
  ymap.observe(onY);

  // ── Coordinates ──────────────────────────────────────────────────────────
  const rect = () => root.getBoundingClientRect();
  const toWorld = (sx, sy) => ({ x: (sx - view.x) / view.z, y: (sy - view.y) / view.z });
  const toScreen = (x, y) => ({ x: x * view.z + view.x, y: y * view.z + view.y });
  const local = e => { const r = rect(); return { sx: e.clientX - r.left, sy: e.clientY - r.top }; };

  // ── Rendering ────────────────────────────────────────────────────────────
  const aw = opts.awareness || null;          // the page's Yjs awareness (cursors, Follow, Present, timer)
  let timerEnds = 0, timerTick = 0, timerBeeped = 0;
  let interaction = null;                     // what the pointer is doing right now
  const pointers = new Map();
  let invitedFor = null, followingPresenter = null;
  // panels and live-collaboration elements: all created up front (several are touched by the first paint)
  const framesPanel = el('div', 'wb-panel wb-frames'); framesPanel.hidden = true;
  const timerPanel = el('div', 'wb-panel wb-timer'); timerPanel.hidden = true;
  const presBar = el('div', 'wb-presbar'); presBar.hidden = true;
  const invite = el('div', 'wb-invite'); invite.hidden = true;
  const people = el('div', 'wb-people');
  const followBar = el('div', 'wb-followbar'); followBar.hidden = true;
  root.append(framesPanel, timerPanel, presBar, invite, followBar);
  // the faces sit at the start of the top-right row, so they never cover a button
  let following = null;                        // awareness client id being followed
  let lastCursor = null, laserTrail = [], laserFade = 0;
  let presenting = null;                       // { index } while I present
  const vmap = opts.voteMap || null;           // shared voting round + votes (a Y.Map next to the scene)
  let afterPaint = () => {};   // set by the live section below (cursors, Follow)
  let raf = 0;
  function render() { if (!raf && !destroyed) raf = requestAnimationFrame(() => { raf = 0; paint(); }); }
  function paint() {
    world.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    const g = 24 * view.z;
    root.style.backgroundSize = `${g}px ${g}px`;
    root.style.backgroundPosition = `${view.x}px ${view.y}px`;
    const els = live();
    const ordered = [...els].sort((p, q) => (p.type === 'frame') !== (q.type === 'frame') ? (p.type === 'frame' ? -1 : 1) : (p.z < q.z ? -1 : p.z > q.z ? 1 : p.id < q.id ? -1 : 1));
    const seen = new Set();
    let prev = null;
    for (const e of ordered) {
      seen.add(e.id);
      if ((e.type === 'image' || e.type === 'file') && e.fileId && e.type === 'image') ensureFile(e.fileId);
      // a connector must redraw when either shape it is attached to moves
      const deps = e.type === 'connector' ? `${get(e.from?.id)?.version ?? ''}:${get(e.from?.id)?.x ?? ''},${get(e.from?.id)?.y ?? ''}|${get(e.to?.id)?.version ?? ''}:${get(e.to?.id)?.x ?? ''},${get(e.to?.id)?.y ?? ''},${get(e.to?.id)?.w ?? ''}` : '';
      // a stroke being drawn keeps its version while points are added, so the point count is part of the key
      const key = `${e.version}:${e.versionNonce}:${theme}:${editing?.id === e.id}:${deps}:${e.x},${e.y},${e.w},${e.h}:${e.points?.length ?? ''}`;
      let c = cache.get(e.id);
      if (!c || c.key !== key) {
        const tmp = svgEl('g');
        const node = drawElement(tmp, e, get, { theme, editingId: editing?.id, files: opts.files, cardInfo: opts.cardInfo });
        if (c) c.node.replaceWith(node); else (prev ? prev.after(node) : world.prepend(node));
        c = { key, node }; cache.set(e.id, c);
      }
      // keep DOM order = draw order
      if (prev ? prev.nextSibling !== c.node : world.firstChild !== c.node) { if (prev) prev.after(c.node); else world.prepend(c.node); }
      prev = c.node;
    }
    for (const [id, c] of cache) if (!seen.has(id)) { c.node.remove(); cache.delete(id); }
    for (const id of [...sel]) if (!get(id) || get(id).isDeleted) sel.delete(id);
    paintOverlay();
    paintZoom();
    paintEmpty(!els.length);
    afterPaint();
  }

  function paintOverlay() {
    overlay.innerHTML = '';
    const p = pal();
    // others' changes are drawn by paint(); this layer is selection, guides, marquee, previews
    if (sel.size && !editing) {
      const els = [...sel].map(get).filter(Boolean);
      const b = unionBounds(els, get);
      if (b) {
        const a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
        svgEl('rect', { x: a.x - 4, y: a.y - 4, width: z.x - a.x + 8, height: z.y - a.y + 8, fill: 'none', stroke: p.sel, 'stroke-width': 1.5, rx: 3 }, overlay);
        const one = els.length === 1 ? els[0] : null;
        const canResize = !readOnly && els.every(e => !e.locked) && !(one && (one.type === 'connector'));
        if (canResize) {
          const hs = [['nw', a.x - 4, a.y - 4], ['ne', z.x + 4, a.y - 4], ['sw', a.x - 4, z.y + 4], ['se', z.x + 4, z.y + 4]];
          if (one && one.type !== 'path') hs.push(['n', (a.x + z.x) / 2, a.y - 4], ['s', (a.x + z.x) / 2, z.y + 4], ['w', a.x - 4, (a.y + z.y) / 2], ['e', z.x + 4, (a.y + z.y) / 2]);
          for (const [h, x, y] of hs) svgEl('rect', { x: x - 5, y: y - 5, width: 10, height: 10, rx: 3, fill: '#fff', stroke: p.sel, 'stroke-width': 1.5, 'data-handle': h, class: `wb-h wb-h-${h}` }, overlay);
        }
        if (one?.type === 'connector' && !readOnly) {
          const ends = connectorEndsScreen(one);
          if (ends) for (const [k, pt] of ends) svgEl('circle', { cx: pt.x, cy: pt.y, r: 6, fill: '#fff', stroke: p.sel, 'stroke-width': 1.5, 'data-end': k, class: 'wb-h' }, overlay);
        }
        if (els.some(e => e.locked)) { const t = svgEl('text', { x: a.x, y: a.y - 10, 'font-size': 11, fill: p.ink3 }, overlay); t.textContent = '🔒 Locked'; }
      }
    }
    if (interaction?.marquee) {
      const m = interaction.marquee, a = toScreen(m.x, m.y), z = toScreen(m.x + m.w, m.y + m.h);
      svgEl('rect', { x: a.x, y: a.y, width: z.x - a.x, height: z.y - a.y, fill: 'rgba(10,132,255,.08)', stroke: p.sel, 'stroke-dasharray': '4 3' }, overlay);
    }
    for (const l of interaction?.guideLines || []) {
      const a = toScreen(l.x1, l.y1), z = toScreen(l.x2, l.y2);
      svgEl('line', { x1: a.x, y1: a.y, x2: z.x, y2: z.y, stroke: '#FF2D55', 'stroke-width': 1, 'stroke-dasharray': '3 3' }, overlay);
    }
    if (interaction?.connectHover) {
      const t = get(interaction.connectHover);
      if (t) { const b = bounds(t, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h); svgEl('rect', { x: a.x - 3, y: a.y - 3, width: z.x - a.x + 6, height: z.y - a.y + 6, fill: 'none', stroke: p.sel, 'stroke-width': 2, rx: 6, opacity: 0.6 }, overlay); }
    }
    positionProps();
  }
  function connectorEndsScreen(c) {
    const a = get(c.from?.id), b = get(c.to?.id);
    const pa = a ? centre(a, get) : c.from, pb = b ? centre(b, get) : c.to;
    if (!pa || !pb || !Number.isFinite(pa.x) || !Number.isFinite(pb.x)) return null;
    return [['from', toScreen(pa.x, pa.y)], ['to', toScreen(pb.x, pb.y)]];
  }

  // ── Toolbar ──────────────────────────────────────────────────────────────
  const compact = mode === 'block';
  const TOOLS = [
    ['select', 'Select', 'V'], ['hand', 'Hand', 'H'], '|',
    ['pen', 'Pen (click again for options)', 'P'], ['eraser', 'Eraser (click again for options)', 'E'], '|',
    ['sticky', 'Sticky note', 'S'], ['text', 'Text', 'T'], ['shape', 'Shapes', 'R'], ['connector', 'Connector', 'A'], '|', ['frame', 'Frame', 'F'], ['image', 'Picture or file', 'I'], ['comment', 'Comment', 'C'], ['stamp', 'Stamp', 'K'], ['card', 'Add a Notes page or Projects task', 'N'], ['laser', 'Laser pointer', 'L'],
  ];
  // phone = a narrow screen (same breakpoint as the rest of the app: under 768 px)
  // one phone breakpoint for the whole app: ask ipop.mobile (same query as the CSS)
  const isPhone = () => { try { return !!window.ipop?.mobile?.isMobile?.(); } catch { return false; } };
  let moreOpen = false;
  const moreMenu = el('div', 'wb-menu wb-moremenu'); moreMenu.hidden = true;
  root.append(moreMenu);
  moreMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function paintMore() {
    const items = [['shape', 'Shapes'], ['connector', 'Connector'], ['text', 'Text'], ['eraser', 'Eraser'], ['laser', 'Laser'], ...(compact ? [] : [['frame', 'Frame'], ['stamp', 'Stamp'], ['card', 'Add card']])]
      .filter(([k]) => !(k === 'stamp' && compact) && !(k === 'card' && !opts.pages && !opts.taskSearch));
    moreMenu.innerHTML = items.map(([k, label]) => `<button type="button" data-more="${k}" class="wb-moreitem"><span class="wb-moreic">${k === 'shape' ? icon(lastShape) : icon(k)}</span>${label}</button>`).join('')
      + (opts.voteMap ? '<button type="button" data-more="vote" class="wb-moreitem"><span class="wb-moreic">' + icon('vote') + '</span>Vote</button>' : '');
  }
  moreMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-more]'); if (!b) return;
    moreOpen = false; moreMenu.hidden = true; paintToolbar();
    const k = b.dataset.more;
    if (k === 'vote') { toggleVotePanel(); return; }
    if (k === 'shape') { setTool(lastShape); return; }
    if (k === 'stamp') { toggleStampMenu(toolbar); return; }
    if (k === 'card') { toggleCardPicker(); return; }
    setTool(k);
  });
  function paintToolbar() {
    if (readOnly) {
      // viewers still get the hand and the laser pointer (pointing during a meeting)
      toolbar.hidden = false;
      toolbar.innerHTML = [['hand', 'Hand', 'H'], ...(opts.comments?.canComment?.() ? [['comment', 'Comment', 'C']] : []), ['laser', 'Laser pointer', 'L']].map(([k, label, key]) => `<button type="button" data-tool="${k}" class="${tool === k ? 'on' : ''}" title="${label} (${key})" aria-label="${label}" aria-pressed="${tool === k}">${icon(k)}<span class="wb-k">${key}</span></button>`).join('');
      return;
    }
    toolbar.hidden = false;
    // On a phone the bar keeps what a finger needs: select, hand, pen, sticky, picture, comment, and a "More" that opens the rest.
    const phone = isPhone();
    const PHONE_KEEP = new Set(['select', 'hand', 'pen', 'sticky', 'image', 'comment']);
    toolbar.innerHTML = TOOLS.filter(t => t === '|' ? !phone : (!phone || PHONE_KEEP.has(t[0])))
      .filter(t => !(compact && (t[0] === 'frame' || t[0] === 'highlighter')) && !(t[0] === 'image' && !opts.upload) && !(t[0] === 'comment' && !opts.comments) && !(compact && t[0] === 'stamp') && !(t[0] === 'card' && !opts.pages && !opts.taskSearch)).map(t => {
      if (t === '|') return '<span class="wb-sep"></span>';
      const [k, label, key] = t;
      const on = k === tool || (k === 'shape' && SHAPES.includes(tool)) || (k === 'pen' && tool === 'highlighter');
      const pk = k === 'pen' ? (tool === 'highlighter' ? 'highlighter' : prefs.ptype) : null;
      const ic = k === 'sticky' ? `<span class="wb-stk" style="background:${pal().sticky[stickyColor]}"></span>`
        : k === 'shape' ? icon(lastShape)
        : pk ? `${icon(pk === 'pen' ? 'pen' : pk)}<span class="wb-pendot" style="background:${colourCss(prefs.color[pk])}"></span>`
        : icon(k);
      return `<button type="button" data-tool="${k}" class="${on ? 'on' : ''}" title="${label} (${key})" aria-label="${label}" aria-pressed="${on}">${ic}<span class="wb-k">${key}</span></button>`;
    }).join('') + (phone ? `<button type="button" data-tool="more" class="${moreOpen ? 'on' : ''}" title="More tools" aria-label="More tools" aria-expanded="${moreOpen}">${icon('more')}</button>` : '') + (compact && opts.onFullscreen ? `<span class="wb-sep"></span><button type="button" data-act="fullscreen" title="Open full screen" aria-label="Open full screen"><svg class="wb-i" viewBox="0 0 24 24"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg></button>` : '');
  }
  toolbar.addEventListener('pointerdown', e => e.stopPropagation());
  toolbar.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.act === 'fullscreen') { opts.onFullscreen?.(); return; }
    const k = b.dataset.tool;
    if (k === 'more') { moreOpen = !moreOpen; if (moreOpen) paintMore(); moreMenu.hidden = !moreOpen; paintToolbar(); return; }
    if (k === 'shape') { toggleShapeMenu(b); return; }
    if (k === 'image') { pickFiles(); return; }
    if (k === 'stamp') { toggleStampMenu(b); return; }
    if (k === 'card') { toggleCardPicker(); return; }
    if (k === 'sticky' && tool === 'sticky') stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length];
    // a second click on the active pen or eraser opens its options (ClickUp-like)
    if ((k === 'pen' && (tool === 'pen' || tool === 'highlighter')) || (k === 'eraser' && tool === 'eraser')) { togglePenPop(k === 'eraser' ? 'eraser' : 'pen', b); return; }
    if (k === 'pen') { setTool(prefs.ptype === 'highlighter' ? 'highlighter' : 'pen'); return; }
    setTool(k);
  });
  // ── Pen and eraser options (popover beside the button; a bottom sheet on a phone) ─────
  const penPop = el('div', 'wb-penpop'); penPop.hidden = true; penPop.setAttribute('role', 'dialog');
  const colourPop = el('div', 'wb-colourpop'); colourPop.hidden = true; colourPop.setAttribute('role', 'dialog'); colourPop.setAttribute('aria-label', 'Colour');
  const eraserRing = el('div', 'wb-eraserring'); eraserRing.hidden = true;
  root.append(penPop, colourPop, eraserRing);
  let penSeen = false;               // a stylus touched this board: resting hands (touch) stop drawing
  let popKind = 'pen';               // pen | eraser
  const colourCss = c => (isHex(c) ? c : pal().ink);
  const swatchBtn = (c, on, extra = '') => `<button type="button" class="wb-swb ${c === 'auto' ? 'wb-auto' : c === 'none' ? 'wb-none' : ''} ${on ? 'on' : ''}" data-c="${c}" ${extra} title="${c === 'auto' ? 'Black or white (follows the theme)' : c === 'none' ? 'No fill' : c}" aria-label="${c === 'auto' ? 'Automatic black or white' : c === 'none' ? 'No fill' : `Colour ${c}`}" aria-pressed="${on}" style="${isHex(c) ? `background:${c}` : ''}"></button>`;
  function setSize(key, v) {
    prefs.size[key] = Math.max(1, Math.min(key === 'eraser' ? 80 : 40, Math.round(v)));
    savePrefs(prefs);
    // with strokes selected, the size applies to them too (ClickUp behaviour)
    if (key !== 'eraser') applyToSelection(x => x.type === 'path' ? { width: +(prefs.size[key] / view.z).toFixed(2) } : null);
    if (!penPop.hidden) paintPenPop();
    paintEraserRing();
  }
  function applyToSelection(fn) {
    if (readOnly || !sel.size) return;
    const els = [...sel].map(get).filter(x => x && !x.locked);
    const out = els.map(x => { const c = fn(x); return c ? bump(x, c) : null; }).filter(Boolean);
    if (out.length) { write(out, { now: true }); commitStop(); }
  }
  function togglePenPop(kind, anchor) {
    if (!penPop.hidden && popKind === kind) { penPop.hidden = true; colourPop.hidden = true; return; }
    popKind = kind; colourPop.hidden = true;
    penPop.hidden = false;
    paintPenPop();
    placePop(penPop, anchor || toolbar.querySelector(`[data-tool="${kind}"]`));
  }
  function placePop(pop, anchor) {
    pop.classList.toggle('wb-sheet', isPhone());
    if (isPhone()) { pop.style.left = ''; pop.style.bottom = ''; return; }
    const r = anchor?.getBoundingClientRect(), rr = rect();
    if (!r) return;
    const w = pop.offsetWidth;
    pop.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - w / 2, rr.width - w - 8))}px`;
    pop.style.bottom = `${Math.max(8, rr.bottom - r.top + 10)}px`;
  }
  function paintPenPop() {
    if (popKind === 'eraser') {
      penPop.setAttribute('aria-label', 'Eraser options');
      penPop.innerHTML = `<div class="wb-pb"><div class="wb-ph5">Eraser</div><div class="wb-modes">
          <button type="button" data-em="whole" class="${prefs.erMode === 'whole' ? 'on' : ''}" aria-pressed="${prefs.erMode === 'whole'}"><span><b>Whole stroke</b><small>Touch a line to remove all of it</small></span></button>
          <button type="button" data-em="part" class="${prefs.erMode === 'part' ? 'on' : ''}" aria-pressed="${prefs.erMode === 'part'}"><span><b>Part of a stroke</b><small>Rub out only where you pass</small></span></button>
        </div></div>
        <div class="wb-pb"><div class="wb-ph5">Size</div><div class="wb-sizes">${ERASER_SIZES.map((v, i) => `<button type="button" data-esz="${v}" class="${prefs.size.eraser === v ? 'on' : ''}" title="${SIZE_NAMES[i]} · ${v} px" aria-label="${SIZE_NAMES[i]} eraser"><i class="wb-ring" style="width:${5 + i * 5}px;height:${5 + i * 5}px"></i></button>`).join('')}</div></div>`;
      return;
    }
    const t = prefs.ptype, def = PEN_TYPES[t], s = prefs.size[t];
    penPop.setAttribute('aria-label', 'Pen options');
    penPop.innerHTML = `<div class="wb-pb"><div class="wb-ph5">Pen</div><div class="wb-ptypes">${Object.entries(PEN_TYPES).map(([k, v]) => `<button type="button" data-pt="${k}" class="${t === k ? 'on' : ''}" aria-pressed="${t === k}">${icon(k === 'pen' ? 'pen' : k)}<span>${v.label}</span></button>`).join('')}</div></div>
      <div class="wb-pb"><div class="wb-ph5">Size</div><div class="wb-sizes">${def.sizes.map((v, i) => `<button type="button" data-sz="${v}" class="${s === v ? 'on' : ''}" title="${SIZE_NAMES[i]} · ${v} px" aria-label="${SIZE_NAMES[i]}"><i class="wb-dotsz" style="width:${Math.min(22, 3 + v * 0.7)}px;height:${Math.min(22, 3 + v * 0.7)}px"></i></button>`).join('')}</div>
        <label class="wb-rl"><span>Custom</span><b>${s} px</b></label><input type="range" min="1" max="40" value="${s}" data-range="size" aria-label="Pen size">
        <canvas class="wb-prev" height="40" aria-hidden="true"></canvas></div>
      <div class="wb-pb"><div class="wb-rowsb"><div class="wb-ph5">Colour</div><button type="button" class="wb-morec" data-act="colours">More colours</button></div>
        <div class="wb-swatches">${BRIGHT.map(c => swatchBtn(c, prefs.color[t] === c)).join('')}</div></div>
      <div class="wb-pb"><label class="wb-rl"><span>Smoothing</span><b>${smoothLabel()}</b></label><input type="range" min="0" max="100" value="${prefs.smooth}" data-range="smooth" aria-label="Smoothing">
        <label class="wb-chk"><input type="checkbox" data-snap ${prefs.snap ? 'checked' : ''}> Hold at the end to make a neat shape</label></div>`;
    requestAnimationFrame(drawPreview);
  }
  const smoothLabel = () => prefs.smooth < 15 ? 'Off' : prefs.smooth < 40 ? 'Light' : prefs.smooth < 70 ? 'Medium' : 'Strong';
  function drawPreview() {
    const c = penPop.querySelector('.wb-prev'); if (!c) return;
    const r = c.getBoundingClientRect(); if (!r.width) return;
    const dpr = window.devicePixelRatio || 1; c.width = r.width * dpr; c.height = 40 * dpr;
    const x = c.getContext('2d'); if (!x) return; x.setTransform(dpr, 0, 0, dpr, 0, 0);
    const t = prefs.ptype, size = prefs.size[t];
    x.strokeStyle = colourCss(prefs.color[t]); x.lineCap = t === 'highlighter' ? 'butt' : 'round'; x.lineJoin = 'round';
    x.globalAlpha = (t === 'highlighter' ? 0.4 : t === 'pencil' ? 0.85 : 1) * prefs.opacity / 100;
    const n = 40;
    for (let i = 1; i < n; i++) {
      const p0 = [12 + (i - 1) * (r.width - 24) / (n - 1), 20 + Math.sin((i - 1) / 6) * 8], p1 = [12 + i * (r.width - 24) / (n - 1), 20 + Math.sin(i / 6) * 8];
      x.lineWidth = t === 'pen' ? size * (0.55 + 0.6 * Math.sin(i / n * Math.PI)) : size;
      x.beginPath(); x.moveTo(p0[0], p0[1]); x.lineTo(p1[0], p1[1]); x.stroke();
    }
  }
  penPop.addEventListener('pointerdown', e => e.stopPropagation());
  penPop.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.pt) {
      prefs.ptype = b.dataset.pt; savePrefs(prefs);
      tool = b.dataset.pt === 'highlighter' ? 'highlighter' : 'pen'; root.dataset.tool = tool;
      paintToolbar(); paintPenPop(); return;
    }
    if (b.dataset.sz) { setSize(prefs.ptype, +b.dataset.sz); return; }
    if (b.dataset.esz) { setSize('eraser', +b.dataset.esz); return; }
    if (b.dataset.em) { prefs.erMode = b.dataset.em; savePrefs(prefs); paintPenPop(); return; }
    if (b.dataset.c) { pickColour(b.dataset.c, 'pen'); return; }
    if (b.dataset.act === 'colours') { openColours('pen', b); }
  });
  penPop.addEventListener('input', e => {
    const r = e.target.closest('[data-range]'); if (!r) return;
    if (r.dataset.range === 'size') { prefs.size[prefs.ptype] = +r.value; savePrefs(prefs); penPop.querySelector('.wb-rl b').textContent = `${r.value} px`; drawPreview(); penPop.querySelectorAll('[data-sz]').forEach(x => x.classList.toggle('on', +x.dataset.sz === +r.value)); }
    if (r.dataset.range === 'smooth') { prefs.smooth = +r.value; savePrefs(prefs); r.previousElementSibling.querySelector('b').textContent = smoothLabel(); }
  });
  penPop.addEventListener('change', e => {
    if (e.target.matches('[data-range="size"]')) setSize(prefs.ptype, +e.target.value);
    if (e.target.matches('[data-snap]')) { prefs.snap = e.target.checked; savePrefs(prefs); }
  });

  // ── Colour panel: 16 colours, recent ones, any colour (wheel + hex) and opacity; Line and Fill for shapes ──
  let colourFor = 'pen', colourTab = 'stroke';
  function openColours(forWhat, anchor) {
    colourFor = forWhat;
    const els = [...sel].map(get).filter(Boolean);
    const hasFill = forWhat === 'sel' && els.some(x => SHAPES.includes(x.type));
    if (!hasFill) colourTab = 'stroke';
    colourPop.hidden = false;
    paintColours(hasFill);
    if (forWhat === 'pen' && !isPhone()) {
      const pr = penPop.getBoundingClientRect(), rr = rect();
      const left = pr.right - rr.left + 10;
      colourPop.classList.remove('wb-sheet');
      colourPop.style.left = `${left + colourPop.offsetWidth > rr.width - 8 ? Math.max(8, pr.left - rr.left - colourPop.offsetWidth - 10) : left}px`;
      colourPop.style.top = ''; colourPop.style.bottom = penPop.style.bottom;
    } else if (forWhat === 'sel' && !isPhone()) {
      colourPop.classList.remove('wb-sheet');
      const r = anchor.getBoundingClientRect(), rr = rect();
      colourPop.style.bottom = '';
      const top = r.bottom - rr.top + 8;
      colourPop.style.top = `${top + colourPop.offsetHeight > rr.height - 8 ? Math.max(8, r.top - rr.top - colourPop.offsetHeight - 8) : top}px`;
      colourPop.style.left = `${Math.max(8, Math.min(r.left - rr.left - 20, rr.width - colourPop.offsetWidth - 8))}px`;
    } else colourPop.classList.add('wb-sheet');
  }
  function currentColour() {
    if (colourFor === 'pen') return prefs.color[prefs.ptype];
    const els = [...sel].map(get).filter(Boolean);
    const s = colourTab === 'fill' ? els.find(x => SHAPES.includes(x.type)) : els[0];
    if (!s) return 'auto';
    if (colourTab === 'fill') return s.fill === 'none' ? 'none' : (s.fill || 'auto');
    return s.stroke || 'auto';
  }
  function currentOpacity() {
    if (colourFor === 'pen') return prefs.opacity;
    const s = [...sel].map(get).find(Boolean);
    return Math.round((s?.opacity ?? 1) * 100);
  }
  function paintColours(hasFill = colourPop.querySelector('[data-ct]') != null) {
    const cur = currentColour();
    const list = colourTab === 'fill' ? ['none', ...ALL_COLOURS.slice(1)] : ALL_COLOURS;
    const hex = isHex(cur) ? cur.toUpperCase() : '#000000';
    colourPop.innerHTML = `${hasFill ? `<div class="wb-seg wb-ctabs" role="tablist"><button type="button" role="tab" data-ct="stroke" class="${colourTab === 'stroke' ? 'on' : ''}" aria-selected="${colourTab === 'stroke'}">Line</button><button type="button" role="tab" data-ct="fill" class="${colourTab === 'fill' ? 'on' : ''}" aria-selected="${colourTab === 'fill'}">Fill</button></div>` : ''}
      <div class="wb-ph5">Colours</div><div class="wb-swatches">${list.map(c => swatchBtn(c, cur === c)).join('')}</div>
      <div class="wb-ph5">Recent</div><div class="wb-recent">${prefs.recent.length ? prefs.recent.map(c => swatchBtn(c, cur === c)).join('') : '<span class="wb-note">Colours you pick show up here</span>'}</div>
      <div class="wb-ph5">Custom</div><div class="wb-custom"><input type="color" value="${hex}" data-picker aria-label="Pick any colour"><input type="text" value="${hex}" maxlength="7" spellcheck="false" data-hex aria-label="Hex code"></div>
      <label class="wb-rl"><span>Opacity</span><b>${currentOpacity()}%</b></label><input type="range" min="10" max="100" value="${currentOpacity()}" data-range="opacity" aria-label="Opacity">`;
  }
  function pickColour(c, forWhat = colourFor) {
    if (isHex(c)) { prefs.recent = [c.toUpperCase(), ...prefs.recent.filter(x => x.toUpperCase() !== c.toUpperCase())].slice(0, 8); }
    if (forWhat === 'pen') {
      prefs.color[prefs.ptype] = c === 'none' ? 'auto' : c;
      savePrefs(prefs);
      applyToSelection(x => x.type === 'path' ? { stroke: isHex(c) ? c : null } : null);
      paintToolbar(); if (!penPop.hidden) paintPenPop();
    } else {
      savePrefs(prefs);
      if (colourTab === 'fill') applyToSelection(x => SHAPES.includes(x.type) ? { fill: c === 'auto' ? null : c } : null);
      else applyToSelection(x => x.type === 'sticky' || x.type === 'frame' || x.type === 'image' || x.type === 'sticker' ? null : { stroke: isHex(c) ? c : null, ...(SHAPES.includes(x.type) ? { textColor: isHex(c) ? c : null } : {}) });
      paintProps();
    }
    if (!colourPop.hidden) paintColours();
  }
  colourPop.addEventListener('pointerdown', e => e.stopPropagation());
  colourPop.addEventListener('click', e => {
    const t = e.target.closest('[data-ct]'); if (t) { colourTab = t.dataset.ct; paintColours(true); return; }
    const b = e.target.closest('[data-c]'); if (b) pickColour(b.dataset.c);
  });
  colourPop.addEventListener('change', e => {
    if (e.target.matches('[data-picker]')) pickColour(e.target.value.toUpperCase());
    if (e.target.matches('[data-range="opacity"]')) {
      const v = +e.target.value;
      if (colourFor === 'pen') { prefs.opacity = v; savePrefs(prefs); }
      else applyToSelection(() => ({ opacity: v >= 100 ? undefined : v / 100 }));
    }
  });
  colourPop.addEventListener('input', e => { if (e.target.matches('[data-range="opacity"]')) e.target.previousElementSibling.querySelector('b').textContent = `${e.target.value}%`; });
  colourPop.addEventListener('keydown', e => {
    e.stopPropagation();
    if (e.key === 'Escape') { colourPop.hidden = true; root.focus({ preventScroll: true }); return; }
    if (e.key === 'Enter' && e.target.matches('[data-hex]')) { let v = e.target.value.trim(); if (!v.startsWith('#')) v = `#${v}`; if (isHex(v)) pickColour(v.toUpperCase()); else e.target.select(); }
  });
  penPop.addEventListener('keydown', e => { if (e.key === 'Escape') { e.stopPropagation(); penPop.hidden = true; colourPop.hidden = true; root.focus({ preventScroll: true }); } });

  // the eraser shows its size under the pointer, so you know what it will remove
  function paintEraserRing(sx, sy) {
    if (tool !== 'eraser') { eraserRing.hidden = true; return; }
    const d = prefs.size.eraser;
    eraserRing.style.width = eraserRing.style.height = `${d}px`;
    if (sx != null) { eraserRing.style.left = `${sx - d / 2}px`; eraserRing.style.top = `${sy - d / 2}px`; eraserRing.hidden = false; }
  }
  root.addEventListener('pointermove', e => { if (tool === 'eraser' && e.pointerType !== 'touch') { const { sx, sy } = local(e); paintEraserRing(sx, sy); } });
  root.addEventListener('pointerleave', () => { eraserRing.hidden = true; });

  function toggleShapeMenu(anchor) {
    if (!shapeMenu.hidden) { shapeMenu.hidden = true; return; }
    shapeMenu.innerHTML = SHAPES.map(s => `<button type="button" data-shape="${s}" class="${s === lastShape ? 'on' : ''}" title="${SHAPE_LABEL[s]}" aria-label="${SHAPE_LABEL[s]}">${icon(s)}</button>`).join('');
    shapeMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    shapeMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - shapeMenu.offsetWidth / 2, rr.width - shapeMenu.offsetWidth - 8))}px`;
    shapeMenu.style.bottom = `${rr.bottom - r.top + 8}px`;
  }
  shapeMenu.addEventListener('pointerdown', e => e.stopPropagation());
  shapeMenu.addEventListener('click', e => { const b = e.target.closest('[data-shape]'); if (!b) return; lastShape = b.dataset.shape; shapeMenu.hidden = true; setTool(lastShape); });

  function setTool(t) {
    if (t === 'pen' && prefs.ptype === 'highlighter') t = 'highlighter';
    if (t === 'highlighter') { prefs.ptype = 'highlighter'; savePrefs(prefs); }
    else if (t === 'pen' && prefs.ptype === 'highlighter') { prefs.ptype = 'pen'; savePrefs(prefs); }
    tool = t;
    if (t !== 'select') { sel.clear(); }
    root.dataset.tool = SHAPES.includes(t) ? 'shape' : t;
    shapeMenu.hidden = true;
    if (t !== 'pen' && t !== 'highlighter' && t !== 'eraser') penPop.hidden = true;
    eraserRing.hidden = t !== 'eraser';
    paintToolbar(); render();
  }

  // ── Corners: templates and full screen ──────────────────────────────────
  const TPL_EMOJI = { retro: '🔁', flowchart: '🔀', brainstorm: '💡', mindmap: '🧠', kanban: '🗂️', swot: '🧭', timeline: '🗓️', journey: '🚶', fishbone: '🐟', agenda: '📋', orgchart: '🏢' };
  const galleryHtml = () => TEMPLATE_INFO.map(([k, n, d]) => `<button type="button" class="wb-tpl" data-tpl="${k}"><span class="wb-tpl-e" aria-hidden="true">${TPL_EMOJI[k] || '▦'}</span><span><b>${n}</b><small>${d}</small></span></button>`).join('');
  let fullscreen = false;
  function paintCorners() {
    const canTemplates = !readOnly && mode === 'board';
    topLeft.innerHTML = canTemplates ? `<button type="button" class="wb-chip" data-c="tpl" aria-haspopup="dialog">${icon('grid')}<span>Templates</span></button>`
      + (opts.library ? `<button type="button" class="wb-chip" data-c="lib" aria-haspopup="dialog">${icon('book')}<span>Library</span></button>` : '') : '';
    topLeft.hidden = !canTemplates;
    const fs = mode === 'board' && opts.onFullscreen;
    const extras = mode === 'board' ? `<button type="button" class="wb-chip" data-c="frames" title="Frames" aria-haspopup="dialog">${icon('frames')}<span>Frames</span></button>`
      + `<button type="button" class="wb-chip" data-c="present" title="Present frame by frame">${icon('play')}<span>Present</span></button>`
      + (aw ? `<button type="button" class="wb-chip ${timerEnds ? 'on' : ''}" data-c="timer" title="Timer everyone sees">${icon('timer')}<span>${timerEnds ? timerLabel() : 'Timer'}</span></button>` : '')
      + (vmap ? `<button type="button" class="wb-chip ${votingOn() ? 'on' : ''}" data-c="vote" title="Dot voting">${icon('vote')}<span>${votingOn() ? `Voting · ${Math.max(0, (round()?.per || 0) - myVotes().length)} left` : 'Vote'}</span></button>` : '')
      + `<button type="button" class="wb-chip" data-c="export" title="Export or copy">${icon('download')}<span>Export</span></button>` : '';
    people.remove();
    topRight.innerHTML = extras + (fs ? `<button type="button" class="wb-chip" data-c="fs" title="Full screen (Shift F)" aria-pressed="${fullscreen}"><svg class="wb-i" viewBox="0 0 24 24" aria-hidden="true">${fullscreen ? '<path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/>' : '<path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/>'}</svg><span>${fullscreen ? 'Exit full screen' : 'Full screen'}</span></button>` : '');
    topRight.hidden = !fs && !extras && people.hidden;
    topRight.prepend(people);
  }
  // While full screen, Esc must work wherever focus is (moving the board element to <body> drops its focus).
  const fsKeys = e => { if (e.key === 'Escape' && presenting) { e.preventDefault(); e.stopPropagation(); stopPresent(); return; }
    if (e.key === 'Escape' && fullscreen && !editing && keysPanel.hidden && gallery.hidden) { e.preventDefault(); e.stopPropagation(); toggleFullscreen(); } };
  async function toggleFullscreen() {
    if (!opts.onFullscreen) return;
    const r = await opts.onFullscreen(!fullscreen);
    fullscreen = typeof r === 'boolean' ? r : !fullscreen;
    if (fullscreen) document.addEventListener('keydown', fsKeys, true); else document.removeEventListener('keydown', fsKeys, true);
    paintCorners();
    requestAnimationFrame(() => { fit(); root.focus({ preventScroll: true }); });
  }
  for (const c of [topLeft, topRight, gallery, emptyGallery, libPanel]) c.addEventListener('pointerdown', e => e.stopPropagation());
  topLeft.addEventListener('click', e => {
    if (e.target.closest('[data-c="lib"]')) { toggleLibrary(); return; }
    if (!e.target.closest('[data-c="tpl"]')) return;
    if (!gallery.hidden) { gallery.hidden = true; return; }
    gallery.innerHTML = `<div class="wb-ph"><b>Start from a template</b><button type="button" data-close aria-label="Close">✕</button></div><p class="wb-note">Added beside what is already on the board, never over it.</p><div class="wb-tgrid">${galleryHtml()}</div>`;
    gallery.hidden = false;
  });
  topRight.addEventListener('click', e => {
    if (e.target.closest('[data-c="fs"]')) toggleFullscreen();
    else if (e.target.closest('[data-c="frames"]')) toggleFrames();
    else if (e.target.closest('[data-c="present"]')) startPresent();
    else if (e.target.closest('[data-c="timer"]')) toggleTimerPanel();
    else if (e.target.closest('[data-c="vote"]')) toggleVotePanel();
    else if (e.target.closest('[data-c="export"]')) toggleExport();
  });
  const pickTemplate = e => {
    if (e.target.closest('[data-close]')) { gallery.hidden = true; return; }
    const b = e.target.closest('[data-tpl]'); if (!b) return;
    gallery.hidden = true;
    const n = api.applyTemplate(b.dataset.tpl);
    if (n) toast('Template added. Change anything on it.');
  };
  gallery.addEventListener('click', pickTemplate);
  emptyGallery.addEventListener('click', pickTemplate);
  function paintEmpty(isEmpty) {
    const show = isEmpty && !readOnly && mode === 'board' && !interaction;
    if (show && emptyGallery.hidden) emptyGallery.innerHTML = `<b>Start with a blank board, or pick a template</b><div class="wb-tgrid">${galleryHtml()}</div><p class="wb-note">Or pick a tool below and start drawing.</p>`;
    emptyGallery.hidden = !show;
    root.classList.toggle('wb-empty', isEmpty && !show);
  }

  // ── Working together: cursors, what others hold, Follow, laser ──────────
  // Shared through the page's Yjs awareness (field "wb"): { board, x, y, sel, laser:[[x,y,t]...], view:{x,y,z,w,h} }.
  // Nothing of this is stored; it disappears when someone leaves.
  const boardKey = opts.boardKey || 'board';
  function now() { return Date.now(); }
  function setMine(patch) {
    if (!aw) return;
    const cur = aw.getLocalState()?.wb || {};
    aw.setLocalStateField('wb', { ...cur, board: boardKey, ...patch });
  }
  let cursorTimer = 0;
  root.addEventListener('pointermove', e => {
    if (!aw) return;
    const { sx, sy } = local(e);
    lastCursor = toWorld(sx, sy);
    if (!cursorTimer) cursorTimer = setTimeout(() => { cursorTimer = 0; setMine({ x: Math.round(lastCursor.x), y: Math.round(lastCursor.y) }); }, 50);
  });
  root.addEventListener('pointerleave', () => { if (aw) setMine({ x: null, y: null }); });
  let viewTimer = 0;
  function shareView() {
    if (!aw || viewTimer) return;
    viewTimer = setTimeout(() => { viewTimer = 0; const r = rect(); setMine({ view: { x: Math.round(view.x), y: Math.round(view.y), z: +view.z.toFixed(3), w: Math.round(r.width), h: Math.round(r.height) } }); }, 120);
  }
  function laserAt(w) {
    laserTrail.push([Math.round(w.x), Math.round(w.y), now()]);
    laserTrail = laserTrail.filter(p => now() - p[2] < 1200).slice(-40);
    setMine({ laser: laserTrail });
    paintLive();
    clearTimeout(laserFade); laserFade = setTimeout(function fade() { laserTrail = laserTrail.filter(p => now() - p[2] < 1200); setMine({ laser: laserTrail }); paintLive(); if (laserTrail.length) laserFade = setTimeout(fade, 120); }, 120);
  }
  // One face per person: the same person in two tabs (or a tab that just reloaded) is shown once, using the newest state.
  const others = () => {
    const byPerson = new Map();
    aw?.getStates().forEach((st, id) => {
      if (id === aw.clientID || st?.wb?.board !== boardKey || !st.user) return;
      const key = st.user.nik || st.user.name || String(id);
      if (key === (aw.getLocalState()?.user?.nik ?? null)) return;   // my own other tab
      const prev = byPerson.get(key);
      if (!prev || id > prev.id) byPerson.set(key, { id, user: st.user, wb: st.wb });
    });
    return [...byPerson.values()];
  };
  const liveLayer = svgEl('g', { class: 'wb-live' }, svg);
  function paintLive() {
    liveLayer.innerHTML = '';
    const list = others();
    for (const o of list) {
      const color = o.user.color || '#5856D6';
      // what they hold
      for (const id of o.wb.sel || []) {
        const e = get(id); if (!e || e.isDeleted) continue;
        const b = bounds(e, get), a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h);
        svgEl('rect', { x: a.x - 5, y: a.y - 5, width: z.x - a.x + 10, height: z.y - a.y + 10, rx: 5, fill: 'none', stroke: color, 'stroke-width': 2, 'stroke-dasharray': '5 3' }, liveLayer);
      }
      // laser trail
      const trail = (o.wb.laser || []).filter(p => now() - p[2] < 1500);
      if (trail.length > 1) {
        let d = '';
        trail.forEach((p, i) => { const q = toScreen(p[0], p[1]); d += `${i ? 'L' : 'M'}${q.x.toFixed(1)} ${q.y.toFixed(1)}`; });
        svgEl('path', { d, fill: 'none', stroke: '#FF3B30', 'stroke-width': 5, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', opacity: 0.55 }, liveLayer);
        const l = toScreen(trail.at(-1)[0], trail.at(-1)[1]);
        svgEl('circle', { cx: l.x, cy: l.y, r: 6, fill: '#FF3B30' }, liveLayer);
      }
      // cursor + name
      if (Number.isFinite(o.wb.x) && Number.isFinite(o.wb.y)) {
        const c = toScreen(o.wb.x, o.wb.y);
        const g = svgEl('g', { transform: `translate(${c.x.toFixed(1)} ${c.y.toFixed(1)})`, class: 'wb-cursor' }, liveLayer);
        svgEl('path', { d: 'M0 0L5.5 15 7.7 8.6 14.5 6.6Z', fill: color, stroke: '#fff', 'stroke-width': 1.3, 'stroke-linejoin': 'round' }, g);
        const name = String(o.user.name || 'Someone').split(' ')[0];
        const w = measure(name, 11, 'ipop', 600) + 14;
        svgEl('rect', { x: 12, y: 14, width: w, height: 18, rx: 6, fill: color }, g);
        const t = svgEl('text', { x: 19, y: 26.5, 'font-size': 11, 'font-weight': 600, fill: '#fff', 'font-family': FONT_STACK.ipop }, g);
        t.textContent = name;
      }
    }
    // my own laser
    if (laserTrail.length > 1) {
      let d = '';
      laserTrail.forEach((p, i) => { const q = toScreen(p[0], p[1]); d += `${i ? 'L' : 'M'}${q.x.toFixed(1)} ${q.y.toFixed(1)}`; });
      svgEl('path', { d, fill: 'none', stroke: '#FF3B30', 'stroke-width': 5, 'stroke-linecap': 'round', opacity: 0.55 }, liveLayer);
    }
    paintPeople(list);
    checkPresenters(list);
    syncTimer(list);
    if (followingPresenter != null && following !== followingPresenter) following = followingPresenter;
    if (following != null) followStep(list);
  }
  function paintPeople(list) {
    if (!aw) { people.hidden = true; return; }
    people.hidden = !list.length;
    people.innerHTML = list.slice(0, 6).map(o => {
      const name = String(o.user.name || 'Someone');
      const ini = name.split(/\s+/).map(w => w[0]).join('').slice(0, 2).toUpperCase();
      return `<button type="button" class="wb-face ${following === o.id ? 'on' : ''}" data-follow="${o.id}" style="background:${o.user.color || '#5856D6'}" title="${following === o.id ? 'Stop following' : 'Follow'} ${name.replace(/"/g, '')}" aria-label="${following === o.id ? 'Stop following' : 'Follow'} ${name.replace(/"/g, '')}">${ini}</button>`;
    }).join('') + (list.length > 6 ? `<span class="wb-face more">+${list.length - 6}</span>` : '');
  }
  function followStep(list) {
    const o = list.find(x => x.id === following);
    if (!o) { stopFollow(); return; }
    const v = o.wb.view; if (!v) return;
    const r = rect();
    // show the same middle of the board at the same zoom (the other screen may be a different size)
    const cx = (v.w / 2 - v.x) / v.z, cy = (v.h / 2 - v.y) / v.z;
    view = { z: v.z, x: r.width / 2 - cx * v.z, y: r.height / 2 - cy * v.z };
    followBar.style.setProperty('--fc', o.user.color || '#5856D6');
    followBar.innerHTML = `<span>Following ${String(o.user.name || 'someone').split(' ')[0]}</span><button type="button" data-unfollow>Stop</button>`;
    followBar.hidden = false;
    root.style.setProperty('--wb-follow', o.user.color || '#5856D6');
    root.classList.add('wb-following');
    world.setAttribute('transform', `translate(${view.x} ${view.y}) scale(${view.z})`);
    root.style.backgroundSize = `${24 * view.z}px ${24 * view.z}px`;
    root.style.backgroundPosition = `${view.x}px ${view.y}px`;
    paintOverlay(); paintZoom();
  }
  function stopFollow() {
    if (following == null) return;
    following = null; followingPresenter = null; followBar.hidden = true; root.classList.remove('wb-following');
    paintPeople(others());
  }
  people.addEventListener('pointerdown', e => e.stopPropagation());
  followBar.addEventListener('pointerdown', e => e.stopPropagation());
  people.addEventListener('click', e => {
    const b = e.target.closest('[data-follow]'); if (!b) return;
    const id = +b.dataset.follow;
    if (following === id) stopFollow(); else { following = id; paintLive(); toast('Following. Move the board yourself or press Esc to stop.'); }
  });
  followBar.addEventListener('click', e => { if (e.target.closest('[data-unfollow]')) stopFollow(); });
  const onAw = () => { if (!destroyed) paintLive(); };
  aw?.on('change', onAw);
  if (aw) setMine({ x: null, y: null, sel: [], laser: [] });
  // the laser trail fades on everyone's screen even without new messages
  afterPaint = () => {
    paintPins(); paintVotes(); paintMini();
    paintLive();
    shareView();
    if (aw) { const s2 = [...sel].sort().join(','); if (s2 !== afterPaint.lastSel) { afterPaint.lastSel = s2; setMine({ sel: [...sel].slice(0, 50) }); } }
  };
  const liveTick = setInterval(() => { if (!destroyed && others().some(o => o.wb.laser?.length)) paintLive(); }, 150);

  // ── Frames list, Present and the timer ───────────────────────────────────
  for (const c of [framesPanel, timerPanel, presBar, invite]) c.addEventListener('pointerdown', e => e.stopPropagation());
  function framesInOrder() { return live().filter(e => e.type === 'frame').sort((a, b) => (a.order ?? 1e9) - (b.order ?? 1e9) || a.y - b.y || a.x - b.x); }
  function showFrame(f, { animate = true } = {}) {
    if (!f) return;
    const r = rect();
    const z = Math.min(4, (r.width - 80) / Math.max(f.w, 1), (r.height - 120) / Math.max(f.h + 30, 1));
    view = { z, x: (r.width - f.w * z) / 2 - f.x * z, y: (r.height - f.h * z) / 2 - f.y * z + 10 };
    render();
  }
  function toggleFrames() {
    if (!framesPanel.hidden) { framesPanel.hidden = true; return; }
    gallery.hidden = true; libPanel.hidden = true; timerPanel.hidden = true;
    paintFrames(); framesPanel.hidden = false;
  }
  function paintFrames() {
    const fs = framesInOrder();
    framesPanel.innerHTML = `<div class="wb-ph"><b>Frames</b><button type="button" data-close aria-label="Close">✕</button></div>`
      + (fs.length ? `<p class="wb-note">Click to go there. ${readOnly ? '' : 'Use the arrows to change the order of the slides.'}</p><ol class="wb-flist">${fs.map((f, i) => `<li><button type="button" class="wb-fgo" data-go="${f.id}"><span class="wb-fnum">${i + 1}</span>${esc(f.name || 'Frame')}</button>${readOnly ? '' : `<span class="wb-fmv"><button type="button" data-up="${f.id}" aria-label="Earlier" ${i ? '' : 'disabled'}>↑</button><button type="button" data-down="${f.id}" aria-label="Later" ${i < fs.length - 1 ? '' : 'disabled'}>↓</button></span>`}</li>`).join('')}</ol>`
        : `<p class="wb-note">No frames yet. Press <b>F</b> and drag over an area to make one. Each frame is a slide when you present.</p>`);
  }
  framesPanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { framesPanel.hidden = true; return; }
    const go = e.target.closest('[data-go]'); if (go) { showFrame(get(go.dataset.go)); return; }
    const up = e.target.closest('[data-up]'), down = e.target.closest('[data-down]');
    if ((up || down) && !readOnly) {
      const fs = framesInOrder(), id = (up || down).dataset.up || (up || down).dataset.down, i = fs.findIndex(f => f.id === id), j = up ? i - 1 : i + 1;
      if (i < 0 || j < 0 || j >= fs.length) return;
      [fs[i], fs[j]] = [fs[j], fs[i]];
      write(fs.map((f, k) => (f.order === k ? null : bump(f, { order: k }))).filter(Boolean), { now: true }); commitStop();
      paintFrames();
    }
  });
  // Present: one frame at a time, full screen; others are invited (never pulled along).
  function startPresent() {
    const fs = framesInOrder();
    if (!fs.length) { toast('Add a frame first (press F and drag). Each frame is a slide.'); return; }
    framesPanel.hidden = true;
    presenting = { index: 0 };
    if (!fullscreen && opts.onFullscreen) toggleFullscreen();
    root.classList.add('wb-presenting');
    goSlide(0);
  }
  function goSlide(i) {
    const fs = framesInOrder();
    if (!presenting || !fs.length) return;
    presenting.index = Math.max(0, Math.min(fs.length - 1, i));
    const f = fs[presenting.index];
    requestAnimationFrame(() => showFrame(f));
    setMine({ present: { frame: f.id, at: now() } });
    presBar.innerHTML = `<button type="button" data-p="prev" aria-label="Previous slide" ${presenting.index ? '' : 'disabled'}>‹</button><span>${presenting.index + 1} / ${fs.length} · ${esc(f.name || 'Frame')}</span><button type="button" data-p="next" aria-label="Next slide" ${presenting.index < fs.length - 1 ? '' : 'disabled'}>›</button><button type="button" data-p="stop" class="wb-stop">Stop</button>`;
    presBar.hidden = false;
  }
  function stopPresent() {
    if (!presenting) return;
    presenting = null; presBar.hidden = true; root.classList.remove('wb-presenting');
    setMine({ present: null });
    if (fullscreen) toggleFullscreen();
  }
  presBar.addEventListener('click', e => {
    const b = e.target.closest('[data-p]'); if (!b) return;
    if (b.dataset.p === 'prev') goSlide(presenting.index - 1);
    else if (b.dataset.p === 'next') goSlide(presenting.index + 1);
    else stopPresent();
  });
  // someone else presenting: one quiet invitation; following them = Follow + their slide
  function checkPresenters(list) {
    const p = list.find(o => o.wb.present?.frame);
    if (!p) { invite.hidden = true; invitedFor = null; if (followingPresenter != null) { followingPresenter = null; stopFollow(); } return; }
    if (followingPresenter === p.id) { const f = get(p.wb.present.frame); if (f && p.wb.present.frame !== checkPresenters.last) { checkPresenters.last = p.wb.present.frame; showFrame(f); } return; }
    if (invitedFor === p.id || presenting) return;
    invitedFor = p.id;
    invite.innerHTML = `<span><b>${esc(String(p.user.name || 'Someone').split(' ')[0])}</b> is presenting.</span><button type="button" data-i="follow" class="wb-pri">Follow</button><button type="button" data-i="no">Not now</button>`;
    invite.hidden = false;
  }
  invite.addEventListener('click', e => {
    const b = e.target.closest('[data-i]'); if (!b) return;
    invite.hidden = true;
    if (b.dataset.i === 'follow') { followingPresenter = invitedFor; checkPresenters.last = null; paintLive(); }
  });
  // Timer: a countdown everyone on the board sees (it lives in the starter's awareness; ends for all at the same moment).
  function timerLabel() { const left = Math.max(0, Math.ceil((timerEnds - now()) / 1000)); return `${String(Math.floor(left / 60)).padStart(2, '0')}:${String(left % 60).padStart(2, '0')}`; }
  function toggleTimerPanel() {
    if (!timerPanel.hidden) { timerPanel.hidden = true; return; }
    framesPanel.hidden = true; gallery.hidden = true; libPanel.hidden = true;
    timerPanel.innerHTML = `<div class="wb-ph"><b>Timer</b><button type="button" data-close aria-label="Close">✕</button></div><p class="wb-note">Everyone on this board sees it count down.</p><div class="wb-tbtns">${[1, 3, 5, 10, 15].map(m => `<button type="button" data-min="${m}">${m} min</button>`).join('')}</div>${timerEnds ? '<button type="button" data-min="0" class="wb-tstop">Stop the timer</button>' : ''}`;
    timerPanel.hidden = false;
  }
  timerPanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { timerPanel.hidden = true; return; }
    const b = e.target.closest('[data-min]'); if (!b) return;
    const m = +b.dataset.min;
    setMine({ timer: m ? { ends: now() + m * 60000 } : null });
    timerPanel.hidden = true;
    syncTimer(others());
  });
  function syncTimer(list) {
    // the latest running timer wins (mine or someone else's)
    const all3 = [...list.map(o => o.wb.timer?.ends || 0), aw?.getLocalState()?.wb?.timer?.ends || 0].filter(t => t > now() - 3000);
    const ends = all3.length ? Math.max(...all3) : 0;
    if (ends !== timerEnds) { timerEnds = ends; timerBeeped = 0; }
    if (timerEnds && !timerTick) timerTick = setInterval(() => {
      if (destroyed) return;
      if (timerEnds && now() >= timerEnds && !timerBeeped) { timerBeeped = 1; toast('Time is up'); }
      if (timerEnds && now() > timerEnds + 3000) { timerEnds = 0; clearInterval(timerTick); timerTick = 0; }
      paintCorners();
    }, 500);
    paintCorners();
  }

  // ── Comments pinned to the board ─────────────────────────────────────────
  // Uses the page's own comment threads (mentions, reactions, resolve). The anchor is {"wb":board,"x","y","el"?}: a pin at a
  // point, or on an element (then it follows the element).
  const pinLayer = el('div', 'wb-pins');
  root.append(pinLayer);
  let threads = [];
  const anchorOf = t => { try { const a = JSON.parse(t.anchor || ''); return a && a.wb === boardKey ? a : null; } catch { return null; } };
  function pinPos(a) {
    if (a.el) { const e = get(a.el); if (e && !e.isDeleted) { const b = bounds(e, get); return { x: b.x + b.w, y: b.y }; } }
    return { x: a.x, y: a.y };
  }
  function paintPins() {
    if (!opts.comments) { pinLayer.innerHTML = ''; return; }
    const list = threads.filter(t => !t.resolvedAt).map(t => ({ t, a: anchorOf(t) })).filter(x => x.a);
    pinLayer.innerHTML = list.map(({ t, a }, i) => {
      const p = toScreen(pinPos(a).x, pinPos(a).y);
      return `<button type="button" class="wb-pin" data-thread="${t.id}" style="left:${p.x.toFixed(0)}px;top:${p.y.toFixed(0)}px" title="Open comment" aria-label="Comment ${i + 1}">${t.count > 1 ? t.count : ''}</button>`;
    }).join('');
  }
  pinLayer.addEventListener('pointerdown', e => e.stopPropagation());
  pinLayer.addEventListener('click', e => { const b = e.target.closest('[data-thread]'); if (b) opts.comments?.open(b.dataset.thread); });
  function startComment(w, onEl) {
    if (!opts.comments) return;
    const anchor = JSON.stringify(onEl ? { wb: boardKey, el: onEl.id, x: Math.round(w.x), y: Math.round(w.y) } : { wb: boardKey, x: Math.round(w.x), y: Math.round(w.y) });
    const quote = onEl ? String(onEl.text || onEl.name || onEl.title || (SHAPE_LABEL[onEl.type] || onEl.type)).slice(0, 120) : 'On the board';
    opts.comments.start(quote, anchor);
  }

  // ── Voting ───────────────────────────────────────────────────────────────
  // The round lives in the shared document ("wbvote:<board>" key on the scene map is avoided: a separate Y.Map), so it survives
  // reloads and everyone sees the same round. Votes are per person; they stay hidden until the round ends.
  function myKey() { return String(opts.me?.id || aw?.getLocalState()?.user?.nik || 'me'); }
  function round() { const r = vmap?.get('round'); return r && typeof r === 'object' ? r : null; }
  function votingOn() { const r = round(); return !!r && !r.closedAt && (!r.endsAt || r.endsAt > now()); }
  function myVotes() { const v = vmap?.get(`v:${round()?.id}:${myKey()}`); return Array.isArray(v) ? v : []; }
  function tally() {
    const r = round(); if (!r) return new Map();
    const t = new Map();
    vmap.forEach((v, k) => { if (k.startsWith(`v:${r.id}:`) && Array.isArray(v)) for (const id of v) t.set(id, (t.get(id) || 0) + 1); });
    return t;
  }
  function castVote(target, takeBack) {
    const r = round(); if (!r) return;
    const mine = myVotes().slice();
    const i = mine.lastIndexOf(target.id);
    if (takeBack) { if (i < 0) return; mine.splice(i, 1); }
    else if (mine.length < r.per) mine.push(target.id);
    else { toast(`You used all ${r.per} votes. Alt-click a sticky to take one back.`); return; }
    doc.transact(() => vmap.set(`v:${r.id}:${myKey()}`, mine), ORIGIN);
    paintVotes(); paintVotePanel();
  }
  const votePanel = el('div', 'wb-panel wb-votep'); votePanel.hidden = true;
  const voteLayer = el('div', 'wb-votes');
  root.append(votePanel, voteLayer);
  votePanel.addEventListener('pointerdown', e => e.stopPropagation());
  function paintVotes() {
    const r = round();
    if (!r || !vmap) { voteLayer.innerHTML = ''; return; }
    const showAll = !!r.closedAt || (r.endsAt && r.endsAt <= now()) || !r.hidden;
    const counts = showAll ? tally() : new Map(myVotes().reduce((m, id) => m.set(id, (m.get(id) || 0) + 1), new Map()));
    voteLayer.innerHTML = [...counts].filter(([, n]) => n > 0).map(([id, n]) => {
      const e = get(id); if (!e || e.isDeleted) return '';
      const b = bounds(e, get), p = toScreen(b.x + b.w, b.y + b.h);
      return `<span class="wb-vote ${showAll ? 'all' : 'mine'}" style="left:${p.x.toFixed(0)}px;top:${p.y.toFixed(0)}px">${n}</span>`;
    }).join('');
  }
  function paintVotePanel() {
    if (votePanel.hidden) return;
    const r = round(), on = votingOn();
    if (!r || (!on && !r.closedAt && !(r.endsAt && r.endsAt <= now()))) {
      votePanel.innerHTML = `<div class="wb-ph"><b>Voting</b><button type="button" data-close aria-label="Close">✕</button></div>
        <label class="wb-row">Votes for each person <select data-per>${[1, 2, 3, 5, 10].map(n => `<option ${n === 3 ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
        <label class="wb-row">Time limit <select data-mins><option value="0">None</option>${[2, 5, 10, 15].map(n => `<option value="${n}">${n} min</option>`).join('')}</select></label>
        <label class="wb-row"><input type="checkbox" data-hide checked> Hide votes until it ends</label>
        ${readOnly ? '<p class="wb-note">Only people who can edit can start a vote.</p>' : '<button type="button" class="wb-wide wb-pri" data-start>Start voting</button>'}`;
    } else if (on) {
      const left = r.per - myVotes().length;
      votePanel.innerHTML = `<div class="wb-ph"><b>Voting is on</b><button type="button" data-close aria-label="Close">✕</button></div>
        <p class="wb-note">Click stickies or cards to vote. You have <b>${left}</b> of ${r.per} left. Alt-click takes one back.${r.endsAt ? ` Ends in ${Math.max(0, Math.ceil((r.endsAt - now()) / 60000))} min.` : ''}</p>
        ${readOnly && r.by !== myKey() ? '' : '<button type="button" class="wb-wide" data-end>End and show results</button>'}`;
    } else {
      const t = [...tally()].sort((a, b) => b[1] - a[1]).slice(0, 10);
      votePanel.innerHTML = `<div class="wb-ph"><b>Results</b><button type="button" data-close aria-label="Close">✕</button></div>`
        + (t.length ? `<ol class="wb-results">${t.map(([id, n]) => { const e = get(id); return `<li><button type="button" data-goto="${id}"><b>${n}</b> ${esc(String(e?.text || e?.title || 'Item').split('\n')[0].slice(0, 40))}</button></li>`; }).join('')}</ol>` : '<p class="wb-note">No votes were cast.</p>')
        + (readOnly ? '' : '<div class="wb-row2"><button type="button" class="wb-wide" data-sort>Line up by votes</button><button type="button" class="wb-wide" data-clear>New vote</button></div>');
    }
  }
  votePanel.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { votePanel.hidden = true; return; }
    if (e.target.closest('[data-start]') && vmap) {
      const per = +votePanel.querySelector('[data-per]').value, mins = +votePanel.querySelector('[data-mins]').value, hidden = votePanel.querySelector('[data-hide]').checked;
      doc.transact(() => {
        vmap.forEach((_, k) => { if (k.startsWith('v:')) vmap.delete(k); });
        vmap.set('round', { id: Math.random().toString(36).slice(2, 9), per, hidden, endsAt: mins ? now() + mins * 60000 : null, by: myKey(), startedAt: now() });
      }, ORIGIN);
      toast('Voting started for everyone on the board');
    }
    if (e.target.closest('[data-end]') && vmap) { const r = round(); if (r) doc.transact(() => vmap.set('round', { ...r, closedAt: now() }), ORIGIN); }
    if (e.target.closest('[data-clear]') && vmap) doc.transact(() => { vmap.forEach((_, k) => vmap.delete(k)); }, ORIGIN);
    const go = e.target.closest('[data-goto]'); if (go) { const t = get(go.dataset.goto); if (t) { sel = new Set([t.id]); fit([t], { max: 1.2 }); } }
    if (e.target.closest('[data-sort]')) sortByVotes();
    paintVotes(); paintVotePanel();
  });
  function toggleVotePanel() {
    if (!vmap) return;
    if (!votePanel.hidden) { votePanel.hidden = true; return; }
    framesPanel.hidden = true; timerPanel.hidden = true; libPanel.hidden = true; gallery.hidden = true;
    votePanel.hidden = false; paintVotePanel();
  }
  /// Results as a row: the voted items move into one line, most votes first (one undo step).
  function sortByVotes() {
    const t = [...tally()].sort((a, b) => b[1] - a[1]).map(([id]) => get(id)).filter(e => e && !e.locked);
    if (!t.length) return;
    const start = unionBounds(t, get);
    let x = start.x;
    write(t.map(e => { const m = moved(e, x - e.x, start.y - e.y); x += (e.w || 100) + 24; return m; }), { now: true });
    commitStop(); fit(t.map(e => get(e.id)), { max: 1 });
  }
  const onVote = () => { if (!destroyed) { paintVotes(); paintVotePanel(); paintCorners(); } };
  vmap?.observe(onVote);
  const unThreads = opts.comments?.subscribe?.(list => { threads = (list || []).map(t => ({ id: t.id, anchor: t.anchor, resolvedAt: t.resolvedAt, count: t.count || 1 })); paintPins(); });

  // ── Stamps ───────────────────────────────────────────────────────────────
  const STAMPS = ['👍', '❤️', '⭐', '✅', '❓', '🔥', '🎉', '👀'];
  let stampEmoji = '👍';
  const stampMenu = el('div', 'wb-menu wb-stampmenu'); stampMenu.hidden = true;
  root.append(stampMenu);
  stampMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleStampMenu(anchor) {
    if (!stampMenu.hidden) { stampMenu.hidden = true; return; }
    stampMenu.innerHTML = STAMPS.map(x => `<button type="button" data-stamp="${x}" class="${x === stampEmoji ? 'on' : ''}" aria-label="Stamp ${x}">${x}</button>`).join('');
    stampMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    stampMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left + r.width / 2 - 100, rr.width - 210))}px`;
    stampMenu.style.bottom = `${rr.bottom - r.top + 8}px`;
  }
  stampMenu.addEventListener('click', e => { const b = e.target.closest('[data-stamp]'); if (!b) return; stampEmoji = b.dataset.stamp; stampMenu.hidden = true; setTool('stamp'); });

  // ── Mind map: Tab = branch, Shift+Enter = sibling ────────────────────────
  function mindAdd(from, kind) {
    if (readOnly || from.locked) return;
    const links = live().filter(c => c.type === 'connector');
    const parentLink = links.find(c => c.to?.id === from.id);
    const parent = kind === 'sibling' ? (parentLink ? get(parentLink.from.id) : null) : from;
    if (kind === 'sibling' && !parent) { mindAdd(from, 'child'); return; }
    const kids = links.filter(c => c.from?.id === parent.id).map(c => get(c.to?.id)).filter(Boolean);
    const right = !parentLink || centre(from, get).x >= centre(get(parentLink.from.id) || from, get).x;
    const dir = kind === 'child' ? (right ? 1 : -1) : (centre(from, get).x >= centre(parent, get).x ? 1 : -1);
    const w = Math.max(160, Math.min(from.w || 160, 240)), h = Math.max(56, Math.min(from.h || 60, 90));
    const pb = bounds(parent, get);
    const x = dir > 0 ? pb.x + pb.w + 120 : pb.x - 120 - w;
    const sameSide = kids.filter(k => (centre(k, get).x - centre(parent, get).x) * dir > 0);
    const y = sameSide.length ? Math.max(...sameSide.map(k => k.y + k.h)) + 24 : pb.y + pb.h / 2 - h / 2;
    const node = create(from.type === 'sticky' ? 'sticky' : 'rect', from.type === 'sticky' ? { x, y, w, h, text: '', color: from.color } : { x, y, w, h, text: '', fill: from.fill ?? null, stroke: from.stroke ?? null });
    const link = create('connector', { from: { id: parent.id }, to: { id: node.id }, route: 'curved', endHead: 'none', startHead: 'none' });
    add([node, link], { select: false });
    sel = new Set([node.id]);
    editSoon2(node);
  }
  function editSoon2(node) { setTimeout(() => { const n = get(node.id); if (n && !destroyed) editText(n, { selectAll: true }); }, 0); }

  // ── Notes page and Projects task cards ───────────────────────────────────
  const taskCache = new Map();   // taskId -> { title, status, due, people, project, private } | 'loading'
  function ensureTask(id) {
    if (!id || taskCache.has(id) || !opts.tasks?.get) return;
    taskCache.set(id, 'loading');
    opts.tasks.get(id).then(t => { taskCache.set(id, t || { private: true }); cache.clear(); world.innerHTML = ''; render(); }, () => taskCache.set(id, { private: true }));
  }
  opts.cardInfo = { task: id => { ensureTask(id); const t = taskCache.get(id); return t === 'loading' ? null : t; }, page: id => opts.pages?.info?.(id) };
  function addTaskCards(tasks, at) {
    let y = at.y;
    const cards = tasks.map(t => { const c = create('taskCard', { x: at.x, y, w: 260, h: 104, taskId: t.id, title: t.title }); y += 120; taskCache.set(t.id, t); return c; });
    add(cards);
  }
  async function makeTasks(stickies) {
    if (!opts.tasks?.create) return;
    const r = await opts.tasks.create(stickies.map(s => ({ id: s.id, text: s.text || '' })));
    if (!r?.length) return;
    // each sticky becomes the task card in its place
    const byId = new Map(r.map(x => [x.stickyId, x.task]));
    const out = [];
    for (const s of stickies) {
      const t = byId.get(s.id); if (!t) continue;
      taskCache.set(t.id, t);
      out.push(bump(s, { isDeleted: true }), { ...create('taskCard', { x: s.x, y: s.y, w: Math.max(240, s.w), h: 104, taskId: t.id, title: t.title }), z: s.z });
    }
    write(out, { now: true }); commitStop(); sel.clear(); paintProps();
    toast(r.length === 1 ? 'Task created in Projects' : `${r.length} tasks created in Projects`);
  }
  // card picker: Notes pages (from the sidebar list) and tasks (projects I can add to)
  const cardPicker = el('div', 'wb-panel wb-cardpick'); cardPicker.hidden = true;
  root.append(cardPicker);
  cardPicker.addEventListener('pointerdown', e => e.stopPropagation());
  let cardTab = 'pages', cardQ = '', cardSeq = 0;
  function toggleCardPicker() {
    if (readOnly) return;
    if (!cardPicker.hidden) { cardPicker.hidden = true; return; }
    for (const p of [framesPanel, timerPanel, libPanel, gallery, votePanel, exportPanel]) p.hidden = true;
    cardPicker.hidden = false; paintCardPicker();
  }
  async function paintCardPicker() {
    const seq = ++cardSeq;
    cardPicker.innerHTML = `<div class="wb-ph"><b>Add a card</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-seg" role="tablist">${opts.pages ? `<button type="button" data-tab="pages" class="${cardTab === 'pages' ? 'on' : ''}">Notes page</button>` : ''}${opts.taskSearch ? `<button type="button" data-tab="tasks" class="${cardTab === 'tasks' ? 'on' : ''}">Projects task</button>` : ''}</div>
      <input class="wb-search" type="search" placeholder="${cardTab === 'pages' ? 'Find a page' : 'Find a task'}" value="${esc(cardQ)}" aria-label="Search">
      <div class="wb-cardlist"><p class="wb-note">${cardTab === 'tasks' ? 'Loading tasks…' : ''}</p></div>`;
    const input = cardPicker.querySelector('.wb-search');
    input.focus(); input.setSelectionRange(input.value.length, input.value.length);
    input.addEventListener('input', () => { cardQ = input.value; clearTimeout(paintCardPicker.t); paintCardPicker.t = setTimeout(fillCards, 200); });
    input.addEventListener('keydown', e => { e.stopPropagation(); if (e.key === 'Escape') cardPicker.hidden = true; });
    fillCards(seq);
  }
  async function fillCards(seq = cardSeq) {
    const list = cardPicker.querySelector('.wb-cardlist'); if (!list) return;
    const q = cardQ.trim().toLowerCase();
    if (cardTab === 'pages') {
      const items = (opts.pages?.all?.() || []).filter(p => !q || String(p.title || '').toLowerCase().includes(q)).slice(0, 40);
      list.innerHTML = items.length ? items.map(p => `<button type="button" data-page="${p.id}">${esc(p.emoji || '📄')} ${esc(p.title || 'Untitled')}</button>`).join('') : '<p class="wb-note">No page found.</p>';
    } else {
      const items = await opts.taskSearch(q).catch(() => []);
      if (seq !== cardSeq && cardTab !== 'tasks') return;
      list.innerHTML = items.length ? items.map(t => `<button type="button" data-task="${t.id}"><b>${esc(t.title)}</b><small>${esc(t.project || '')}</small></button>`).join('') : '<p class="wb-note">No task found in your projects.</p>';
      cardPicker._tasks = items;
    }
  }
  cardPicker.addEventListener('click', e => {
    if (e.target.closest('[data-close]')) { cardPicker.hidden = true; return; }
    const tab = e.target.closest('[data-tab]'); if (tab) { cardTab = tab.dataset.tab; paintCardPicker(); return; }
    const r = rect(), c = toWorld(r.width / 2, r.height / 2);
    const pg = e.target.closest('[data-page]');
    if (pg) { const p = (opts.pages?.all?.() || []).find(x => x.id === pg.dataset.page); if (p) addPageCard(p, c); cardPicker.hidden = true; return; }
    const tk = e.target.closest('[data-task]');
    if (tk) { const t = (cardPicker._tasks || []).find(x => x.id === tk.dataset.task); if (t) addTaskCards([t], { x: c.x - 130, y: c.y - 52 }); cardPicker.hidden = true; }
  });
  const unFocus = opts.comments?.onFocus?.(id => {
    const t = threads.find(x => x.id === id); const a = t && anchorOf(t); if (!a) return;
    const p = pinPos(a), r = rect(); view.x = r.width / 2 - p.x * view.z; view.y = r.height / 2 - p.y * view.z; render();
    requestAnimationFrame(() => pinLayer.querySelector(`[data-thread="${id}"]`)?.classList.add('wb-pin-focus'));
  });
  function addPageCard(page, at) { add([create('noteCard', { x: at.x - 130, y: at.y - 40, w: 260, h: 80, pageId: page.id, title: page.title, emoji: page.emoji })]); }

  // ── Export ───────────────────────────────────────────────────────────────
  const exportPanel = el('div', 'wb-panel wb-exportp'); exportPanel.hidden = true;
  root.append(exportPanel);
  exportPanel.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleExport() {
    if (!exportPanel.hidden) { exportPanel.hidden = true; return; }
    const frames = framesInOrder();
    exportPanel.innerHTML = `<div class="wb-ph"><b>Export</b><button type="button" data-close aria-label="Close">✕</button></div>
      <label class="wb-row">What <select data-what><option value="all">Whole board</option>${sel.size ? '<option value="sel" selected>Selection</option>' : ''}${frames.map(f => `<option value="f:${f.id}">Frame: ${esc(f.name || 'Frame')}</option>`).join('')}</select></label>
      <label class="wb-row">Format <select data-fmt><option value="png">PNG picture</option><option value="svg">SVG (sharp at any size)</option><option value="pdf">PDF</option></select></label>
      <label class="wb-row">Background <select data-bg><option value="light">White</option><option value="dark">Dark</option><option value="none">None (PNG / SVG)</option></select></label>
      <div class="wb-row2"><button type="button" class="wb-wide" data-copy>Copy as image</button><button type="button" class="wb-wide wb-pri" data-dl>Download</button></div>`;
    framesPanel.hidden = true; votePanel.hidden = true; timerPanel.hidden = true; libPanel.hidden = true; gallery.hidden = true;
    exportPanel.hidden = false;
  }
  function exportSet(what) {
    if (what === 'sel') return [...sel].map(get).filter(Boolean);
    if (what?.startsWith('f:')) { const f = get(what.slice(2)); if (!f) return []; return [f, ...live().filter(x => x.id !== f.id && frameOf(x, live(), get)?.id === f.id), ...live().filter(c => c.type === 'connector')].filter((x, i, a) => a.indexOf(x) === i && (x.type !== 'connector' || frameOf(get(x.from?.id) || x, live(), get)?.id === f.id)); }
    return live();
  }
  async function renderExport(els, fmt, bg) {
    const files = await dataUrls();
    const theme = bg === 'dark' ? 'dark' : 'light', background = bg !== 'none' || fmt === 'pdf';
    if (fmt === 'svg') { const o = toSvg(els, { theme, files, background, cardInfo: opts.cardInfo }); return o ? new Blob([new XMLSerializer().serializeToString(o.svg)], { type: 'image/svg+xml' }) : null; }
    const png = await toPng(els, { scale: 2, theme, files, background, cardInfo: opts.cardInfo });
    if (fmt !== 'pdf' || !png) return png;
    return pngToPdf(png);
  }
  exportPanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { exportPanel.hidden = true; return; }
    const what = exportPanel.querySelector('[data-what]')?.value, fmt = exportPanel.querySelector('[data-fmt]')?.value, bg = exportPanel.querySelector('[data-bg]')?.value;
    const els = exportSet(what);
    if (!els.length) { toast('Nothing to export'); return; }
    if (e.target.closest('[data-copy]')) {
      try {
        const png = await toPng(els, { scale: 2, theme: bg === 'dark' ? 'dark' : 'light', files: await dataUrls(), background: bg !== 'none', cardInfo: opts.cardInfo });
        await navigator.clipboard.write([new ClipboardItem({ 'image/png': png })]);
        toast('Copied. Paste it in Chat or Mail.');
      } catch { toast('This browser cannot copy pictures. Use Download instead.'); }
      return;
    }
    if (e.target.closest('[data-dl]')) {
      const blob = await renderExport(els, fmt, bg).catch(() => null);
      if (!blob) { toast('Couldn’t make the file'); return; }
      const name = `${(opts.title?.() || 'whiteboard').replace(/[\\/:*?"<>|]+/g, ' ').trim() || 'whiteboard'}.${fmt}`;
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 4000);
      exportPanel.hidden = true;
    }
  });

  // ── Minimap ──────────────────────────────────────────────────────────────
  const mini = el('div', 'wb-mini'); mini.setAttribute('aria-label', 'Map of the board');
  const miniSvg = svgEl('svg', { class: 'wb-minisvg' }, mini);
  root.append(mini);
  mini.addEventListener('pointerdown', e => {
    e.stopPropagation();
    const b = unionBounds(live(), get); if (!b) return;
    const r = mini.getBoundingClientRect(), vb = miniSvg.viewBox.baseVal;
    const wx = vb.x + (e.clientX - r.left) / r.width * vb.width, wy = vb.y + (e.clientY - r.top) / r.height * vb.height;
    const rr = rect(); view.x = rr.width / 2 - wx * view.z; view.y = rr.height / 2 - wy * view.z; render();
  });
  function paintMini() {
    if (mode !== 'board') { mini.hidden = true; return; }
    const els = live();
    const b = unionBounds(els, get);
    const r = rect();
    // only worth showing once the board is bigger than the screen
    if (!b || (b.w * view.z < r.width * 0.9 && b.h * view.z < r.height * 0.9)) { mini.hidden = true; return; }
    mini.hidden = false;
    const pad = Math.max(b.w, b.h) * 0.06;
    miniSvg.setAttribute('viewBox', `${b.x - pad} ${b.y - pad} ${b.w + pad * 2} ${b.h + pad * 2}`);
    const pal = PALETTE[theme] || PALETTE.light, stroke = Math.max(b.w, b.h) / 120;
    let html = '';
    for (const e of els) {
      if (e.type === 'connector' || e.type === 'path' || e.type === 'stamp') continue;
      const eb = bounds(e, get);
      const fill = e.type === 'sticky' ? pal.sticky[e.color] || pal.sticky.yellow : e.type === 'frame' ? 'none' : 'rgba(118,118,128,.35)';
      html += `<rect x="${eb.x}" y="${eb.y}" width="${Math.max(eb.w, stroke * 2)}" height="${Math.max(eb.h, stroke * 2)}" rx="${stroke * 2}" fill="${fill}" stroke="${e.type === 'frame' ? 'rgba(118,118,128,.6)' : 'none'}" stroke-width="${stroke}"/>`;
    }
    const a = toWorld(0, 0), z = toWorld(r.width, r.height);
    html += `<rect x="${a.x}" y="${a.y}" width="${z.x - a.x}" height="${z.y - a.y}" fill="rgba(10,132,255,.08)" stroke="${pal.sel}" stroke-width="${stroke * 1.5}" rx="${stroke * 3}"/>`;
    miniSvg.innerHTML = html;
  }

  // ── Pictures and files ───────────────────────────────────────────────────
  // A picture or file is uploaded through the page (stored on the NAS with the page's access); the board keeps only its id.
  const files = new Map();     // fileId -> object URL, for drawing
  opts.files = files;
  const loading = new Set();
  function ensureFile(id) {
    if (!id || files.has(id) || loading.has(id) || !opts.loadFile) return;
    loading.add(id);
    opts.loadFile(id).then(url => { files.set(id, url); cache.clear(); world.innerHTML = ''; render(); }, () => {}).finally(() => loading.delete(id));
  }
  const IMAGE_TYPES = /^image\/(png|jpe?g|webp|gif)$/i;
  const MAX_UPLOAD = 10 * 1024 * 1024;
  function pickFiles() {
    if (readOnly || !opts.upload) return;
    const input = document.createElement('input');
    input.type = 'file'; input.multiple = true;
    input.onchange = () => { const r = rect(); dropFiles([...input.files], toWorld(r.width / 2, r.height / 2)); };
    input.click();
  }
  async function dropFiles(list, at) {
    if (readOnly || !opts.upload || !list.length) return;
    let x = at.x, placed = 0;
    for (const f of list) {
      if (f.size > MAX_UPLOAD && IMAGE_TYPES.test(f.type)) { toast(`${f.name} is larger than 10 MB`); continue; }
      const imgCount = live().filter(e => e.type === 'image').length;
      if (IMAGE_TYPES.test(f.type) && imgCount >= 200) { toast('This board already has 200 pictures'); break; }
      try {
        toast(`Uploading ${f.name}…`);
        const up = await opts.upload(f);
        if (IMAGE_TYPES.test(f.type)) {
          const size = await imageSize(f).catch(() => ({ w: 400, h: 300 }));
          const scale = Math.min(1, 480 / size.w, 360 / size.h);
          const w = Math.max(40, size.w * scale), h = Math.max(40, size.h * scale);
          files.set(up.id, URL.createObjectURL(f));
          add([create('image', { x: x - w / 2, y: at.y - h / 2, w, h, fileId: up.id, name: f.name })], { select: list.length === 1 });
          x += w + 24;
        } else {
          add([create('file', { x: x - 120, y: at.y - 32, w: 240, h: 64, fileId: up.id, name: f.name, size: f.size })], { select: list.length === 1 });
          x += 264;
        }
        placed++;
      } catch (err) { toast(err?.message || `Couldn’t upload ${f.name}`); }
    }
    if (placed) toast(placed === 1 ? 'Added to the board' : `Added ${placed} files`);
  }
  function imageSize(file) {
    return new Promise((ok, fail) => { const img = new Image(), u = URL.createObjectURL(file); img.onload = () => { ok({ w: img.naturalWidth, h: img.naturalHeight }); URL.revokeObjectURL(u); }; img.onerror = () => { URL.revokeObjectURL(u); fail(new Error('not an image')); }; img.src = u; });
  }
  root.addEventListener('dragover', e => { if (!readOnly && opts.upload && [...(e.dataTransfer?.types || [])].includes('Files')) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; root.classList.add('wb-dropping'); } });
  root.addEventListener('dragleave', e => { if (e.target === root) root.classList.remove('wb-dropping'); });
  root.addEventListener('drop', e => {
    root.classList.remove('wb-dropping');
    if (readOnly || !opts.upload || !e.dataTransfer?.files?.length) return;
    e.preventDefault(); e.stopPropagation();
    const { sx, sy } = local(e);
    dropFiles([...e.dataTransfer.files], toWorld(sx, sy));
  });
  root.addEventListener('paste', e => {
    if (readOnly || editing) return;
    const fl = [...(e.clipboardData?.files || [])];
    if (fl.length && opts.upload) { e.preventDefault(); const r = rect(); dropFiles(fl, toWorld(r.width / 2, r.height / 2)); return; }
    const text = e.clipboardData?.getData('text/plain');
    if (text && /^\{"ipopBoard":1/.test(text)) { try { clipboard = JSON.parse(text).elements; e.preventDefault(); pasteClip(); } catch { /* not ours */ } }
  });

  // ── Library (My library + team library, saved on the server) ────────────
  let libItems = null, libTab = 'mine', libBusy = false;
  const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  /// Elements moved so the group starts at 0,0; connectors keep only links to shapes that come along.
  function packForLibrary(els) {
    const ids = new Set(els.map(e => e.id));
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && ids.has(c.from?.id) && ids.has(c.to?.id));
    const all2 = [...els, ...extra].filter(e => e.type !== 'image');
    const b = unionBounds(all2, get) || { x: 0, y: 0 };
    return all2.map(e => {
      const c = structuredClone(e);
      delete c.locked; delete c.frameId;
      if (c.type === 'connector') {
        const end = x => (x?.id && ids.has(x.id) ? { id: x.id } : { x: (x?.x ?? 0) - b.x, y: (x?.y ?? 0) - b.y });
        return { ...c, from: end(c.from), to: end(c.to) };
      }
      return { ...c, x: c.x - b.x, y: c.y - b.y };
    });
  }
  async function saveToLibrary(els) {
    if (!opts.library || !els.length) return;
    const packed = packForLibrary(els);
    if (!packed.length) { toast('Pictures cannot go in the library'); return; }
    const first = packed.find(e => e.text || e.name);
    const name = (first?.text || first?.name || '').split('\n')[0].slice(0, 60) || (packed.length === 1 ? (SHAPE_LABEL[packed[0].type] || packed[0].type) : `${packed.length} items`);
    try {
      await opts.library.save({ name, json: JSON.stringify(packed) });
      libItems = null;
      toast(`Added to your library as "${name}"`);
      if (!libPanel.hidden) paintLibrary();
    } catch (err) { toast(err?.message === 'library_full' ? 'Your library is full (200 items). Remove some first.' : 'Couldn’t save to the library'); }
  }
  async function loadLibrary(force = false) {
    if (!opts.library) return [];
    if (libItems && !force) return libItems;
    libItems = await opts.library.list().catch(() => null);
    return libItems || [];
  }
  function libThumb(item) {
    let els; try { els = JSON.parse(item.json); } catch { return ''; }
    const o = toSvg(els.filter(isElement), { theme, background: false, padding: 8 });
    if (!o) return '';
    o.svg.setAttribute('width', '100%'); o.svg.setAttribute('height', '100%'); o.svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
    return new XMLSerializer().serializeToString(o.svg);
  }
  async function paintLibrary() {
    libPanel.innerHTML = `<div class="wb-ph"><b>Library</b><button type="button" data-close aria-label="Close">✕</button></div><div class="wb-note">Loading…</div>`;
    const items = await loadLibrary();
    if (libPanel.hidden) return;
    const shown = items.filter(i => (libTab === 'mine' ? i.mine : i.shared && !i.mine));
    libPanel.innerHTML = `<div class="wb-ph"><b>Library</b><button type="button" data-close aria-label="Close">✕</button></div>
      <div class="wb-seg" role="tablist"><button type="button" role="tab" data-tab="mine" aria-selected="${libTab === 'mine'}" class="${libTab === 'mine' ? 'on' : ''}">My library</button><button type="button" role="tab" data-tab="team" aria-selected="${libTab === 'team'}" class="${libTab === 'team' ? 'on' : ''}">Team library</button></div>
      ${shown.length ? `<div class="wb-libgrid">${shown.map(i => `<div class="wb-libitem" data-id="${i.id}">
          <button type="button" class="wb-libuse" data-use="${i.id}" title="Put it on the board"><span class="wb-libthumb">${libThumb(i)}</span><span class="wb-libname">${esc(i.name)}</span><small>${i.mine ? (i.shared ? 'Shared with the team' : 'Only you') : esc(i.ownerName)}</small></button>
          ${i.mine ? `<span class="wb-libacts"><button type="button" data-share="${i.id}" title="${i.shared ? 'Stop sharing' : 'Share with the team'}">${i.shared ? 'Unshare' : 'Share'}</button><button type="button" data-del="${i.id}" title="Remove" aria-label="Remove ${esc(i.name)}">✕</button></span>` : ''}
        </div>`).join('')}</div>`
        : `<p class="wb-note">${libTab === 'mine' ? 'Select things on the board and press <b>Add to my library</b> in their menu to keep them for later.' : 'Nothing shared yet. Share an item from your library and everyone can use it.'}</p>`}`;
  }
  function toggleLibrary() {
    if (!libPanel.hidden) { libPanel.hidden = true; return; }
    gallery.hidden = true;
    libPanel.hidden = false;
    paintLibrary();
  }
  libPanel.addEventListener('click', async e => {
    if (e.target.closest('[data-close]')) { libPanel.hidden = true; return; }
    const tab = e.target.closest('[data-tab]');
    if (tab) { libTab = tab.dataset.tab; paintLibrary(); return; }
    const items = libItems || [];
    const use = e.target.closest('[data-use]');
    if (use) {
      const item = items.find(i => i.id === use.dataset.use); if (!item || readOnly) return;
      let els; try { els = JSON.parse(item.json).filter(isElement); } catch { return; }
      const r = rect(), c = toWorld(r.width / 2, r.height / 2);
      const b = unionBounds(els, id => els.find(x => x.id === id)) || { w: 0, h: 0 };
      const map = new Map(els.map(x => [x.id, create(x.type).id]));
      const out = els.map(x => {
        const copy = { ...structuredClone(x), id: map.get(x.id), version: 1 };
        if (x.type === 'connector') { const end = y => (y?.id && map.has(y.id) ? { id: map.get(y.id) } : { x: (y?.x ?? 0) + c.x - b.w / 2, y: (y?.y ?? 0) + c.y - b.h / 2 }); return { ...copy, from: end(x.from), to: end(x.to) }; }
        return { ...copy, x: x.x + c.x - b.w / 2, y: x.y + c.y - b.h / 2 };
      });
      add(out);
      return;
    }
    const share = e.target.closest('[data-share]');
    if (share && !libBusy) {
      const item = items.find(i => i.id === share.dataset.share); if (!item) return;
      libBusy = true;
      try { await opts.library.update(item.id, { shared: !item.shared }); libItems = null; toast(item.shared ? 'Only you can see it now' : 'Shared with the team library'); paintLibrary(); }
      catch { toast('Couldn’t change it'); } finally { libBusy = false; }
      return;
    }
    const del = e.target.closest('[data-del]');
    if (del && !libBusy) {
      libBusy = true;
      try { await opts.library.remove(del.dataset.del); libItems = null; paintLibrary(); }
      catch { toast('Couldn’t remove it'); } finally { libBusy = false; }
    }
  });

  // ── Zoom ─────────────────────────────────────────────────────────────────
  // The tool bar is centred and the zoom bar sits bottom right: when the board is too narrow for both on one line, the zoom bar
  // moves up above the tool bar instead of covering its last buttons.
  function layoutBars() {
    const w = root.clientWidth;
    root.classList.toggle('wb-narrow', w > 0 && (w - toolbar.offsetWidth) / 2 < zoomBar.offsetWidth + 24);
  }
  function paintZoom() {
    zoomBar.innerHTML = `<button type="button" data-z="out" aria-label="Zoom out" title="Zoom out (Ctrl −)">−</button><button type="button" data-z="pct" class="wb-pct" title="Zoom to 100%">${Math.round(view.z * 100)}%</button><button type="button" data-z="in" aria-label="Zoom in" title="Zoom in (Ctrl +)">+</button><button type="button" data-z="fit" title="Fit everything (Shift 1)">Fit</button>`
      + (readOnly ? '' : `<span class="wb-sep"></span><button type="button" data-z="undo" aria-label="Undo" title="Undo (Ctrl Z)" ${undo.undoStack.length ? '' : 'disabled'}>${icon('undo')}</button><button type="button" data-z="redo" aria-label="Redo" title="Redo (Ctrl Y)" ${undo.redoStack.length ? '' : 'disabled'}>${icon('redo')}</button>`)
      + (compact ? '' : `<span class="wb-sep"></span><button type="button" data-z="keys" aria-label="Keyboard shortcuts" title="Keyboard shortcuts (?)">?</button>`);
    layoutBars();
  }
  zoomBar.addEventListener('pointerdown', e => e.stopPropagation());
  zoomBar.addEventListener('click', e => {
    const b = e.target.closest('[data-z]'); if (!b) return;
    const z = b.dataset.z;
    if (z === 'in') zoomAt(1.2); else if (z === 'out') zoomAt(1 / 1.2); else if (z === 'pct') zoomAt(1 / view.z); else if (z === 'fit') fit();
    else if (z === 'undo') doUndo(); else if (z === 'redo') doRedo(); else if (z === 'keys') toggleKeys();
  });
  function zoomAt(f, sx, sy) {
    const r = rect(); sx ??= r.width / 2; sy ??= r.height / 2;
    const w = toWorld(sx, sy);
    view.z = Math.min(4, Math.max(0.1, view.z * f));
    view.x = sx - w.x * view.z; view.y = sy - w.y * view.z;
    render();
  }
  function fit(els = live(), { pad = 60, max = 1.4 } = {}) {
    const r = rect();
    if (r.width < 50 || r.height < 50) return;
    const b = unionBounds(els, get);
    if (!b) { view = { x: r.width / 2 - 200, y: r.height / 2 - 150, z: 1 }; render(); return; }
    const top = 30, bottom = readOnly ? 20 : 70;
    const z = Math.max(0.1, Math.min(max, (r.width - pad * 2) / Math.max(b.w, 1), (r.height - top - bottom - pad) / Math.max(b.h + 26, 1)));
    view = { z, x: (r.width - b.w * z) / 2 - b.x * z, y: top + (r.height - top - bottom - b.h * z) / 2 - b.y * z + 13 * z };
    render();
  }

  // ── Selection quick menu ─────────────────────────────────────────────────
  function paintProps() {
    const els = [...sel].map(get).filter(Boolean);
    widthMenu.hidden = true;
    if (!els.length || readOnly || interaction || editing) { props.hidden = true; if (colourFor === 'sel') colourPop.hidden = true; return; }
    const types = new Set(els.map(e => e.type));
    const only = t => types.size === 1 && types.has(t);
    const anyShape = els.some(e => SHAPES.includes(e.type));
    const parts = [];
    if (only('sticky')) parts.push(STICKY_COLORS.map(c => `<button type="button" data-sticky="${c}" title="${c[0].toUpperCase() + c.slice(1)}" aria-label="${c} sticky"><span class="wb-sw" style="background:${pal().sticky[c]}"></span></button>`).join(''));
    else if (els.some(e => !['frame', 'image', 'sticker', 'file', 'taskCard', 'noteCard', 'link'].includes(e.type))) {
      const first = els.find(e => !['frame', 'image', 'sticker', 'sticky'].includes(e.type));
      parts.push(`<button type="button" data-colour="stroke" title="${anyShape ? 'Line colour' : 'Colour'}" aria-label="${anyShape ? 'Line colour' : 'Colour'}"><span class="wb-sw wb-ring2" style="--c:${colourCss(first?.stroke)}"></span></button>`);
      if (anyShape) { const f = els.find(e => SHAPES.includes(e.type)).fill; parts.push(`<button type="button" data-colour="fill" title="Fill colour" aria-label="Fill colour"><span class="wb-sw ${f === 'none' ? 'wb-fillsw' : ''}" style="background:${f === 'none' ? 'transparent' : (f || pal().shapeFill)}"></span></button>`); }
    }
    if (els.some(e => e.type === 'connector')) parts.push(['straight', 'elbow', 'curved'].map(r => `<button type="button" data-route="${r}" title="${r[0].toUpperCase() + r.slice(1)}">${r === 'straight' ? '／' : r === 'elbow' ? '⌐' : '∿'}</button>`).join('') + `<button type="button" data-heads title="Arrow ends">⇄</button><button type="button" data-dash title="Dashed">┄</button>`);
    if (els.some(e => e.type === 'path' || e.type === 'connector' || anyShape)) {
      const p = els.find(e => e.type === 'path'), cur = p ? Math.round((p.width || 3) * view.z) : (els.find(e => e.strokeWidth)?.strokeWidth || 2);
      parts.push(`<button type="button" data-width title="Line weight · ${cur} px" aria-label="Line weight"><span class="wb-wline" style="height:${Math.max(1.5, Math.min(8, cur / (p ? 2 : 1)))}px"></span></button>`);
    }
    if (els.some(e => e.type === 'text' || e.type === 'sticky' || anyShape)) parts.push(['ipop', 'hand', 'mono'].map(f => `<button type="button" data-font="${f}" title="${f === 'ipop' ? 'iPop font' : f === 'hand' ? 'Handwriting' : 'Monospace'}" style="font-family:${FONT_STACK[f].replace(/"/g, "'")}">Aa</button>`).join('') + SIZES.map(([n, s]) => `<button type="button" data-size="${s}" title="Text size ${n}">${n}</button>`).join(''));
    const locked = els.every(e => e.locked);
    if (opts.tasks?.create && els.every(x => x.type === 'sticky')) parts.push(`<button type="button" data-act="task" class="wb-taskbtn" title="Turn into Projects tasks">${icon('task')}<span>Make ${els.length > 1 ? els.length + ' tasks' : 'task'}</span></button>`);
    if (els.length === 1 && (SHAPES.includes(els[0].type) || els[0].type === 'sticky')) parts.push(`<button type="button" data-act="branch" title="Add a branch (Tab)">${icon('mind')}</button>`);
    if (opts.library) parts.push(`<button type="button" data-act="lib" title="Add to my library">${icon('bookplus')}</button>`);
    parts.push(`<button type="button" data-act="front" title="Bring to front (Ctrl ])">${icon('front')}</button><button type="button" data-act="back" title="Send to back (Ctrl [)">${icon('back')}</button><button type="button" data-act="dup" title="Duplicate (Ctrl D)">${icon('copy')}</button><button type="button" data-act="lock" title="${locked ? 'Unlock' : 'Lock'}">${icon(locked ? 'unlock' : 'lock')}</button><button type="button" data-act="del" title="Delete (Del)" class="wb-danger">${icon('trash')}</button>`);
    props.innerHTML = parts.join('<span class="wb-sep"></span>');
    props.hidden = false;
    positionProps();
  }
  const widthMenu = el('div', 'wb-menu wb-widthmenu'); widthMenu.hidden = true;
  root.append(widthMenu);
  widthMenu.addEventListener('pointerdown', e => e.stopPropagation());
  function toggleWidthMenu(anchor, els) {
    if (!widthMenu.hidden) { widthMenu.hidden = true; return; }
    const strokes = els.some(x => x.type === 'path');
    const opts2 = strokes ? [2, 4, 8, 14, 24] : [1, 2, 4, 6, 8];
    widthMenu.innerHTML = opts2.map(v => `<button type="button" data-w="${v}" title="${v} px" aria-label="${v} px"><span class="wb-wline" style="height:${Math.max(1, Math.min(10, v / (strokes ? 2.2 : 1)))}px"></span></button>`).join('');
    widthMenu.dataset.kind = strokes ? 'path' : 'shape';
    widthMenu.hidden = false;
    const r = anchor.getBoundingClientRect(), rr = rect();
    widthMenu.style.left = `${Math.max(8, Math.min(r.left - rr.left - 8, rr.width - widthMenu.offsetWidth - 8))}px`;
    widthMenu.style.top = `${r.bottom - rr.top + 6}px`;
  }
  widthMenu.addEventListener('click', e => {
    const b = e.target.closest('[data-w]'); if (!b) return;
    const v = +b.dataset.w; widthMenu.hidden = true;
    applyToSelection(x => x.type === 'path' ? { width: +(v / view.z).toFixed(2) } : (x.type === 'connector' || SHAPES.includes(x.type)) ? { strokeWidth: v } : null);
    paintProps();
  });
  function positionProps() {
    if (props.hidden) return;
    const b = unionBounds([...sel].map(get).filter(Boolean), get);
    if (!b) { props.hidden = true; return; }
    const a = toScreen(b.x, b.y), z = toScreen(b.x + b.w, b.y + b.h), r = rect();
    const w = props.offsetWidth, h = props.offsetHeight;
    let x = (a.x + z.x) / 2 - w / 2, y = a.y - h - 14;
    if (y < 8) y = z.y + 14;
    if (y + h > r.height - 64) y = Math.max(8, r.height - 64 - h);
    props.style.left = `${Math.max(8, Math.min(x, r.width - w - 8))}px`;
    props.style.top = `${y}px`;
  }
  props.addEventListener('pointerdown', e => e.stopPropagation());
  props.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    const els = [...sel].map(get).filter(Boolean).filter(x => !x.locked || b.dataset.act === 'lock');
    const upd = fn => write(els.map(x => { const c = fn(x); return c ? bump(x, c) : null; }).filter(Boolean), { now: true });
    if (b.dataset.sticky) upd(x => x.type === 'sticky' ? { color: b.dataset.sticky } : null);
    else if (b.dataset.colour) { colourTab = b.dataset.colour; if (!colourPop.hidden && colourFor === 'sel') { colourPop.hidden = true; return; } openColours('sel', b); return; }
    else if (b.dataset.route) upd(x => x.type === 'connector' ? { route: b.dataset.route } : null);
    else if ('heads' in b.dataset) upd(x => { if (x.type !== 'connector') return null; const s = x.startHead === 'arrow', en = (x.endHead ?? 'arrow') === 'arrow'; const next = en && !s ? [true, true] : en && s ? [false, false] : [false, true]; return { startHead: next[0] ? 'arrow' : 'none', endHead: next[1] ? 'arrow' : 'none' }; });
    else if ('dash' in b.dataset) upd(x => x.type === 'connector' ? { dashed: !x.dashed } : null);
    else if ('width' in b.dataset) { toggleWidthMenu(b, els); return; }
    else if (b.dataset.font) upd(x => (x.type === 'text' || x.type === 'sticky' || SHAPES.includes(x.type)) ? { font: b.dataset.font } : null);
    else if (b.dataset.size) upd(x => {
      if (x.type === 'text') { const s = +b.dataset.size, lines = String(x.text).split('\n'); return { size: s, w: Math.max(...lines.map(l => measure(l, s, x.font || 'ipop'))) + 4, h: lines.length * s * 1.3 }; }
      return (x.type === 'sticky' || SHAPES.includes(x.type)) ? { size: +b.dataset.size } : null;
    });
    else if (b.dataset.act === 'front') reorder(els, 'front');
    else if (b.dataset.act === 'back') reorder(els, 'back');
    else if (b.dataset.act === 'dup') duplicate(els);
    else if (b.dataset.act === 'lock') { const lock = !els.every(x => x.locked); upd(() => ({ locked: lock })); toast(lock ? 'Locked: it can no longer be moved by accident' : 'Unlocked'); }
    else if (b.dataset.act === 'del') remove(els);
    else if (b.dataset.act === 'lib') { saveToLibrary(els); return; }
    else if (b.dataset.act === 'task') { makeTasks(els); return; }
    else if (b.dataset.act === 'branch') { mindAdd(els[0], 'child'); return; }
    commitStop();
    paintProps();
  });

  // ── Editing operations ───────────────────────────────────────────────────
  function add(els, { select = true } = {}) {
    if (readOnly) return;
    if (count() + els.length > MAX_ELEMENTS) { toast(`This board is full (${MAX_ELEMENTS.toLocaleString()} things). Split it into another board.`); return; }
    let z = topZ(all());
    const placed = els.map(e => { z = zBetween(z || null, null); return { ...e, z }; });
    write(placed, { now: true });
    commitStop();
    if (select) { sel = new Set(placed.filter(e => e.type !== 'connector' || placed.length === 1).map(e => e.id)); paintProps(); }
    if (count() > MAX_ELEMENTS * 0.9) toast('This board is getting very large. Think about moving part of it to a new board.');
    return placed;
  }
  function remove(els) {
    if (!els.length) return;
    const ids = new Set(els.map(e => e.id));
    // connectors attached to something that goes keep a free end where it was
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && (ids.has(c.from?.id) || ids.has(c.to?.id))).map(c => {
      const fix = end => { if (!ids.has(end?.id)) return end; const t = get(end.id), p = t ? centre(t, get) : end; return { x: p.x, y: p.y }; };
      return bump(c, { from: fix(c.from), to: fix(c.to) });
    });
    write([...els.map(e => bump(e, { isDeleted: true })), ...extra], { now: true });
    sel.clear(); commitStop();
  }
  function duplicate(els) {
    const map = new Map();
    const copies = els.filter(e => e.type !== 'connector').map(e => { const c = { ...structuredClone(e), id: create(e.type).id, version: 1, locked: false }; map.set(e.id, c.id); return moved(c, 24, 24); });
    // connectors between duplicated shapes are duplicated too, attached to the copies
    for (const c of els.filter(e => e.type === 'connector')) {
      const end = x => (x?.id && map.has(x.id) ? { id: map.get(x.id) } : x?.id ? x : { x: x.x + 24, y: x.y + 24 });
      copies.push({ ...structuredClone(c), id: create('connector').id, version: 1, from: end(c.from), to: end(c.to) });
    }
    add(copies);
  }
  function reorder(els, where) {
    const others = live().filter(e => !sel.has(e.id));
    let z = where === 'front' ? topZ(others) : bottomZ(others);
    const sorted = [...els].sort((p, q) => (p.z < q.z ? -1 : 1));
    const out = [];
    if (where === 'front') for (const e of sorted) { z = zBetween(z || null, null); out.push(bump(e, { z })); }
    else for (const e of sorted.reverse()) { z = z ? zBetween(null, z) : 'a0'; out.unshift(bump(e, { z })); }
    write(out, { now: true });
  }
  function doUndo() { flush(); undo.undo(); sel.clear(); render(); paintZoom(); }
  function doRedo() { flush(); undo.redo(); sel.clear(); render(); paintZoom(); }
  undo.on('stack-item-added', () => paintZoom());
  undo.on('stack-item-popped', () => paintZoom());

  // ── Text editing ─────────────────────────────────────────────────────────
  function editText(target, { selectAll = false } = {}) {
    if (readOnly || target.locked) return;
    stopEdit();
    const ta = el('textarea', 'wb-edit');
    ta.setAttribute('aria-label', 'Text');
    ta.spellcheck = true;
    ta.value = target.text || '';
    const place = () => {
      const t = get(target.id); if (!t) return;
      const z = view.z, isText = t.type === 'text';
      const box = t.type === 'sticky' ? { x: t.x + 14, y: t.y + 12, w: t.w - 28, h: t.h - 24 } : isText ? { x: t.x, y: t.y, w: Math.max(t.w, 120), h: Math.max(t.h, (t.size || 20) * 1.3) } : { x: t.x + 10, y: t.y + 6, w: t.w - 20, h: t.h - 12 };
      const a = toScreen(box.x, box.y);
      const size = (t.size || (isText ? 20 : 16)) * z;
      Object.assign(ta.style, {
        left: `${a.x}px`, top: `${a.y}px`, width: `${Math.max(box.w * z, 60)}px`, height: `${Math.max(box.h * z, size * 1.5)}px`,
        fontSize: `${size}px`, lineHeight: '1.3', fontFamily: FONT_STACK[t.font || 'ipop'], fontWeight: isText ? 400 : 500,
        textAlign: t.type === 'sticky' || isText ? 'left' : 'center', color: t.type === 'sticky' ? pal().stickyInk : (t.stroke && isText ? t.stroke : pal().ink),
        paddingTop: t.type === 'sticky' || isText ? '0' : `${Math.max(0, (box.h * z - size * 1.3) / 2)}px`,
      });
    };
    root.append(ta);
    editing = { id: target.id, ta, place };
    place(); render();
    ta.focus();
    if (selectAll) ta.select(); else ta.setSelectionRange(ta.value.length, ta.value.length);
    const grow = () => {
      const t = get(target.id); if (!t) return;
      if (t.type === 'text') {
        const s = t.size || 20, lines = ta.value.split('\n');
        const w = Math.max(40, ...lines.map(l => measure(l, s, t.font || 'ipop') + 6)), h = lines.length * s * 1.3;
        draft.set(t.id, bump(t, { text: ta.value, w, h }));
        place();
      }
      if (t.type === 'sticky') {
        // stickies grow taller when the text needs it (ClickUp behaviour)
        const lt = layoutText(ta.value, { w: t.w - 28, h: 1e9 }, { size: t.size || 16, font: t.font || 'ipop', weight: 500, fit: false });
        const need = lt.lines.length * lt.lh + 24;
        if (need > t.h) { draft.set(t.id, bump(t, { h: Math.ceil(need) })); place(); render(); }
      }
    };
    ta.addEventListener('input', grow);
    ta.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'Escape' || (e.key === 'Enter' && (e.ctrlKey || e.metaKey))) { e.preventDefault(); stopEdit(); root.focus({ preventScroll: true }); }
    });
    ta.addEventListener('blur', () => setTimeout(() => { if (editing?.ta === ta) stopEdit(); }, 0));
  }
  /// Opens the text editor once the pointer that created the element is released. Opening it during pointerdown loses it:
  /// the browser then moves focus to the board (its default for a mouse press), which closes the editor at once.
  function editSoon(target) {
    const open = () => { root.removeEventListener('pointerup', open); root.removeEventListener('pointercancel', open); setTimeout(() => { if (!destroyed && get(target.id)) editText(get(target.id)); }, 0); };
    root.addEventListener('pointerup', open); root.addEventListener('pointercancel', open);
  }
  function stopEdit() {
    if (!editing) return;
    const { id, ta } = editing;
    editing = null;
    const t = get(id), text = ta.value.replace(/\s+$/, '');
    ta.remove();
    if (t) {
      if (t.type === 'text' && !text.trim()) write([bump(t, { isDeleted: true })], { now: true });
      else if (text !== (t.text || '') || draft.has(id)) write([bump(t, { text })], { now: true });
      commitStop();
    }
    render(); paintProps();
  }

  // ── Pointer input ────────────────────────────────────────────────────────
  let pinch = null, spaceDown = false;

  const tolerance = () => 6 / view.z;
  root.addEventListener('pointerdown', onDown);
  root.addEventListener('pointermove', onMove);
  root.addEventListener('pointerup', onUp);
  root.addEventListener('pointercancel', onUp);
  root.addEventListener('dblclick', onDouble);
  root.addEventListener('wheel', onWheel, { passive: false });
  root.addEventListener('dragstart', e => e.preventDefault());
  root.addEventListener('contextmenu', e => { if (e.target.closest('.wb-svg')) e.preventDefault(); });

  function onDown(e) {
    if (e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit,.wb-penpop,.wb-colourpop')) return;
    if (editing) stopEdit();
    shapeMenu.hidden = true; penPop.hidden = true; colourPop.hidden = true;
    // Inside a page the board sits in a draggable block: without this the browser starts a native drag (or a text selection)
    // on the first move and cancels the pointer, which cut every pen stroke off after a few pixels.
    e.preventDefault();
    root.focus({ preventScroll: true });
    pointers.set(e.pointerId, local(e));
    try { root.setPointerCapture(e.pointerId); } catch { /* not capturable */ }
    if (pointers.size === 2) {
      // two fingers: zoom and move, and cancel whatever one finger started
      cancelInteraction();
      const [p1, p2] = [...pointers.values()];
      pinch = { d: Math.hypot(p1.sx - p2.sx, p1.sy - p2.sy), cx: (p1.sx + p2.sx) / 2, cy: (p1.sy + p2.sy) / 2, view: { ...view } };
      return;
    }
    if (pointers.size > 2) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const touch = e.pointerType === 'touch';

    // handles of the current selection
    const h = e.target.closest?.('[data-handle]')?.dataset.handle, endKey = e.target.closest?.('[data-end]')?.dataset.end;
    if (h && !readOnly) { startResize(h, w, e.shiftKey); return; }
    if (endKey && !readOnly) { const c = get([...sel][0]); interaction = { kind: 'end', id: c.id, key: endKey }; return; }

    stopFollow();
    if (tool === 'laser' && e.button === 0) { interaction = { kind: 'laser' }; laserAt(w); return; }
    if (tool === 'comment' && e.button === 0) {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      startComment(w, hit && hit.type !== 'frame' ? hit : null);
      setTool('select');
      return;
    }
    if (votingOn() && e.button === 0 && !e.shiftKey) {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      if (hit && (hit.type === 'sticky' || hit.type === 'taskCard' || hit.type === 'noteCard')) { castVote(hit, e.altKey); return; }
    }
    if (tool === 'hand' || spaceDown || e.button === 1 || (readOnly && tool !== 'laser')) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; root.classList.add('wb-panning'); return; }
    if (e.button === 2) return;
    if (readOnly) return;
    if (tool === 'pen' || tool === 'highlighter') {
      // palm rejection: once a stylus has been used on this board, a resting hand (touch) does not draw
      if (touch && penSeen) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; return; }
      if (e.pointerType === 'pen') penSeen = true;
      const kind = penKind();
      const pressure = e.pointerType === 'pen' ? e.pressure || 0.5 : 0.5;
      const c = prefs.color[kind];
      const p = create('path', { x: w.x, y: w.y, points: [[0, 0, pressure]], stroke: isHex(c) ? c : null, width: +(prefs.size[kind] / view.z).toFixed(2), tool: kind,
        highlight: kind === 'highlighter' || undefined, simulate: e.pointerType !== 'pen' || undefined, opacity: prefs.opacity < 100 ? prefs.opacity / 100 : undefined });
      p.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'draw', el: p, pen: e.pointerType === 'pen', raw: [[w.x, w.y]], last: { x: w.x, y: w.y } };
      draft.set(p.id, p); render();
      armHold(sx, sy);
      return;
    }
    if (tool === 'eraser') {
      if (touch && penSeen) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; return; }
      interaction = { kind: 'erase', erased: new Set() }; eraseAt(w); return;
    }
    if (tool === 'stamp') { add([create('stamp', { x: w.x - 18, y: w.y - 18, w: 36, h: 36, emoji: stampEmoji })], { select: false }); return; }
    if (tool === 'sticky') { const s = create('sticky', { x: w.x - 100, y: w.y - 80, w: 200, h: 160, color: stickyColor, text: '' }); const [placed] = add([s]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (tool === 'text') { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: inkNow() }); const [placed] = add([t]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (SHAPES.includes(tool) || tool === 'frame') {
      const s = create(tool, tool === 'frame' ? { x: w.x, y: w.y, w: 1, h: 1, name: `Frame ${live().filter(x => x.type === 'frame').length + 1}`, order: live().filter(x => x.type === 'frame').length } : { x: w.x, y: w.y, w: 1, h: 1, text: '', stroke: inkNow() });
      s.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'create', el: s, ox: w.x, oy: w.y };
      draft.set(s.id, s); render();
      return;
    }
    if (tool === 'connector') {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      const from = hit && hit.type !== 'connector' && hit.type !== 'path' ? { id: hit.id } : { x: w.x, y: w.y };
      const c = create('connector', { from, to: { x: w.x, y: w.y }, route: 'straight', endHead: 'arrow', startHead: 'none', stroke: inkNow() });
      c.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'connect', el: c };
      draft.set(c.id, c); render();
      return;
    }
    // select tool
    const hit = hitTest(live(), w.x, w.y, get, tolerance());
    if (hit) {
      if (e.shiftKey) { sel.has(hit.id) ? sel.delete(hit.id) : sel.add(hit.id); paintProps(); render(); return; }
      if (!sel.has(hit.id)) sel = new Set([hit.id]);
      const movers = [...sel].map(get).filter(x => x && !x.locked);
      if (!movers.length) { paintProps(); render(); return; }
      // moving a frame carries what sits inside it
      const ids = new Set(movers.map(m => m.id));
      for (const f of movers.filter(m => m.type === 'frame')) for (const x of live()) if (!ids.has(x.id) && !x.locked && frameOf(x, live(), get)?.id === f.id) { ids.add(x.id); }
      const orig = [...ids].map(id => get(id));
      interaction = { kind: 'move', start: w, orig, moved: false, others: live().filter(x => !ids.has(x.id) && x.type !== 'connector' && x.type !== 'path').slice(0, 400).map(x => bounds(x, get)) };
      props.hidden = true; render();
      return;
    }
    sel.clear(); props.hidden = true;
    if (touch) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; root.classList.add('wb-panning'); render(); return; }
    interaction = { kind: 'marquee', ox: w.x, oy: w.y, marquee: { x: w.x, y: w.y, w: 0, h: 0 } };
    render();
  }

  function onMove(e) {
    if (pointers.has(e.pointerId)) pointers.set(e.pointerId, local(e));
    if (pinch && pointers.size >= 2) {
      const [p1, p2] = [...pointers.values()];
      const d = Math.hypot(p1.sx - p2.sx, p1.sy - p2.sy), cx = (p1.sx + p2.sx) / 2, cy = (p1.sy + p2.sy) / 2;
      const z = Math.min(4, Math.max(0.1, pinch.view.z * (d / (pinch.d || 1))));
      const wx = (pinch.cx - pinch.view.x) / pinch.view.z, wy = (pinch.cy - pinch.view.y) / pinch.view.z;
      view = { z, x: cx - wx * z, y: cy - wy * z };
      render(); return;
    }
    const it = interaction; if (!it) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    switch (it.kind) {
      case 'pan': view.x = it.view.x + sx - it.sx; view.y = it.view.y + sy - it.sy; render(); break;
      case 'draw': {
        if (it.snapped) break;            // became a neat shape while held: the rest of this press draws nothing
        // Coalesced events give smooth lines at high pointer rates; some browsers (and synthetic events) return none.
        const co = e.getCoalescedEvents?.() || [];
        const evs = co.length ? co : [e];
        const pts = it.el.points.slice();
        // Smoothing (stabiliser): each point moves part of the way towards the pointer. A stylus gets a lighter touch so
        // handwriting keeps its shape; the raw points are kept for "hold to make it neat".
        const k = 1 - (prefs.smooth / 100) * (it.pen ? 0.5 : 0.8);
        let moved2 = 0;
        for (const ev of evs) {
          const l = local(ev), p = toWorld(l.sx, l.sy);
          it.raw.push([p.x, p.y]);
          const nx = it.last.x + (p.x - it.last.x) * k, ny = it.last.y + (p.y - it.last.y) * k;
          moved2 += Math.hypot(nx - it.last.x, ny - it.last.y) * view.z;
          it.last = { x: nx, y: ny };
          pts.push([+(nx - it.el.x).toFixed(2), +(ny - it.el.y).toFixed(2), it.pen ? ev.pressure || 0.5 : 0.5]);
        }
        it.el = { ...it.el, points: pts };
        draft.set(it.el.id, it.el); render();
        if (moved2 > 2) armHold(sx, sy);
        break;
      }
      case 'erase': eraseAt(w); break;
      case 'laser': laserAt(w); break;
      case 'create': {
        let x = Math.min(it.ox, w.x), y = Math.min(it.oy, w.y), ww = Math.abs(w.x - it.ox), hh = Math.abs(w.y - it.oy);
        if (e.shiftKey) { const s = Math.max(ww, hh); ww = hh = s; if (w.x < it.ox) x = it.ox - s; if (w.y < it.oy) y = it.oy - s; }
        it.el = { ...it.el, x, y, w: ww, h: hh }; draft.set(it.el.id, it.el); render();
        break;
      }
      case 'connect': {
        const hit = hitTest(live().filter(x => x.id !== it.el.id), w.x, w.y, get, tolerance());
        const target = hit && hit.type !== 'connector' && hit.type !== 'path' && hit.id !== it.el.from?.id ? hit : null;
        it.connectHover = target?.id; interaction.connectHover = target?.id;
        it.el = { ...it.el, to: target ? { id: target.id } : { x: w.x, y: w.y } };
        draft.set(it.el.id, it.el); render();
        break;
      }
      case 'end': {
        const c = get(it.id);
        const hit = hitTest(live().filter(x => x.id !== c.id), w.x, w.y, get, tolerance());
        const target = hit && hit.type !== 'connector' && hit.type !== 'path' ? hit : null;
        interaction.connectHover = target?.id;
        write([bump(c, { [it.key]: target ? { id: target.id } : { x: w.x, y: w.y } })]);
        break;
      }
      case 'marquee': {
        it.marquee = { x: Math.min(it.ox, w.x), y: Math.min(it.oy, w.y), w: Math.abs(w.x - it.ox), h: Math.abs(w.y - it.oy) };
        render(); break;
      }
      case 'move': {
        let dx = w.x - it.start.x, dy = w.y - it.start.y;
        if (!it.moved && Math.hypot(dx, dy) * view.z < 3) return;
        it.moved = true;
        const box = unionBounds(it.orig, get);
        it.guideLines = [];
        if (box && !e.altKey) {
          const g = guides({ x: box.x + dx, y: box.y + dy, w: box.w, h: box.h }, it.others, 5 / view.z);
          if (g.dx || g.lines.some(l => l.x1 === l.x2)) dx += g.dx; else dx = snap(box.x + dx) - box.x;
          if (g.dy || g.lines.some(l => l.y1 === l.y2)) dy += g.dy; else dy = snap(box.y + dy) - box.y;
          it.guideLines = g.lines;
        }
        interaction.guideLines = it.guideLines;
        write(it.orig.map(o => ({ ...moved(o, dx, dy), version: o.version + 1 })));
        break;
      }
      case 'resize': {
        const dx = w.x - it.start.x, dy = w.y - it.start.y;
        const nb = resizeBox(it.box, it.handle, dx, dy, { keepRatio: it.keepRatio || e.shiftKey });
        const sx2 = nb.w / (it.box.w || 1), sy2 = nb.h / (it.box.h || 1);
        write(it.orig.map(o => {
          if (o.type === 'connector') return null;
          if (o.type === 'path') {
            const pts = o.points.map(p => [p[0] * sx2, p[1] * sy2, p[2]]);
            return { ...o, x: nb.x + (o.x - it.box.x) * sx2, y: nb.y + (o.y - it.box.y) * sy2, points: pts, version: o.version + 1 };
          }
          const ob = bounds(o, get);
          const next = { x: nb.x + (ob.x - it.box.x) * sx2, y: nb.y + (ob.y - it.box.y) * sy2, w: Math.max(8, ob.w * sx2), h: Math.max(8, ob.h * sy2) };
          if (o.type === 'text') next.size = Math.max(8, Math.round((o.size || 20) * Math.min(sx2, sy2) * 10) / 10);
          return { ...o, ...next, version: o.version + 1 };
        }).filter(Boolean));
        break;
      }
    }
  }

  function onUp(e) {
    pointers.delete(e.pointerId);
    if (pinch) { if (pointers.size < 2) pinch = null; return; }
    const it = interaction; interaction = null;
    root.classList.remove('wb-panning');
    if (!it) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    switch (it.kind) {
      case 'draw': {
        clearHold();
        draft.delete(it.el.id);
        if (it.snapped) break;                       // already replaced by a neat shape while held
        // the end of a smoothed stroke catches up with the pointer, so the line ends where the pen lifted
        const endPt = it.raw.at(-1);
        if (endPt && it.el.points.length > 1) it.el = { ...it.el, points: [...it.el.points, [+(endPt[0] - it.el.x).toFixed(2), +(endPt[1] - it.el.y).toFixed(2), it.el.points.at(-1)[2]]] };
        if (it.el.points.length === 1) it.el.points.push([0.1, 0.1, it.el.points[0][2]]);
        add([it.el], { select: false });
        break;
      }
      case 'erase': commitStop(); break;
      case 'create': {
        draft.delete(it.el.id);
        let s = it.el;
        if (s.w < 6 && s.h < 6) s = { ...s, x: w.x - (s.type === 'frame' ? 240 : 80), y: w.y - (s.type === 'frame' ? 160 : 50), w: s.type === 'frame' ? 480 : 160, h: s.type === 'frame' ? 320 : 100 };
        const [placed] = add([s]) || [];
        setTool('select');
        if (placed && s.type !== 'frame') editText(placed);
        break;
      }
      case 'connect': {
        draft.delete(it.el.id);
        const c = it.el;
        const a = c.from?.id ? centre(get(c.from.id), get) : c.from, b = c.to?.id ? centre(get(c.to.id), get) : c.to;
        if (!a || !b || Math.hypot(a.x - b.x, a.y - b.y) < 8) { render(); break; }
        add([c]);
        setTool('select');
        break;
      }
      case 'end': commitStop(); break;
      case 'marquee': {
        const m = it.marquee;
        if (m.w * view.z > 4 || m.h * view.z > 4) sel = new Set(insideBox(live(), m, get).map(x => x.id));
        break;
      }
      case 'move': case 'resize': commitStop(); break;
    }
    render(); paintProps();
  }

  function cancelInteraction() {
    clearHold();
    const it = interaction; interaction = null;
    if (!it) return;
    if (it.el) draft.delete(it.el.id);
    if (it.kind === 'move' || it.kind === 'resize') { draft.clear(); }
    render();
  }

  function startResize(handle, w, shift) {
    const els = [...sel].map(get).filter(x => x && !x.locked);
    const box = unionBounds(els, get);
    if (!box) return;
    interaction = { kind: 'resize', handle, start: w, box, orig: els, keepRatio: shift || (els.length === 1 && els[0].type === 'image') || els.length > 1 };
    props.hidden = true;
  }

  function eraseAt(w) {
    const r = prefs.size.eraser / 2 / view.z;
    if (prefs.erMode === 'part') {
      // rub out only where the eraser passes: a stroke is split into the pieces that stay
      const out = [];
      for (const x of live()) {
        if (x.type !== 'path' || x.locked) continue;
        const runs = cutStroke(x, w.x, w.y, r);
        if (runs.length === 1 && runs[0] === x.points) continue;
        out.push(bump(x, { isDeleted: true }), ...strokePieces(x, runs).map(p => ({ ...p, z: x.z })));
      }
      if (out.length) write(out);
      return;
    }
    const victims = live().filter(x => (x.type === 'path' || x.type === 'connector') && !x.locked && !interaction?.erased?.has(x.id) && hitTest([x], w.x, w.y, get, r));
    if (!victims.length) return;
    victims.forEach(v => interaction?.erased?.add(v.id));
    write(victims.map(v => bump(v, { isDeleted: true })));
  }

  function onDouble(e) {
    if (readOnly || e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit,.wb-penpop,.wb-colourpop')) return;
    const { sx, sy } = local(e), w = toWorld(sx, sy);
    const hit = hitTest(live(), w.x, w.y, get, tolerance());
    if (hit?.type === 'frame') {
      const name = hit.name || 'Frame';
      const ta = el('input', 'wb-edit wb-frame-name'); ta.value = name; ta.setAttribute('aria-label', 'Frame name');
      const a = toScreen(hit.x, hit.y - 26);
      Object.assign(ta.style, { left: `${a.x}px`, top: `${a.y}px`, width: '220px', height: '24px', fontSize: '14px', fontWeight: '600' });
      root.append(ta); ta.focus(); ta.select();
      const done = () => { const v = ta.value.trim(); ta.remove(); if (v && v !== name) { write([bump(hit, { name: v.slice(0, 80) })], { now: true }); commitStop(); } };
      ta.addEventListener('blur', done); ta.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') ta.blur(); });
      return;
    }
    if (hit?.type === 'file' && hit.fileId && opts.openFile) { opts.openFile(hit.fileId, hit.name); return; }
    if (hit?.type === 'taskCard' && hit.taskId && opts.tasks?.open) { opts.tasks.open(hit.taskId); return; }
    if (hit?.type === 'noteCard' && hit.pageId && opts.pages?.open) { opts.pages.open(hit.pageId); return; }
    if (hit && (hit.type === 'sticky' || hit.type === 'text' || SHAPES.includes(hit.type))) { sel = new Set([hit.id]); editText(hit); return; }
    if (hit?.type === 'connector') {
      const label = el('input', 'wb-edit'); label.value = hit.label || ''; label.placeholder = 'Label'; label.setAttribute('aria-label', 'Connector label');
      Object.assign(label.style, { left: `${sx - 70}px`, top: `${sy - 14}px`, width: '140px', height: '28px', textAlign: 'center' });
      root.append(label); label.focus();
      const done = () => { const v = label.value.trim(); label.remove(); write([bump(hit, { label: v || undefined })], { now: true }); commitStop(); };
      label.addEventListener('blur', done); label.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') label.blur(); });
      return;
    }
    if (!hit) { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: inkNow() }); const [placed] = add([t]) || []; if (placed) editText(placed); }
  }

  function onWheel(e) {
    e.preventDefault();
    stopFollow();
    const { sx, sy } = local(e);
    if (e.ctrlKey || e.metaKey) zoomAt(Math.exp(-e.deltaY * (e.deltaMode ? 0.05 : 0.0018)), sx, sy);
    else { view.x -= e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX; view.y -= e.shiftKey && !e.deltaX ? 0 : e.deltaY; render(); }
    if (editing) editing.place();
  }

  // ── Hold to make it neat ─────────────────────────────────────────────────
  // Keep the pen still for a moment at the end of a stroke: a ring fills, then a rough circle, box, triangle or line becomes
  // a clean one (grill Q10). Never automatic; the toast offers Undo, which brings the hand-drawn stroke back.
  let holdTimer = 0, holdRing = null, neatUndo = null;
  function clearHold() { clearTimeout(holdTimer); holdTimer = 0; holdRing?.remove(); holdRing = null; }
  function armHold(sx, sy) {
    clearHold();
    const it = interaction;
    if (!prefs.snap || it?.kind !== 'draw' || it.snapped || it.el.tool === 'highlighter' || it.raw.length < 8) return;
    holdTimer = setTimeout(() => {
      if (interaction !== it) return;
      holdRing = el('div', 'wb-holdring'); holdRing.style.left = `${sx}px`; holdRing.style.top = `${sy}px`; root.append(holdRing);
      holdTimer = setTimeout(() => { clearHold(); if (interaction === it) snapStroke(it); }, 380);
    }, 260);
  }
  function snapStroke(it) {
    if (it.snapped) return;
    const shape = recognise(it.raw);
    const line = shape ? null : recogniseLine(it.raw);
    if (!shape && !line) return;
    it.snapped = true;
    draft.delete(it.el.id);
    const stroke = it.el.stroke;
    let made;
    if (shape) {
      const square = Math.abs(shape.w - shape.h) < Math.max(shape.w, shape.h) * 0.15;
      const s = square ? (shape.w + shape.h) / 2 : 0;
      const box = square ? { x: shape.x + shape.w / 2 - s / 2, y: shape.y + shape.h / 2 - s / 2, w: s, h: s } : { x: shape.x, y: shape.y, w: shape.w, h: shape.h };
      made = create(shape.type, { ...box, text: '', stroke, fill: 'none', strokeWidth: Math.max(1, Math.round(it.el.width * 0.6 * 10) / 10) });
      const names = { ellipse: square ? 'a circle' : 'an oval', rect: square ? 'a square' : 'a rectangle', triangle: 'a triangle' };
      showNeatToast(`Became ${names[shape.type]}`, it.el, made);
    } else {
      made = create('connector', { from: { x: line.x1, y: line.y1 }, to: { x: line.x2, y: line.y2 }, route: 'straight', endHead: 'none', startHead: 'none', stroke, strokeWidth: Math.max(1, Math.round(it.el.width * 0.6 * 10) / 10) });
      showNeatToast('Became a straight line', it.el, made);
    }
    add([made], { select: false });
  }
  function showNeatToast(text, raw, made) {
    neatUndo = { raw, made };
    neat.innerHTML = `<span>${text}</span><button type="button" data-n="undo">Undo</button>`;
    neat.hidden = false;
    neat.style.left = `calc(50% - ${neat.offsetWidth / 2}px)`; neat.style.top = '';
    clearTimeout(showNeatToast.t); showNeatToast.t = setTimeout(() => { neat.hidden = true; neatUndo = null; }, 5000);
  }
  neat.addEventListener('pointerdown', e => e.stopPropagation());
  neat.addEventListener('click', e => {
    if (!e.target.closest('[data-n="undo"]') || !neatUndo) return;
    neat.hidden = true;
    const { raw, made } = neatUndo; neatUndo = null;
    const cur = get(made.id);
    if (cur) write([bump(cur, { isDeleted: true })], { now: true });
    add([{ ...raw, id: create('path').id, version: 1 }], { select: false });
  });

  // ── Keyboard ─────────────────────────────────────────────────────────────
  root.addEventListener('keydown', e => {
    if (e.target !== root) return;
    const k = e.key.toLowerCase(), mod = e.ctrlKey || e.metaKey;
    if (e.key === ' ' && !spaceDown) { spaceDown = true; root.classList.add('wb-space'); e.preventDefault(); return; }
    if (mod && k === 'z') { e.preventDefault(); if (!readOnly) (e.shiftKey ? doRedo : doUndo)(); return; }
    if (mod && k === 'y') { e.preventDefault(); if (!readOnly) doRedo(); return; }
    if (mod && k === 'a') { e.preventDefault(); sel = new Set(live().filter(x => x.type !== 'frame').map(x => x.id)); paintProps(); render(); return; }
    if (mod && (k === '=' || k === '+')) { e.preventDefault(); zoomAt(1.2); return; }
    if (mod && k === '-') { e.preventDefault(); zoomAt(1 / 1.2); return; }
    if (mod && k === '0') { e.preventDefault(); zoomAt(1 / view.z); return; }
    if (e.shiftKey && k === '!') { fit(); return; }
    if (e.shiftKey && e.code === 'Digit1') { e.preventDefault(); fit(); return; }
    if (e.shiftKey && k === 'f' && !mod && mode === 'board' && opts.onFullscreen) { e.preventDefault(); toggleFullscreen(); return; }
    if (e.key === '?') { toggleKeys(); return; }
    if (presenting && (e.key === 'ArrowRight' || e.key === 'PageDown' || e.key === ' ')) { e.preventDefault(); goSlide(presenting.index + 1); return; }
    if (presenting && (e.key === 'ArrowLeft' || e.key === 'PageUp')) { e.preventDefault(); goSlide(presenting.index - 1); return; }
    if (e.key === 'Escape' && (!penPop.hidden || !colourPop.hidden)) { penPop.hidden = true; colourPop.hidden = true; return; }
    if (e.key === 'Escape') { if (presenting) { stopPresent(); return; } if (following != null) { followingPresenter = null; stopFollow(); return; } if (!keysPanel.hidden) { keysPanel.hidden = true; return; } if (!gallery.hidden) { gallery.hidden = true; return; } if (!libPanel.hidden) { libPanel.hidden = true; return; } if (fullscreen) { toggleFullscreen(); return; } sel.clear(); setTool('select'); paintProps(); render(); return; }
    if (readOnly) { if (k === 'h' || k === 'l') setTool(k === 'h' ? 'hand' : 'laser'); else if (k === 'c' && opts.comments?.canComment?.()) setTool('comment'); return; }
    const chosen = [...sel].map(get).filter(Boolean);
    if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); remove(chosen.filter(x => !x.locked)); paintProps(); return; }
    if (mod && k === 'd') { e.preventDefault(); duplicate(chosen); return; }
    if (mod && k === 'c') { if (chosen.length) { e.preventDefault(); copySel(chosen); } return; }
    if (mod && k === 'x') { if (chosen.length) { e.preventDefault(); copySel(chosen); remove(chosen.filter(x => !x.locked)); } return; }
    if (mod && k === 'v') return;   // handled by the paste event (files, pictures and copied board items)
    if (mod && e.key === ']') { e.preventDefault(); reorder(chosen, 'front'); commitStop(); return; }
    if (mod && e.key === '[') { e.preventDefault(); reorder(chosen, 'back'); commitStop(); return; }
    if (e.key === 'Tab' && chosen.length === 1 && (SHAPES.includes(chosen[0].type) || chosen[0].type === 'sticky')) { e.preventDefault(); mindAdd(chosen[0], 'child'); return; }
    if (e.key === 'Enter' && e.shiftKey && chosen.length === 1 && (SHAPES.includes(chosen[0].type) || chosen[0].type === 'sticky')) { e.preventDefault(); mindAdd(chosen[0], 'sibling'); return; }
    if (e.key === 'Enter' && chosen.length === 1 && (chosen[0].type === 'sticky' || chosen[0].type === 'text' || SHAPES.includes(chosen[0].type))) { e.preventDefault(); editText(chosen[0]); return; }
    if (e.key.startsWith('Arrow') && chosen.length) {
      e.preventDefault();
      const step = e.shiftKey ? 24 : 1, dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0, dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
      write(chosen.filter(x => !x.locked).map(x => moved(x, dx, dy))); commitStop(); return;
    }
    if (mod || e.altKey) return;
    if ((e.key === '[' || e.key === ']') && (tool === 'pen' || tool === 'highlighter' || tool === 'eraser')) {
      const key = tool === 'eraser' ? 'eraser' : penKind(), v = prefs.size[key], step = v >= 10 ? 2 : 1;
      setSize(key, v + (e.key === ']' ? step : -step)); toast(`${key === 'eraser' ? 'Eraser' : PEN_TYPES[key].label} size ${prefs.size[key]} px`); return;
    }
    if (k === 's' && tool === 'sticky') { stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length]; setTool('sticky'); return; }
    // R picks the rectangle; pressing it again while a shape tool is on walks through the other shapes (shown in the bar).
    if (k === 'r') { lastShape = SHAPES.includes(tool) ? SHAPES[(SHAPES.indexOf(tool) + 1) % SHAPES.length] : 'rect'; setTool(lastShape); return; }
    if (k === 'i') { if (opts.upload) pickFiles(); return; }
    if (k === 'n') { toggleCardPicker(); return; }
    if (k === 'm') { prefs.ptype = 'highlighter'; savePrefs(prefs); setTool('highlighter'); return; }
    if (k === 'p') { if (prefs.ptype === 'highlighter') { prefs.ptype = 'pen'; savePrefs(prefs); } setTool('pen'); return; }
    if (KEYS[k]) { if (SHAPES.includes(KEYS[k])) lastShape = KEYS[k]; setTool(KEYS[k]); return; }
    if (/^[0-9]$/.test(k)) setTool(DIGITS[k]);
  });
  root.addEventListener('keyup', e => { if (e.key === ' ') { spaceDown = false; root.classList.remove('wb-space'); } });

  function copySel(els) {
    const ids = new Set(els.map(x => x.id));
    // connectors between copied shapes come along
    const extra = live().filter(c => c.type === 'connector' && !ids.has(c.id) && ids.has(c.from?.id) && ids.has(c.to?.id));
    clipboard = structuredClone([...els, ...extra]);
    try { navigator.clipboard?.writeText(JSON.stringify({ ipopBoard: 1, elements: clipboard })).catch(() => {}); } catch { /* blocked */ }
    toast(`Copied ${els.length === 1 ? '1 thing' : `${els.length} things`}`);
  }
  function pasteClip() {
    if (!clipboard?.length) return;
    const b = unionBounds(clipboard, id => clipboard.find(x => x.id === id));
    const r = rect(), c = toWorld(r.width / 2, r.height / 2);
    const dx = b ? c.x - (b.x + b.w / 2) : 24, dy = b ? c.y - (b.y + b.h / 2) : 24;
    const map = new Map(clipboard.map(x => [x.id, create(x.type).id]));
    const out = clipboard.map(x => {
      const copy = { ...structuredClone(x), id: map.get(x.id), version: 1, locked: false };
      if (x.type === 'connector') { const end = y => (y?.id && map.has(y.id) ? { id: map.get(y.id) } : { x: (y?.x ?? 0) + dx, y: (y?.y ?? 0) + dy }); return { ...copy, from: end(x.from), to: end(x.to) }; }
      return { ...copy, x: x.x + dx, y: x.y + dy };
    });
    add(out);
  }

  function toggleKeys() {
    if (!keysPanel.hidden) { keysPanel.hidden = true; return; }
    const rows = [['V', 'Select'], ['H', 'Hand (or hold Space)'], ['P', 'Pen (click it again: pen, marker, pencil, size, colour)'], ['M', 'Highlighter'], ['E', 'Eraser (click it again: whole or part)'], ['[ / ]', 'Smaller / bigger pen or eraser'], ['S', 'Sticky note (again: next colour)'], ['T', 'Text'], ['R', 'Shape (again: next shape)'], ['O', 'Circle'], ['D', 'Diamond'], ['A', 'Connector'], ['F', 'Frame'], ['Shift F', 'Full screen'], ['L', 'Laser pointer'], ['→ / ←', 'Next / previous slide while presenting'],
      ['1–9, 0', 'The old drawing keys'], ['Enter', 'Edit the selected text'], ['Ctrl D', 'Duplicate'], ['Ctrl C / V', 'Copy / paste'], ['Ctrl Z / Y', 'Undo / redo'], ['Ctrl ] / [', 'Front / back'], ['Arrows', 'Nudge (Shift: 24 px)'], ['Ctrl + wheel', 'Zoom'], ['Shift 1', 'Fit everything'], ['Alt while dragging', 'No snapping']];
    keysPanel.innerHTML = `<div class="wb-ph"><b>Keyboard shortcuts</b><button type="button" data-close aria-label="Close">✕</button></div><div class="wb-kgrid">${rows.map(([k, d]) => `<div><kbd>${k}</kbd><span>${d}</span></div>`).join('')}</div>`;
    keysPanel.hidden = false;
  }
  keysPanel.addEventListener('pointerdown', e => e.stopPropagation());
  keysPanel.addEventListener('click', e => { if (e.target.closest('[data-close]')) keysPanel.hidden = true; });

  // ── Start ────────────────────────────────────────────────────────────────
  paintToolbar();
  paintCorners();
  root.dataset.tool = 'select';
  let fitted = false;
  let wasPhone = isPhone();
  const ro = new ResizeObserver(() => {
    if (isPhone() !== wasPhone) { wasPhone = isPhone(); paintToolbar(); }
    const r = rect();
    if (!fitted && r.width > 60 && r.height > 60) { fitted = true; fit(); }
    else render();
    if (editing) editing.place();
  });
  ro.observe(root);
  render();

  /// Pictures as data URLs (a PNG made from an SVG cannot read blob: URLs).
  async function dataUrls() {
    const out = new Map();
    for (const [id, url] of files) {
      try { const b = await (await fetch(url)).blob(); out.set(id, await new Promise(ok => { const r = new FileReader(); r.onload = () => ok(r.result); r.readAsDataURL(b); })); } catch { /* left out */ }
    }
    return out;
  }
  const api = {
    root,
    /// Adds a starter layout beside what is already on the board. Returns how many things were added.
    applyTemplate(name) {
      const make = CANVAS_TEMPLATES[name];
      if (!make || readOnly) return 0;
      const b = unionBounds(live(), get);
      const items = make({ x: b ? b.x + b.w + 120 : 0, y: b ? b.y : 0 });
      const placed = add(items) || [];
      fit(placed, { max: 1 });
      return placed.length;
    },
    setReadOnly(v) { readOnly = !!v; if (readOnly) { stopEdit(); sel.clear(); setTool('select'); gallery.hidden = true; } paintToolbar(); paintCorners(); paintZoom(); render(); paintProps(); },
    setTheme(t) { theme = t; cache.clear(); world.innerHTML = ''; root.classList.toggle('wb-dark', t === 'dark'); paintToolbar(); render(); },
    fit: () => fit(),
    focus: () => root.focus({ preventScroll: true }),
    /// PNG of the whole board (light background unless dark is asked for).
    png: async ({ scale = 2, dark = false } = {}) => toPng(live(), { scale, theme: dark ? 'dark' : 'light', files: await dataUrls(), cardInfo: opts.cardInfo }),
    svg: ({ dark = false } = {}) => { const o = toSvg(live(), { theme: dark ? 'dark' : 'light', files: opts.files }); return o ? new XMLSerializer().serializeToString(o.svg) : null; },
    count,
    flush,
    /// The page's comment threads (only those pinned to this board are drawn).
    setThreads(list) { threads = (list || []).map(t => ({ id: t.id, anchor: t.anchor, resolvedAt: t.resolvedAt, count: t.count || 1 })); paintPins(); },
    /// Put a Notes page or Projects tasks on the board, in the middle of the screen.
    addPageCard(page) { if (readOnly) return; const r = rect(); addPageCard(page, toWorld(r.width / 2, r.height / 2)); },
    addTaskCards(tasks) { if (readOnly || !tasks?.length) return; const r = rect(); const c = toWorld(r.width / 2, r.height / 2); addTaskCards(tasks, { x: c.x - 130, y: c.y - 52 }); },
    setFullscreen(v) { fullscreen = !!v; paintCorners(); requestAnimationFrame(() => fit()); },
    destroy() { unFocus?.(); unThreads?.(); vmap?.unobserve(onVote); clearInterval(liveTick); clearInterval(timerTick); clearTimeout(cursorTimer); clearTimeout(viewTimer); clearTimeout(laserFade); clearTimeout(showNeatToast.t); clearHold(); clearTimeout(flushTimer); aw?.off('change', onAw); if (aw) { const st = aw.getLocalState(); if (st?.wb?.board === boardKey) aw.setLocalStateField('wb', null); } for (const u of files.values()) { try { URL.revokeObjectURL(u); } catch { /* not ours */ } } document.removeEventListener('keydown', fsKeys, true); stopEdit(); flush(); destroyed = true; ymap.unobserve(onY); undo.destroy(); ro.disconnect(); cancelAnimationFrame(raf); root.remove(); },
  };
  return api;
}
