// iPop's own whiteboard editor. One instance per board or /draw block.
//
// Storage: the scene's Y.Map ("wb:<id>") is the single source of truth; every element is one key. The editor draws straight
// from it, so what everyone sees converges with Yjs and needs no reconciliation layer. While a pointer is down, changes go to
// a local draft that is written to the map at most every 60 ms (others see the move live); undo is a Y.UndoManager that only
// tracks this editor's own writes, so Ctrl Z never undoes somebody else's work.
import {
  create, bump, isElement, zBetween, topZ, bottomZ, bounds, unionBounds, hitTest, insideBox, frameOf, moved, resizeBox,
  snap, guides, recognise, centre, SHAPES, STICKY_COLORS,
} from './model.js';
import { svgEl, defs, drawElement, PALETTE, toPng, toSvg, measure, FONT_STACK, layoutText } from './render.js';
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
const icon = (name, cls = 'wb-i') => `<svg class="${cls}" viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ''}</svg>`;

const INK = ['#1D1D1F', '#FF3B30', '#FF9500', '#34A853', '#0A84FF', '#AF52DE'];
const FILLS = [null, '#FFF3A3', '#C9F2C7', '#CFE6FF', '#FFD3E1', '#E6DAFF', '#F2F2F4'];
const SIZES = [['S', 14], ['M', 18], ['L', 26], ['XL', 40]];

// Letter shortcuts like ClickUp; digits keep the old Excalidraw meaning (grill Q18).
const KEYS = { v: 'select', h: 'hand', p: 'pen', m: 'highlighter', e: 'eraser', s: 'sticky', t: 'text', r: 'rect', o: 'ellipse', d: 'diamond', a: 'connector', l: 'connector', f: 'frame' };
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
  root.append(toolbar, props, zoomBar, shapeMenu, keysPanel, neat, toastEl, topLeft, topRight, gallery, emptyGallery);
  container.append(root);

  // ── State ────────────────────────────────────────────────────────────────
  let view = { x: 40, y: 40, z: 1 };
  let tool = 'select', lastShape = 'rect', stickyColor = 'yellow', penColor = null, penWidth = 3;
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
      // a connector must redraw when either shape it is attached to moves
      const deps = e.type === 'connector' ? `${get(e.from?.id)?.version ?? ''}:${get(e.from?.id)?.x ?? ''},${get(e.from?.id)?.y ?? ''}|${get(e.to?.id)?.version ?? ''}:${get(e.to?.id)?.x ?? ''},${get(e.to?.id)?.y ?? ''},${get(e.to?.id)?.w ?? ''}` : '';
      // a stroke being drawn keeps its version while points are added, so the point count is part of the key
      const key = `${e.version}:${e.versionNonce}:${theme}:${editing?.id === e.id}:${deps}:${e.x},${e.y},${e.w},${e.h}:${e.points?.length ?? ''}`;
      let c = cache.get(e.id);
      if (!c || c.key !== key) {
        const tmp = svgEl('g');
        const node = drawElement(tmp, e, get, { theme, editingId: editing?.id, files: opts.files });
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
    ['pen', 'Pen', 'P'], ['highlighter', 'Highlighter', 'M'], ['eraser', 'Eraser', 'E'], '|',
    ['sticky', 'Sticky note', 'S'], ['text', 'Text', 'T'], ['shape', 'Shapes', 'R'], ['connector', 'Connector', 'A'], '|', ['frame', 'Frame', 'F'],
  ];
  function paintToolbar() {
    if (readOnly) { toolbar.hidden = true; return; }
    toolbar.hidden = false;
    toolbar.innerHTML = TOOLS.filter(t => !(compact && (t[0] === 'frame' || t[0] === 'highlighter'))).map(t => {
      if (t === '|') return '<span class="wb-sep"></span>';
      const [k, label, key] = t;
      const on = k === tool || (k === 'shape' && SHAPES.includes(tool));
      const ic = k === 'sticky' ? `<span class="wb-stk" style="background:${pal().sticky[stickyColor]}"></span>` : k === 'shape' ? icon(lastShape) : icon(k);
      return `<button type="button" data-tool="${k}" class="${on ? 'on' : ''}" title="${label} (${key})" aria-label="${label}" aria-pressed="${on}">${ic}<span class="wb-k">${key}</span></button>`;
    }).join('') + (compact && opts.onFullscreen ? `<span class="wb-sep"></span><button type="button" data-act="fullscreen" title="Open full screen" aria-label="Open full screen"><svg class="wb-i" viewBox="0 0 24 24"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg></button>` : '');
  }
  toolbar.addEventListener('pointerdown', e => e.stopPropagation());
  toolbar.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.act === 'fullscreen') { opts.onFullscreen?.(); return; }
    const k = b.dataset.tool;
    if (k === 'shape') { toggleShapeMenu(b); return; }
    if (k === 'sticky' && tool === 'sticky') stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length];
    setTool(k);
  });
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
    tool = t;
    if (t !== 'select') { sel.clear(); }
    root.dataset.tool = SHAPES.includes(t) ? 'shape' : t;
    shapeMenu.hidden = true;
    paintToolbar(); render();
  }

  // ── Corners: templates and full screen ──────────────────────────────────
  const TPL_EMOJI = { retro: '🔁', flowchart: '🔀', brainstorm: '💡', mindmap: '🧠', kanban: '🗂️', swot: '🧭', timeline: '🗓️', journey: '🚶', fishbone: '🐟', agenda: '📋', orgchart: '🏢' };
  const galleryHtml = () => TEMPLATE_INFO.map(([k, n, d]) => `<button type="button" class="wb-tpl" data-tpl="${k}"><span class="wb-tpl-e" aria-hidden="true">${TPL_EMOJI[k] || '▦'}</span><span><b>${n}</b><small>${d}</small></span></button>`).join('');
  let fullscreen = false;
  function paintCorners() {
    const canTemplates = !readOnly && mode === 'board';
    topLeft.innerHTML = canTemplates ? `<button type="button" class="wb-chip" data-c="tpl" aria-haspopup="dialog">${icon('grid')}<span>Templates</span></button>` : '';
    topLeft.hidden = !canTemplates;
    const fs = mode === 'board' && opts.onFullscreen;
    topRight.innerHTML = fs ? `<button type="button" class="wb-chip" data-c="fs" title="Full screen (Shift F)" aria-pressed="${fullscreen}"><svg class="wb-i" viewBox="0 0 24 24" aria-hidden="true">${fullscreen ? '<path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/>' : '<path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/>'}</svg><span>${fullscreen ? 'Exit full screen' : 'Full screen'}</span></button>` : '';
    topRight.hidden = !fs;
  }
  // While full screen, Esc must work wherever focus is (moving the board element to <body> drops its focus).
  const fsKeys = e => { if (e.key === 'Escape' && fullscreen && !editing && keysPanel.hidden && gallery.hidden) { e.preventDefault(); e.stopPropagation(); toggleFullscreen(); } };
  async function toggleFullscreen() {
    if (!opts.onFullscreen) return;
    const r = await opts.onFullscreen(!fullscreen);
    fullscreen = typeof r === 'boolean' ? r : !fullscreen;
    if (fullscreen) document.addEventListener('keydown', fsKeys, true); else document.removeEventListener('keydown', fsKeys, true);
    paintCorners();
    requestAnimationFrame(() => { fit(); root.focus({ preventScroll: true }); });
  }
  for (const c of [topLeft, topRight, gallery, emptyGallery]) c.addEventListener('pointerdown', e => e.stopPropagation());
  topLeft.addEventListener('click', e => {
    if (!e.target.closest('[data-c="tpl"]')) return;
    if (!gallery.hidden) { gallery.hidden = true; return; }
    gallery.innerHTML = `<div class="wb-ph"><b>Start from a template</b><button type="button" data-close aria-label="Close">✕</button></div><p class="wb-note">Added beside what is already on the board, never over it.</p><div class="wb-tgrid">${galleryHtml()}</div>`;
    gallery.hidden = false;
  });
  topRight.addEventListener('click', e => { if (e.target.closest('[data-c="fs"]')) toggleFullscreen(); });
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
    if (!els.length || readOnly || interaction || editing) { props.hidden = true; return; }
    const types = new Set(els.map(e => e.type));
    const only = t => types.size === 1 && types.has(t);
    const anyShape = els.some(e => SHAPES.includes(e.type));
    const parts = [];
    if (only('sticky')) parts.push(STICKY_COLORS.map(c => `<button type="button" data-sticky="${c}" title="${c[0].toUpperCase() + c.slice(1)}" aria-label="${c} sticky"><span class="wb-sw" style="background:${pal().sticky[c]}"></span></button>`).join(''));
    else if (els.some(e => e.type !== 'frame' && e.type !== 'image')) parts.push(INK.map(c => `<button type="button" data-ink="${c}" title="Colour" aria-label="Colour ${c}"><span class="wb-sw" style="background:${c}"></span></button>`).join(''));
    if (anyShape) parts.push(`<button type="button" data-menu="fill" title="Fill" aria-label="Fill"><span class="wb-sw wb-fillsw" style="background:${els.find(e => SHAPES.includes(e.type)).fill || 'transparent'}"></span></button>`);
    if (els.some(e => e.type === 'connector')) parts.push(['straight', 'elbow', 'curved'].map(r => `<button type="button" data-route="${r}" title="${r[0].toUpperCase() + r.slice(1)}">${r === 'straight' ? '／' : r === 'elbow' ? '⌐' : '∿'}</button>`).join('') + `<button type="button" data-heads title="Arrow ends">⇄</button><button type="button" data-dash title="Dashed">┄</button>`);
    if (els.some(e => e.type === 'path' || e.type === 'connector' || anyShape)) parts.push(`<button type="button" data-width title="Line weight">≡</button>`);
    if (els.some(e => e.type === 'text' || e.type === 'sticky' || anyShape)) parts.push(['ipop', 'hand', 'mono'].map(f => `<button type="button" data-font="${f}" title="${f === 'ipop' ? 'iPop font' : f === 'hand' ? 'Handwriting' : 'Monospace'}" style="font-family:${FONT_STACK[f].replace(/"/g, "'")}">Aa</button>`).join('') + SIZES.map(([n, s]) => `<button type="button" data-size="${s}" title="Text size ${n}">${n}</button>`).join(''));
    const locked = els.every(e => e.locked);
    parts.push(`<button type="button" data-act="front" title="Bring to front (Ctrl ])">${icon('front')}</button><button type="button" data-act="back" title="Send to back (Ctrl [)">${icon('back')}</button><button type="button" data-act="dup" title="Duplicate (Ctrl D)">${icon('copy')}</button><button type="button" data-act="lock" title="${locked ? 'Unlock' : 'Lock'}">${icon(locked ? 'unlock' : 'lock')}</button><button type="button" data-act="del" title="Delete (Del)" class="wb-danger">${icon('trash')}</button>`);
    props.innerHTML = parts.join('<span class="wb-sep"></span>');
    props.hidden = false;
    positionProps();
  }
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
    else if (b.dataset.ink) { upd(x => x.type === 'sticky' || x.type === 'frame' || x.type === 'image' ? null : { stroke: b.dataset.ink, ...(SHAPES.includes(x.type) ? { textColor: b.dataset.ink } : {}) }); penColor = b.dataset.ink; }
    else if (b.dataset.menu === 'fill') { const cur = els.find(x => SHAPES.includes(x.type))?.fill ?? null; const next = FILLS[(FILLS.indexOf(cur) + 1) % FILLS.length]; upd(x => SHAPES.includes(x.type) ? { fill: next } : null); }
    else if (b.dataset.route) upd(x => x.type === 'connector' ? { route: b.dataset.route } : null);
    else if ('heads' in b.dataset) upd(x => { if (x.type !== 'connector') return null; const s = x.startHead === 'arrow', en = (x.endHead ?? 'arrow') === 'arrow'; const next = en && !s ? [true, true] : en && s ? [false, false] : [false, true]; return { startHead: next[0] ? 'arrow' : 'none', endHead: next[1] ? 'arrow' : 'none' }; });
    else if ('dash' in b.dataset) upd(x => x.type === 'connector' ? { dashed: !x.dashed } : null);
    else if ('width' in b.dataset) upd(x => x.type === 'path' ? { width: (x.width || 3) >= 8 ? 2 : (x.width || 3) * 2 } : (x.type === 'connector' || SHAPES.includes(x.type)) ? { strokeWidth: (x.strokeWidth || 2) >= 6 ? 1 : (x.strokeWidth || 2) + 2 } : null);
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
  let interaction = null;
  const pointers = new Map();
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
    if (e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit')) return;
    if (editing) stopEdit();
    shapeMenu.hidden = true; neat.hidden = true;
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

    if (tool === 'hand' || spaceDown || e.button === 1 || (readOnly && tool !== 'laser')) { interaction = { kind: 'pan', sx, sy, view: { ...view } }; root.classList.add('wb-panning'); return; }
    if (e.button === 2) return;
    if (readOnly) return;
    if (tool === 'pen' || tool === 'highlighter') {
      const hl = tool === 'highlighter';
      const pressure = e.pointerType === 'pen' ? e.pressure || 0.5 : 0.5;
      const p = create('path', { x: w.x, y: w.y, points: [[0, 0, pressure]], stroke: hl ? '#FFCC00' : penColor, width: hl ? 18 : penWidth, highlight: hl || undefined, simulate: e.pointerType !== 'pen' || undefined });
      p.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'draw', el: p, pen: e.pointerType === 'pen' };
      draft.set(p.id, p); render();
      return;
    }
    if (tool === 'eraser') { interaction = { kind: 'erase', erased: new Set() }; eraseAt(w); return; }
    if (tool === 'sticky') { const s = create('sticky', { x: w.x - 100, y: w.y - 80, w: 200, h: 160, color: stickyColor, text: '' }); const [placed] = add([s]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (tool === 'text') { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: penColor }); const [placed] = add([t]) || []; setTool('select'); if (placed) editSoon(placed); return; }
    if (SHAPES.includes(tool) || tool === 'frame') {
      const s = create(tool, tool === 'frame' ? { x: w.x, y: w.y, w: 1, h: 1, name: `Frame ${live().filter(x => x.type === 'frame').length + 1}` } : { x: w.x, y: w.y, w: 1, h: 1, text: '', stroke: penColor });
      s.z = zBetween(topZ(all()) || null, null);
      interaction = { kind: 'create', el: s, ox: w.x, oy: w.y };
      draft.set(s.id, s); render();
      return;
    }
    if (tool === 'connector') {
      const hit = hitTest(live(), w.x, w.y, get, tolerance());
      const from = hit && hit.type !== 'connector' && hit.type !== 'path' ? { id: hit.id } : { x: w.x, y: w.y };
      const c = create('connector', { from, to: { x: w.x, y: w.y }, route: 'straight', endHead: 'arrow', startHead: 'none', stroke: penColor });
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
        // Coalesced events give smooth lines at high pointer rates; some browsers (and synthetic events) return none.
        const co = e.getCoalescedEvents?.() || [];
        const evs = co.length ? co : [e];
        const pts = it.el.points.slice();
        for (const ev of evs) {
          const l = local(ev), p = toWorld(l.sx, l.sy);
          pts.push([+(p.x - it.el.x).toFixed(2), +(p.y - it.el.y).toFixed(2), it.pen ? ev.pressure || 0.5 : 0.5]);
        }
        it.el = { ...it.el, points: pts };
        draft.set(it.el.id, it.el); render();
        break;
      }
      case 'erase': eraseAt(w); break;
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
        draft.delete(it.el.id);
        if (it.el.points.length === 1) it.el.points.push([0.1, 0.1, it.el.points[0][2]]);
        const [placed] = add([it.el], { select: false }) || [];
        if (placed && !placed.highlight) offerNeat(placed);
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
    const r = 10 / view.z;
    const victims = live().filter(x => (x.type === 'path' || x.type === 'connector') && !x.locked && !interaction?.erased?.has(x.id) && hitTest([x], w.x, w.y, get, r));
    if (!victims.length) return;
    victims.forEach(v => interaction?.erased?.add(v.id));
    write(victims.map(v => bump(v, { isDeleted: true })));
  }

  function onDouble(e) {
    if (readOnly || e.target.closest('.wb-tools,.wb-props,.wb-zoom,.wb-menu,.wb-panel,.wb-neat,.wb-edit')) return;
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
    if (hit && (hit.type === 'sticky' || hit.type === 'text' || SHAPES.includes(hit.type))) { sel = new Set([hit.id]); editText(hit); return; }
    if (hit?.type === 'connector') {
      const label = el('input', 'wb-edit'); label.value = hit.label || ''; label.placeholder = 'Label'; label.setAttribute('aria-label', 'Connector label');
      Object.assign(label.style, { left: `${sx - 70}px`, top: `${sy - 14}px`, width: '140px', height: '28px', textAlign: 'center' });
      root.append(label); label.focus();
      const done = () => { const v = label.value.trim(); label.remove(); write([bump(hit, { label: v || undefined })], { now: true }); commitStop(); };
      label.addEventListener('blur', done); label.addEventListener('keydown', ev => { ev.stopPropagation(); if (ev.key === 'Enter' || ev.key === 'Escape') label.blur(); });
      return;
    }
    if (!hit) { const t = create('text', { x: w.x, y: w.y - 13, w: 40, h: 26, text: '', size: 20, font: 'ipop', stroke: penColor }); const [placed] = add([t]) || []; if (placed) editText(placed); }
  }

  function onWheel(e) {
    e.preventDefault();
    const { sx, sy } = local(e);
    if (e.ctrlKey || e.metaKey) zoomAt(Math.exp(-e.deltaY * (e.deltaMode ? 0.05 : 0.0018)), sx, sy);
    else { view.x -= e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX; view.y -= e.shiftKey && !e.deltaX ? 0 : e.deltaY; render(); }
    if (editing) editing.place();
  }

  // ── "Make it neat" ───────────────────────────────────────────────────────
  let neatFor = null;
  function offerNeat(p) {
    const pts = p.points.map(q => [q[0] + p.x, q[1] + p.y]);
    const shape = recognise(pts);
    if (!shape) return;
    neatFor = { id: p.id, shape };
    neat.innerHTML = `<span>Make it a neat ${shape.type === 'ellipse' ? 'circle' : shape.type === 'rect' ? 'box' : 'triangle'}?</span><button type="button" data-n="yes" class="wb-pri">Yes</button><button type="button" data-n="no">No</button>`;
    const s = toScreen(shape.x + shape.w / 2, shape.y + shape.h), r = rect();
    neat.hidden = false;
    neat.style.left = `${Math.max(8, Math.min(s.x - neat.offsetWidth / 2, r.width - neat.offsetWidth - 8))}px`;
    neat.style.top = `${Math.min(s.y + 10, r.height - 110)}px`;
    clearTimeout(offerNeat.t); offerNeat.t = setTimeout(() => { neat.hidden = true; }, 6000);
  }
  neat.addEventListener('pointerdown', e => e.stopPropagation());
  neat.addEventListener('click', e => {
    const b = e.target.closest('[data-n]'); if (!b) return;
    neat.hidden = true;
    if (b.dataset.n !== 'yes' || !neatFor) return;
    const p = get(neatFor.id); if (!p) return;
    const { type, x, y, w, h } = neatFor.shape;
    write([bump(p, { isDeleted: true })], { now: true });
    add([create(type, { x, y, w, h, text: '', stroke: p.stroke })]);
    neatFor = null;
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
    if (e.key === 'Escape') { if (!keysPanel.hidden) { keysPanel.hidden = true; return; } if (!gallery.hidden) { gallery.hidden = true; return; } if (fullscreen) { toggleFullscreen(); return; } sel.clear(); setTool('select'); paintProps(); render(); return; }
    if (readOnly) return;
    const chosen = [...sel].map(get).filter(Boolean);
    if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); remove(chosen.filter(x => !x.locked)); paintProps(); return; }
    if (mod && k === 'd') { e.preventDefault(); duplicate(chosen); return; }
    if (mod && k === 'c') { if (chosen.length) { e.preventDefault(); copySel(chosen); } return; }
    if (mod && k === 'x') { if (chosen.length) { e.preventDefault(); copySel(chosen); remove(chosen.filter(x => !x.locked)); } return; }
    if (mod && k === 'v') { if (clipboard) { e.preventDefault(); pasteClip(); } return; }
    if (mod && e.key === ']') { e.preventDefault(); reorder(chosen, 'front'); commitStop(); return; }
    if (mod && e.key === '[') { e.preventDefault(); reorder(chosen, 'back'); commitStop(); return; }
    if (e.key === 'Enter' && chosen.length === 1 && (chosen[0].type === 'sticky' || chosen[0].type === 'text' || SHAPES.includes(chosen[0].type))) { e.preventDefault(); editText(chosen[0]); return; }
    if (e.key.startsWith('Arrow') && chosen.length) {
      e.preventDefault();
      const step = e.shiftKey ? 24 : 1, dx = e.key === 'ArrowLeft' ? -step : e.key === 'ArrowRight' ? step : 0, dy = e.key === 'ArrowUp' ? -step : e.key === 'ArrowDown' ? step : 0;
      write(chosen.filter(x => !x.locked).map(x => moved(x, dx, dy))); commitStop(); return;
    }
    if (mod || e.altKey) return;
    if (k === 's' && tool === 'sticky') { stickyColor = STICKY_COLORS[(STICKY_COLORS.indexOf(stickyColor) + 1) % STICKY_COLORS.length]; setTool('sticky'); return; }
    // R picks the rectangle; pressing it again while a shape tool is on walks through the other shapes (shown in the bar).
    if (k === 'r') { lastShape = SHAPES.includes(tool) ? SHAPES[(SHAPES.indexOf(tool) + 1) % SHAPES.length] : 'rect'; setTool(lastShape); return; }
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
    const rows = [['V', 'Select'], ['H', 'Hand (or hold Space)'], ['P', 'Pen'], ['M', 'Highlighter'], ['E', 'Eraser'], ['S', 'Sticky note (again: next colour)'], ['T', 'Text'], ['R', 'Shape (again: next shape)'], ['O', 'Circle'], ['D', 'Diamond'], ['A', 'Connector'], ['F', 'Frame'], ['Shift F', 'Full screen'],
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
  const ro = new ResizeObserver(() => {
    const r = rect();
    if (!fitted && r.width > 60 && r.height > 60) { fitted = true; fit(); }
    else render();
    if (editing) editing.place();
  });
  ro.observe(root);
  render();

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
    png: ({ scale = 2, dark = false } = {}) => toPng(live(), { scale, theme: dark ? 'dark' : 'light', files: opts.files }),
    svg: ({ dark = false } = {}) => { const o = toSvg(live(), { theme: dark ? 'dark' : 'light', files: opts.files }); return o ? new XMLSerializer().serializeToString(o.svg) : null; },
    count,
    flush,
    setFullscreen(v) { fullscreen = !!v; paintCorners(); requestAnimationFrame(() => fit()); },
    destroy() { document.removeEventListener('keydown', fsKeys, true); stopEdit(); flush(); destroyed = true; ymap.unobserve(onY); undo.destroy(); ro.disconnect(); cancelAnimationFrame(raf); root.remove(); },
  };
  return api;
}
