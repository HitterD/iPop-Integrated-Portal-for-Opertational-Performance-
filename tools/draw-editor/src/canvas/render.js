// Drawing elements as SVG. The same code draws the live board, the minimap thumbnail and exports, so what is exported is
// exactly what is on screen. Clean ClickUp-like shapes; only the pen looks hand-drawn (perfect-freehand).
import { getStroke } from 'perfect-freehand';
import { connectorEnds, connectorPath, connectorMid, connectorPoints, drawOrder, wrapLines, bounds, SHAPE_TYPES } from './model.js';
import { stencilPath, stencilTextBox, lanesOf, rackSlots, STENCIL } from './stencils.js';
import { pathWithJumps, pointAlong } from './flow.js';
import { parse as parseRich, hasFormatting, wrapRuns } from './richtext.js';

const NS = 'http://www.w3.org/2000/svg';
export const svgEl = (tag, attrs = {}, parent) => {
  const e = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) if (v != null && v !== false) e.setAttribute(k, String(v));
  parent?.appendChild(e);
  return e;
};

/// Theme colours. Stickies and default ink differ per theme; explicit colours chosen by people stay as they are.
export const PALETTE = {
  light: {
    ink: '#1D1D1F', ink2: '#424245', ink3: '#86868B', shapeFill: '#FFFFFF', frameFill: 'rgba(0,0,0,.025)', frameLine: 'rgba(0,0,0,.16)',
    connector: '#6E6E73', sel: '#0A84FF', board: '#FBFBFA',
    sticky: { yellow: '#FFF3A3', green: '#C9F2C7', blue: '#CFE6FF', pink: '#FFD3E1', purple: '#E6DAFF', orange: '#FFDDB8', gray: '#EEEEF0' },
    stickyInk: '#1D1D1F',
  },
  dark: {
    ink: '#F5F5F7', ink2: '#D1D1D6', ink3: '#8E8E93', shapeFill: '#1C1C1E', frameFill: 'rgba(255,255,255,.035)', frameLine: 'rgba(255,255,255,.2)',
    connector: '#A1A1A6', sel: '#409CFF', board: '#141416',
    sticky: { yellow: '#5A5220', green: '#244A2A', blue: '#1F3B5A', pink: '#5A2638', purple: '#3A2D5A', orange: '#5A3A1C', gray: '#3A3A3E' },
    stickyInk: '#F5F5F7',
  },
};
/// Ink chosen as "default" (null) follows the theme; near-black ink on a dark board is shown light so old drawings stay readable.
export function inkFor(color, pal) {
  if (!color || color === 'auto') return pal.ink;
  if (pal === PALETTE.dark && /^#(1d1d1f|1e1e1e|000000|000|212121|2a2a2a)$/i.test(color)) return pal.ink;
  return color;
}

export const FONT_STACK = {
  ipop: '-apple-system, BlinkMacSystemFont, "SF Pro Text", "Inter", "Segoe UI", system-ui, sans-serif',
  hand: '"Excalifont", "Segoe Print", "Bradley Hand", "Chalkboard SE", "Comic Sans MS", cursive',
  mono: '"SF Mono", ui-monospace, Menlo, Consolas, monospace',
};

let measureCtx = null;
/// Width of `text` in px at `size` in the given font (real measurement in a browser, an estimate elsewhere).
export function measure(text, size, font = 'ipop', weight = 400) {
  if (typeof document === 'undefined') return text.length * size * 0.55;
  measureCtx ||= document.createElement('canvas').getContext('2d');
  if (!measureCtx) return text.length * size * 0.55;
  measureCtx.font = `${weight} ${size}px ${FONT_STACK[font] || FONT_STACK.ipop}`;
  // a little extra so SVG text (hinted differently from canvas text) never pokes past the edge it was wrapped for
  return measureCtx.measureText(text).width * 1.06;
}

/// SVG path outline of a pen stroke (filled, not stroked) from points [[dx,dy,pressure]].
export function strokeOutline(points, size = 3, { simulatePressure = false, kind = 'legacy' } = {}) {
  if (!points?.length) return '';
  // legacy = strokes drawn before pen types existed (their width was multiplied by 1.6); the rest use the width as drawn
  const o = {
    legacy: { size: size * 1.6, thinning: 0.5 },
    pen: { size: size * 1.25, thinning: 0.55 },
    marker: { size, thinning: 0, start: { cap: true }, end: { cap: true } },
    pencil: { size: size * 1.1, thinning: 0.25 },
    // round 6 pens: a fineliner never changes width; a brush tapers at both ends; a crayon is a little uneven
    fine: { size, thinning: 0, start: { cap: true }, end: { cap: true } },
    brush: { size: size * 1.35, thinning: 0.7, start: { taper: size * 4, cap: true }, end: { taper: size * 5, cap: true } },
    crayon: { size, thinning: 0.15, start: { cap: true }, end: { cap: true } },
    neon: { size, thinning: 0, start: { cap: true }, end: { cap: true } },
  }[kind] || { size: size * 1.6, thinning: 0.5 };
  const outline = getStroke(points.map(p => [p[0], p[1], p[2] ?? 0.5]), { smoothing: 0.5, streamline: 0.45, simulatePressure: kind === 'marker' || kind === 'fine' || kind === 'neon' ? false : simulatePressure, ...o });
  if (!outline.length) return '';
  let d = `M${outline[0][0].toFixed(2)} ${outline[0][1].toFixed(2)}`;
  for (let i = 1; i < outline.length; i++) {
    const [x0, y0] = outline[i - 1], [x1, y1] = outline[i];
    d += `Q${x0.toFixed(2)} ${y0.toFixed(2)} ${((x0 + x1) / 2).toFixed(2)} ${((y0 + y1) / 2).toFixed(2)}`;
  }
  return d + 'Z';
}

/// A calligraphy stroke: a flat nib held at 45°, so the line is thick across and thin along that angle. Filled outline.
export function nibOutline(points, size = 6) {
  if (!points?.length) return '';
  const dx = Math.cos(Math.PI / 4) * size / 2, dy = -Math.sin(Math.PI / 4) * size / 2;
  let d = '';
  for (let i = 1; i < points.length; i++) {
    const [ax, ay] = points[i - 1], [bx, by] = points[i];
    d += `M${(ax - dx).toFixed(2)} ${(ay - dy).toFixed(2)}L${(ax + dx).toFixed(2)} ${(ay + dy).toFixed(2)}L${(bx + dx).toFixed(2)} ${(by + dy).toFixed(2)}L${(bx - dx).toFixed(2)} ${(by - dy).toFixed(2)}Z`;
  }
  if (points.length === 1) { const [x, y] = points[0]; d = `M${x - dx} ${y - dy}L${x + dx} ${y + dy}`; }
  return d;
}
const polyline = pts => pts.map((p, i) => `${i ? 'L' : 'M'}${p[0]} ${p[1]}`).join('');

// Outlines of the shape types (box x,y,w,h). Rect/rounded/ellipse use native SVG; the rest are paths.
export function shapePath(type, x, y, w, h) {
  const cx = x + w / 2, cy = y + h / 2;
  switch (type) {
    case 'diamond': return `M${cx} ${y}L${x + w} ${cy}L${cx} ${y + h}L${x} ${cy}Z`;
    case 'triangle': return `M${cx} ${y}L${x + w} ${y + h}L${x} ${y + h}Z`;
    case 'star': {
      let d = '';
      for (let i = 0; i < 10; i++) {
        const a = -Math.PI / 2 + (i * Math.PI) / 5, r = i % 2 ? 0.42 : 1;
        d += `${i ? 'L' : 'M'}${(cx + (w / 2) * r * Math.cos(a)).toFixed(2)} ${(cy + (h / 2) * r * Math.sin(a)).toFixed(2)}`;
      }
      return d + 'Z';
    }
    case 'cloud': {
      const p = (fx, fy) => `${(x + w * fx).toFixed(2)} ${(y + h * fy).toFixed(2)}`;
      return `M${p(0.25, 0.85)}C${p(0.05, 0.85)} ${p(0.02, 0.55)} ${p(0.18, 0.48)}C${p(0.12, 0.2)} ${p(0.42, 0.08)} ${p(0.52, 0.25)}`
        + `C${p(0.62, 0.02)} ${p(0.92, 0.12)} ${p(0.85, 0.42)}C${p(1.02, 0.5)} ${p(0.98, 0.85)} ${p(0.75, 0.85)}Z`;
    }
    case 'arrowShape': {
      const head = Math.min(w * 0.4, h);
      return `M${x} ${y + h * 0.28}H${x + w - head}V${y}L${x + w} ${cy}L${x + w - head} ${y + h}V${y + h * 0.72}H${x}Z`;
    }
    case 'cylinder': {
      const ry = Math.min(h * 0.14, 18);
      return `M${x} ${y + ry}A${w / 2} ${ry} 0 0 1 ${x + w} ${y + ry}V${y + h - ry}A${w / 2} ${ry} 0 0 1 ${x} ${y + h - ry}Z`
        + `M${x} ${y + ry}A${w / 2} ${ry} 0 0 0 ${x + w} ${y + ry}`;
    }
    case 'bubble': {
      const r = Math.min(14, w / 4, h / 4), tail = Math.min(h * 0.25, 22), by = y + h - tail;
      return `M${x + r} ${y}H${x + w - r}Q${x + w} ${y} ${x + w} ${y + r}V${by - r}Q${x + w} ${by} ${x + w - r} ${by}`
        + `H${x + w * 0.38}L${x + w * 0.2} ${y + h}L${x + w * 0.24} ${by}H${x + r}Q${x} ${by} ${x} ${by - r}V${y + r}Q${x} ${y} ${x + r} ${y}Z`;
    }
    default: return '';
  }
}

/// Where text sits inside a shape: the usable box (a diamond / triangle / star has less room than its bounds).
function textBox(el) {
  const { x, y, w, h } = el;
  switch (el.type) {
    case 'diamond': return { x: x + w * 0.22, y: y + h * 0.22, w: w * 0.56, h: h * 0.56 };
    case 'triangle': return { x: x + w * 0.25, y: y + h * 0.45, w: w * 0.5, h: h * 0.5 };
    case 'star': return { x: x + w * 0.3, y: y + h * 0.32, w: w * 0.4, h: h * 0.4 };
    case 'bubble': return { x: x + 10, y: y + 6, w: w - 20, h: h * 0.72 };
    case 'cylinder': return { x: x + 8, y: y + h * 0.2, w: w - 16, h: h * 0.7 };
    case 'sticky': return { x: x + 14, y: y + 12, w: w - 28, h: h - 24 };
    default: return { x: x + 10, y: y + 6, w: Math.max(0, w - 20), h: Math.max(0, h - 12) };
  }
}

/// Lays text out in a box: returns { lines, size, lh } where size shrinks (down to 10) until the text fits.
export function layoutText(text, box, { size = 16, font = 'ipop', weight = 400, fit = true } = {}) {
  let s = size;
  for (;;) {
    const lines = wrapLines(text, box.w, t => measure(t, s, font, weight));
    const lh = s * 1.3;
    if (!fit || lines.length * lh <= box.h || s <= 10) return { lines, size: s, lh };
    s -= 1;
  }
}

function drawTextLines(g, lines, box, { size, lh, font, color, align = 'middle', valign = 'middle', weight = 400 }) {
  const total = lines.length * lh;
  const top = valign === 'top' ? box.y : box.y + (box.h - total) / 2;
  const x = align === 'start' ? box.x : box.x + box.w / 2;
  const t = svgEl('text', { x, 'font-size': size, 'font-family': FONT_STACK[font] || FONT_STACK.ipop, 'font-weight': weight, fill: color, 'text-anchor': align === 'start' ? 'start' : 'middle' }, g);
  lines.forEach((l, i) => { const span = svgEl('tspan', { x, y: top + i * lh + size * 0.98 }, t); span.textContent = l || '​'; });
  return t;
}

/// Formatted text (bold/italic/strike/links/bullets) in a box: wraps, shrinks to fit when `fit` is on, draws links
/// underlined in the link colour. Plain text keeps the faster layoutText path.
function drawRichText(g, text, box, { size = 16, font = 'ipop', weight = 400, color, align = 'middle', valign = 'middle', fit = true, link = '#0A84FF' }) {
  const lines = parseRich(text);
  const fam = FONT_STACK[font] || FONT_STACK.ipop;
  let s = size, wrapped, lh;
  for (;;) {
    wrapped = wrapRuns(lines, box.w, (t, r) => measure(t, s, font, r.b ? 700 : weight), s * 1.1);
    lh = s * 1.3;
    if (!fit || wrapped.length * lh <= box.h || s <= 10) break;
    s -= 1;
  }
  const indent = s * 1.1;
  const total = wrapped.length * lh, top = valign === 'top' ? box.y : box.y + (box.h - total) / 2;
  wrapped.forEach((l, i) => {
    const y = top + i * lh + s * 0.98;
    const pad = l.bullet ? indent : 0;
    const w = l.runs.reduce((a, r) => a + measure(r.text, s, font, r.b ? 700 : weight), 0);
    const x0 = align === 'start' ? box.x + pad : align === 'end' ? box.x + box.w - w : box.x + pad + (box.w - pad - w) / 2;
    if (l.bullet && l.first) { const b = svgEl('text', { x: x0 - indent * 0.75, y, 'font-size': s, 'font-family': fam, fill: color }, g); b.textContent = '•'; }
    const t = svgEl('text', { x: x0, y, 'font-size': s, 'font-family': fam, 'font-weight': weight, fill: color, 'xml:space': 'preserve' }, g);
    for (const r of l.runs) {
      const deco = [r.s ? 'line-through' : '', r.href ? 'underline' : ''].filter(Boolean).join(' ');
      const sp = svgEl('tspan', { 'font-weight': r.b ? 700 : null, 'font-style': r.i ? 'italic' : null, 'text-decoration': deco || null, fill: r.href ? link : null, 'data-href': r.href || null }, t);
      sp.textContent = r.text;
    }
    if (!l.runs.length) t.textContent = '\u200b';
  });
}
const ALIGN = { left: 'start', center: 'middle', right: 'end' };

/// Draws one element into the group `g`. `get(id)` resolves connector ends. `opts`: { theme, files: Map(fileId -> url), editingId }.
export function drawElement(g, el, get, opts = {}) {
  const pal = PALETTE[opts.theme] || PALETTE.light;
  const node = svgEl('g', { 'data-id': el.id, opacity: el.opacity != null ? el.opacity : null }, g);
  const editing = opts.editingId === el.id;
  switch (el.type) {
    case 'frame': {
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 14, fill: pal.frameFill, stroke: pal.frameLine, 'stroke-width': 1.2, 'vector-effect': 'non-scaling-stroke' }, node);
      const t = svgEl('text', { x: el.x + 2, y: el.y - 9, 'font-size': 14, 'font-weight': 600, 'font-family': FONT_STACK.ipop, fill: pal.ink3 }, node);
      t.textContent = el.name || 'Frame';
      break;
    }
    case 'sticky': {
      const fill = pal.sticky[el.color] || pal.sticky.yellow;
      svgEl('rect', { x: el.x + 1, y: el.y + 3, width: el.w, height: el.h, rx: 4, fill: 'rgba(0,0,0,.10)', filter: 'url(#wb-soft)' }, node);
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 4, fill }, node);
      if (el.text && !editing) {
        const box = textBox(el);
        if (hasFormatting(el.text) || (el.align && el.align !== 'left')) drawRichText(node, el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500, color: pal.stickyInk, align: ALIGN[el.align || 'left'], valign: 'top' });
        else {
          const lt = layoutText(el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500 });
          drawTextLines(node, lt.lines, box, { ...lt, font: el.font || 'ipop', color: pal.stickyInk, align: 'start', valign: 'top', weight: 500 });
        }
      }
      break;
    }
    case 'text': {
      if (editing) break;
      const font = el.font || 'ipop', size = el.size || 20;
      if (hasFormatting(el.text) || (el.align && el.align !== 'left')) {
        const n = String(el.text ?? '').split('\n').length;
        drawRichText(node, el.text, { x: el.x, y: el.y, w: Math.max(el.w, 20), h: Math.max(el.h, n * size * 1.3) }, { size, font, color: inkFor(el.stroke, pal), align: ALIGN[el.align || 'left'], valign: 'top', fit: false });
        break;
      }
      const lines = String(el.text ?? '').split('\n');
      drawTextLines(node, lines, { x: el.x, y: el.y, w: el.w, h: lines.length * size * 1.3 }, { size, lh: size * 1.3, font, color: inkFor(el.stroke, pal), align: 'start', valign: 'top' });
      break;
    }
    case 'path': {
      const color = inkFor(el.stroke, pal);
      const tg = svgEl('g', { transform: `translate(${el.x} ${el.y})` }, node);
      if (el.highlight || el.tool === 'highlighter') {
        let d = '';
        el.points.forEach((p, i) => { d += `${i ? 'L' : 'M'}${p[0]} ${p[1]}`; });
        // multiply keeps the writing underneath readable on a light board; a dark board uses plain transparency
        svgEl('path', { d, fill: 'none', stroke: color, 'stroke-width': el.width || 18, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', opacity: 0.38, style: opts.theme === 'dark' ? null : 'mix-blend-mode:multiply' }, tg);
      } else if (el.tool === 'pencil') {
        // a pencil: a thin core plus two faint offset passes, for a grainy graphite look
        const d = strokeOutline(el.points, el.width || 2, { simulatePressure: !!el.simulate, kind: 'pencil' });
        const k = Math.max(1, el.width || 2) / 2;
        svgEl('path', { d, fill: color, opacity: 0.85 }, tg);
        for (const [ox, oy, a] of [[0.45, -0.35, 0.3], [-0.35, 0.45, 0.25]]) svgEl('path', { d, fill: color, opacity: a, transform: `translate(${(ox * k).toFixed(2)} ${(oy * k).toFixed(2)})` }, tg);
      } else if (el.straight || el.dashed) {
        // a ruler line, or any pen drawn dashed: a plain stroked line (an outline cannot be dashed)
        const w = el.width || 2;
        svgEl('path', { d: polyline(el.points), fill: 'none', stroke: color, 'stroke-width': w, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'stroke-dasharray': el.dashed ? `${(w * 2.6).toFixed(1)} ${(w * 2.2).toFixed(1)}` : null }, tg);
      } else if (el.tool === 'callig') {
        svgEl('path', { d: nibOutline(el.points, el.width || 6), fill: color, stroke: color, 'stroke-width': 0.6, 'stroke-linejoin': 'round' }, tg);
      } else if (el.tool === 'crayon') {
        // wax on paper: the stroke is broken up by a grain pattern (the filter keeps it deterministic, same on every screen)
        svgEl('path', { d: strokeOutline(el.points, el.width || 7, { kind: 'crayon' }), fill: color, filter: 'url(#wb-crayon)', opacity: 0.92 }, tg);
      } else if (el.tool === 'neon') {
        const w = el.width || 3, d = strokeOutline(el.points, w, { kind: 'neon' });
        svgEl('path', { d: strokeOutline(el.points, w * 2.4, { kind: 'neon' }), fill: color, opacity: 0.35, filter: 'url(#wb-glow)' }, tg);
        svgEl('path', { d, fill: color }, tg);
        svgEl('path', { d: strokeOutline(el.points, Math.max(1, w * 0.38), { kind: 'neon' }), fill: '#FFFFFF', opacity: 0.85 }, tg);
      } else {
        const kind = ['pen', 'marker', 'fine', 'brush'].includes(el.tool) ? el.tool : 'legacy';
        svgEl('path', { d: strokeOutline(el.points, el.width || 3, { simulatePressure: !!el.simulate, kind }), fill: color }, tg);
      }
      // an arrow at the end of the stroke, along its last direction
      if (el.arrowEnd && el.points.length > 1) {
        const pts = el.points, b = pts.at(-1);
        let a = pts[Math.max(0, pts.length - 2)];
        for (let i = pts.length - 2; i >= 0; i--) { a = pts[i]; if (Math.hypot(b[0] - a[0], b[1] - a[1]) > (el.width || 3) * 3) break; }
        const ang = Math.atan2(b[1] - a[1], b[0] - a[0]), L = Math.max(10, (el.width || 3) * 3.2);
        const p1 = [b[0] - L * Math.cos(ang - 0.5), b[1] - L * Math.sin(ang - 0.5)], p2 = [b[0] - L * Math.cos(ang + 0.5), b[1] - L * Math.sin(ang + 0.5)];
        svgEl('path', { d: `M${p1[0].toFixed(2)} ${p1[1].toFixed(2)}L${b[0]} ${b[1]}L${p2[0].toFixed(2)} ${p2[1].toFixed(2)}`, fill: 'none', stroke: color, 'stroke-width': Math.max(2, (el.width || 3) * 0.9), 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, tg);
      }
      break;
    }
    case 'connector': {
      const [a, b] = connectorEnds(el, get);
      if (!a || !b) break;
      // a line labelled "VLAN 30" takes that VLAN's colour unless someone picked a colour
      const vl = !el.stroke && /\bvlan\s*(\d+)/i.exec(el.label || '');
      const color = vl ? vlanColor(vl[1]) : inkFor(el.stroke, { ...pal, ink: pal.connector }) || pal.connector;
      const route = el.route || 'straight';
      const pts = route === 'elbow' || route === 'smart' || el.waypoints?.length ? connectorPoints(el, get, route === 'smart' ? (opts.obstaclesFor?.(el) || []) : []) : null;
      const head = (k, start) => ({ arrow: start ? 'url(#wb-head-start)' : 'url(#wb-head)', open: start ? 'url(#wb-open-s)' : 'url(#wb-open)', filled: start ? 'url(#wb-fill-s)' : 'url(#wb-fill)', diamond: start ? 'url(#wb-dia-s)' : 'url(#wb-dia)', dot: 'url(#wb-dot)', crow: start ? 'url(#wb-crow-s)' : 'url(#wb-crow)', one: start ? 'url(#wb-one-s)' : 'url(#wb-one)' }[k] || null);
      const d = pts ? pathWithJumps(pts, el.jumps === false ? [] : (opts.jumpsFor?.(el) || []), { round: 6 }) : connectorPath(a, b, route);
      if (pts) opts.onRouted?.(el.id, pts);
      svgEl('path', {
        d, fill: 'none', stroke: color, 'stroke-width': el.strokeWidth || 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round',
        'stroke-dasharray': el.dashed ? '6 5' : null,
        'marker-end': head(el.endHead ?? 'arrow', false),
        'marker-start': head(el.startHead || 'none', true),
        style: `color:${color}`,
      }, node);
      if (el.label) {
        const m0 = pts ? pointAlong(pts, el.labelAt ?? 0.5) : connectorMid(a, b, route);
        const m = m0, w = measure(el.label, 13) + 14;
        svgEl('rect', { x: m.x - w / 2, y: m.y - 11, width: w, height: 22, rx: 6, fill: pal.board }, node);
        const t = svgEl('text', { x: m.x, y: m.y + 4.5, 'text-anchor': 'middle', 'font-size': 13, 'font-family': FONT_STACK.ipop, fill: pal.ink2 }, node);
        t.textContent = el.label;
      }
      break;
    }
    case 'stamp': {
      const t = svgEl('text', { x: el.x + el.w / 2, y: el.y + el.h * 0.8, 'text-anchor': 'middle', 'font-size': el.h * 0.85 }, node);
      t.textContent = el.emoji || '👍';
      break;
    }
    case 'noteCard': {
      const info = opts.cardInfo?.page?.(el.pageId);
      const gone = opts.cardInfo?.page && !info;
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 12, fill: pal.shapeFill, stroke: pal.frameLine }, node);
      svgEl('rect', { x: el.x, y: el.y + 10, width: 4, height: el.h - 20, rx: 2, fill: '#34A853' }, node);
      const k = svgEl('text', { x: el.x + 18, y: el.y + 26, 'font-size': 11, fill: pal.ink3, 'font-family': FONT_STACK.ipop }, node);
      k.textContent = gone ? 'Notes page · no access' : 'Notes page';
      const t = svgEl('text', { x: el.x + 18, y: el.y + 50, 'font-size': 15, 'font-weight': 650, fill: gone ? pal.ink3 : pal.ink, 'font-family': FONT_STACK.ipop }, node);
      const title = gone ? 'Private page' : `${info?.emoji || el.emoji || '📄'} ${info?.title || el.title || 'Untitled'}`;
      t.textContent = title.length > 30 ? title.slice(0, 29) + '…' : title;
      break;
    }
    case 'taskCard': {
      const info = opts.cardInfo?.task?.(el.taskId);
      const hidden = info?.private;
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 12, fill: pal.shapeFill, stroke: pal.frameLine }, node);
      const st = String(info?.status || 'todo');
      const stColor = { todo: '#8E8E93', doing: '#FF9500', review: '#AF52DE', done: '#34A853' }[st] || '#0A84FF';
      svgEl('rect', { x: el.x, y: el.y + 10, width: 4, height: el.h - 20, rx: 2, fill: hidden ? pal.ink3 : stColor }, node);
      const k = svgEl('text', { x: el.x + 18, y: el.y + 24, 'font-size': 11, fill: pal.ink3, 'font-family': FONT_STACK.ipop }, node);
      k.textContent = hidden ? 'Projects task' : (info?.project ? `☑ ${String(info.project).slice(0, 34)}` : '☑ Projects task');
      const t = svgEl('text', { x: el.x + 18, y: el.y + 48, 'font-size': 15, 'font-weight': 650, fill: hidden ? pal.ink3 : pal.ink, 'font-family': FONT_STACK.ipop }, node);
      const title = hidden ? '🔒 Private task' : String(info?.title || el.title || 'Task');
      t.textContent = title.length > 30 ? title.slice(0, 29) + '…' : title;
      if (!hidden) {
        const label = { todo: 'To do', doing: 'In progress', review: 'In review', done: 'Done' }[st] || st;
        const lw = measure(label, 11, 'ipop', 600) + 16;
        svgEl('rect', { x: el.x + 18, y: el.y + 62, width: lw, height: 20, rx: 10, fill: stColor, opacity: 0.16 }, node);
        const sl = svgEl('text', { x: el.x + 26, y: el.y + 76, 'font-size': 11, 'font-weight': 600, fill: stColor, 'font-family': FONT_STACK.ipop }, node);
        sl.textContent = label;
        const meta = [info?.people?.length ? info.people.slice(0, 2).join(', ') + (info.people.length > 2 ? ` +${info.people.length - 2}` : '') : null, info?.due ? `📅 ${info.due}` : null].filter(Boolean).join(' · ');
        if (meta) { const m = svgEl('text', { x: el.x + 26 + lw, y: el.y + 76, 'font-size': 11, fill: pal.ink3, 'font-family': FONT_STACK.ipop }, node); m.textContent = meta.length > 34 ? meta.slice(0, 33) + '…' : meta; }
      }
      break;
    }
    case 'file': {
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 10, fill: pal.shapeFill, stroke: pal.frameLine }, node);
      const ext = (String(el.name || '').match(/\.([a-z0-9]{1,5})$/i)?.[1] || 'FILE').toUpperCase();
      svgEl('rect', { x: el.x + 12, y: el.y + 12, width: 34, height: el.h - 24, rx: 6, fill: ext === 'PDF' ? 'rgba(255,59,48,.14)' : /XLSX?|CSV/.test(ext) ? 'rgba(52,168,83,.16)' : /DOCX?/.test(ext) ? 'rgba(10,132,255,.14)' : 'rgba(118,118,128,.16)' }, node);
      const tx = svgEl('text', { x: el.x + 29, y: el.y + el.h / 2 + 4, 'text-anchor': 'middle', 'font-size': 9.5, 'font-weight': 700, 'font-family': FONT_STACK.ipop, fill: ext === 'PDF' ? '#C9372C' : pal.ink2 }, node);
      tx.textContent = ext.slice(0, 4);
      const nm = svgEl('text', { x: el.x + 56, y: el.y + el.h / 2 - 2, 'font-size': 13, 'font-weight': 600, 'font-family': FONT_STACK.ipop, fill: pal.ink }, node);
      const name = String(el.name || 'File');
      nm.textContent = name.length > 26 ? name.slice(0, 24) + '…' : name;
      const sz = svgEl('text', { x: el.x + 56, y: el.y + el.h / 2 + 15, 'font-size': 11, 'font-family': FONT_STACK.ipop, fill: pal.ink3 }, node);
      sz.textContent = `${el.size ? (el.size < 1048576 ? `${Math.max(1, Math.round(el.size / 1024))} KB` : `${(el.size / 1048576).toFixed(1)} MB`) : ''} · Double-click to open`;
      break;
    }
    case 'link': {
      // a web address with its preview (title, site, picture); the picture is optional
      const img = el.image ? opts.files?.get(`lp:${el.image}`) : null;
      svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 12, fill: pal.shapeFill, stroke: pal.frameLine }, node);
      const ih = img ? Math.min(el.h * 0.55, el.w * 0.52) : 0;
      if (img) {
        const cid = `wbc-${el.id}`;
        const cp = svgEl('clipPath', { id: cid }, node);
        svgEl('rect', { x: el.x, y: el.y, width: el.w, height: ih + 12, rx: 12 }, cp);
        svgEl('image', { href: img, x: el.x, y: el.y, width: el.w, height: ih, preserveAspectRatio: 'xMidYMid slice', 'clip-path': `url(#${cid})` }, node);
      }
      const tx = el.x + 14, top = el.y + ih + 12, iw = el.w - 28;
      const title = String(el.title || el.url || 'Link');
      const all = wrapLines(title, iw, t => measure(t, 14, 'ipop', 650));
      all.slice(0, 2).forEach((l, i) => { const t = svgEl('text', { x: tx, y: top + 16 + i * 18, 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: pal.ink }, node); t.textContent = i === 1 && all.length > 2 ? l.replace(/.$/, '…') : l; });
      let site = String(el.site || '');
      if (!site) { try { site = new URL(el.url).hostname.replace(/^www\./, ''); } catch { site = ''; } }
      const st = svgEl('text', { x: tx, y: Math.min(el.y + el.h - 12, top + 16 + Math.min(2, all.length) * 18 + 4), 'font-size': 12, 'font-family': FONT_STACK.ipop, fill: pal.ink3 }, node);
      st.textContent = site.length > 40 ? site.slice(0, 39) + '…' : site;
      break;
    }
    case 'sticker': {
      const url = opts.files?.get(`st:${el.stickerId}`);
      if (url) svgEl('image', { href: url, x: el.x, y: el.y, width: el.w, height: el.h, preserveAspectRatio: 'xMidYMid meet' }, node);
      else svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 10, fill: pal.frameFill }, node);
      break;
    }
    case 'image': {
      const url = opts.files?.get(el.fileId);
      if (url) svgEl('image', { href: url, x: el.x, y: el.y, width: el.w, height: el.h, preserveAspectRatio: 'xMidYMid slice' }, node);
      else {
        svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 6, fill: pal.frameFill, stroke: pal.frameLine, 'stroke-dasharray': '6 4' }, node);
        const t = svgEl('text', { x: el.x + el.w / 2, y: el.y + el.h / 2 + 5, 'text-anchor': 'middle', 'font-size': 13, fill: pal.ink3, 'font-family': FONT_STACK.ipop }, node);
        t.textContent = 'Picture';
      }
      break;
    }
    case 'swimlane': drawSwimlane(node, el, pal, opts); break;
    default: {
      // shapes
      const stroke = inkFor(el.stroke, pal), fill = el.fill === 'none' ? 'none' : (el.fill || pal.shapeFill), sw = el.strokeWidth || 2;
      const common = { fill, stroke, 'stroke-width': sw, 'stroke-linejoin': 'round', 'stroke-dasharray': el.dashed ? `${sw * 3} ${sw * 2.5}` : null, filter: el.shadow ? 'url(#wb-shadow)' : null };
      const st = stencilPath(el.type, el.x, el.y, el.w, el.h);
      if (st) { drawStencil(node, el, st, common, pal, opts); break; }
      if (el.type === 'rect') svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: el.rounded === false ? 0 : Math.min(12, el.w / 4, el.h / 4), ...common }, node);
      else if (el.type === 'ellipse') svgEl('ellipse', { cx: el.x + el.w / 2, cy: el.y + el.h / 2, rx: el.w / 2, ry: el.h / 2, ...common }, node);
      else svgEl('path', { d: shapePath(el.type, el.x, el.y, el.w, el.h), ...common }, node);
      if (el.text && !editing) {
        const box = textBox(el);
        const tc = el.textColor ? inkFor(el.textColor, pal) : inkOnFill(el.fill, pal) || inkFor(null, pal);
        if (hasFormatting(el.text) || (el.align && el.align !== 'center')) drawRichText(node, el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500, color: tc, align: ALIGN[el.align || 'center'] });
        else {
          const lt = layoutText(el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500 });
          drawTextLines(node, lt.lines, box, { ...lt, font: el.font || 'ipop', color: tc, weight: 500 });
        }
      }
    }
  }
  if (el.link && el.type !== 'connector') {
    const b = bounds(el, get);
    const c = svgEl('g', { 'data-link': el.link, class: 'wb-linkbadge' }, node);
    svgEl('circle', { cx: b.x + b.w, cy: b.y, r: 9, fill: pal.sel }, c);
    const t = svgEl('text', { x: b.x + b.w, y: b.y + 4, 'text-anchor': 'middle', 'font-size': 11, fill: '#fff' }, c);
    t.textContent = '↗';
  }
  return node;
}

/// Text colour that reads on a fill: dark on light fills, light on dark ones (null = the theme's own ink).
function inkOnFill(fill, pal) {
  const m = /^#([0-9a-f]{6})$/i.exec(fill || '');
  if (!m) return null;
  const n = parseInt(m[1], 16), lum = (0.299 * (n >> 16) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255)) / 255;
  return lum > 0.6 ? '#1D1D1F' : lum < 0.35 ? '#F5F5F7' : null;
}
export { inkOnFill };

/// A diagram stencil (flowchart, BPMN, ERD, network, timeline, value stream, group box).
function drawStencil(node, el, st, common, pal, opts) {
  const sharp = el.rounded === false;
  // data graphics: a colour rule may tint the fill
  const ruleFill = opts.graphics?.(el)?.fill;
  const fill = st.solid ? (el.fill && el.fill !== 'none' ? el.fill : common.stroke) : ruleFill || (st.fill && !el.fill ? st.fill : common.fill);
  // a callout points at the shape it belongs to
  if (el.type === 'callout' && el.attach) {
    const t = opts.getEl?.(el.attach);
    if (t && !t.isDeleted) {
      const tb = { x: t.x, y: t.y, w: t.w || 0, h: t.h || 0 };
      const cx = el.x + el.w / 2, cy = el.y + el.h / 2;
      const px = Math.max(tb.x, Math.min(cx, tb.x + tb.w)), py = Math.max(tb.y, Math.min(cy, tb.y + tb.h));
      const sx = Math.max(el.x, Math.min(px, el.x + el.w)), sy = Math.max(el.y, Math.min(py, el.y + el.h));
      svgEl('path', { d: `M${sx} ${sy}L${px} ${py}`, stroke: '#FF9500', 'stroke-width': 1.6, fill: 'none' }, node);
      svgEl('circle', { cx: px, cy: py, r: 3.5, fill: '#FF9500' }, node);
    }
  }
  for (const b of st.behind || []) svgEl('path', { d: b, ...common, fill }, node);
  if (el.rackU) svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 3, ...common, fill, 'stroke-width': 1.5 }, node);
  else if (st.rx && !sharp && /^M[\d.-]+ [\d.-]+H[\d.-]+V[\d.-]+H[\d.-]+Z$/.test(st.d)) svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: st.rx, ...common, fill, 'stroke-width': st.strokeWidth || common['stroke-width'] }, node);
  else svgEl('path', { d: st.d, ...common, fill, 'stroke-width': st.strokeWidth || common['stroke-width'], 'stroke-dasharray': st.dashed && !el.dashed ? '8 6' : common['stroke-dasharray'] }, node);
  // a device in a rack is drawn as its front panel: a status light and ports on the left, the name on the right
  if (el.rackU) {
    const k = Math.min(el.h * 0.5, 8);
    svgEl('circle', { cx: el.x + 10, cy: el.y + el.h / 2, r: Math.max(2, k / 2.2), fill: '#34C759' }, node);
    const ports = Math.max(0, Math.min(8, Math.floor((el.w * 0.36 - 16) / 9)));
    for (let i = 0; i < ports; i++) svgEl('rect', { x: el.x + 20 + i * 9, y: el.y + el.h / 2 - k / 2, width: 6, height: k, rx: 1, fill: 'none', stroke: common.stroke, 'stroke-width': 1 }, node);
  } else if (st.extra) svgEl('path', { d: st.extra, fill: 'none', stroke: common.stroke, 'stroke-width': st.extraWidth || Math.max(1.2, (el.strokeWidth || 2) * 0.8), 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'stroke-dasharray': st.extraDash ? '6 5' : null }, node);
  if (st.dot) svgEl('path', { d: st.dot, fill: common.stroke }, node);
  if (st.badge && !el.rackU) { const t = svgEl('text', { x: el.x + el.w / 2, y: el.y + el.h * 0.84, 'text-anchor': 'middle', 'font-size': Math.max(9, Math.min(16, el.w * 0.2)), 'font-weight': 800, 'font-family': FONT_STACK.ipop, fill: '#0A84FF' }, node); t.textContent = st.badge; }
  if (st.rack) drawRackSlots(node, el, pal);
  if (st.org) drawOrgAvatar(node, el, pal, opts);
  // VLAN colour on a network device: a small tag under the icon
  const vlan = (el.data || []).find(d => /^vlan$/i.test(d.name || ''))?.value;
  if (vlan && /^net/.test(el.type)) { const c = vlanColor(vlan); svgEl('rect', { x: el.x + el.w - 8, y: el.y - 4, width: 14, height: 14, rx: 4, fill: c, stroke: pal.board, 'stroke-width': 2 }, node); }
  // data graphics: progress bar and status icon
  const gr = opts.graphics?.(el);
  if (gr?.bar != null) {
    svgEl('rect', { x: el.x + 10, y: el.y + el.h - 10, width: el.w - 20, height: 5, rx: 2.5, fill: pal === PALETTE.dark ? 'rgba(255,255,255,.14)' : 'rgba(0,0,0,.08)' }, node);
    svgEl('rect', { x: el.x + 10, y: el.y + el.h - 10, width: Math.max(0, (el.w - 20) * Math.min(1, gr.bar)), height: 5, rx: 2.5, fill: gr.color }, node);
  }
  if (gr?.icon) {
    svgEl('circle', { cx: el.x + el.w - 2, cy: el.y + 2, r: 10, fill: gr.color, stroke: pal.board, 'stroke-width': 2 }, node);
    const t = svgEl('text', { x: el.x + el.w - 2, y: el.y + 6, 'text-anchor': 'middle', 'font-size': 11, 'font-weight': 800, 'font-family': FONT_STACK.ipop, fill: '#fff' }, node);
    t.textContent = gr.icon;
  }
  // ERD tables: name in the header, fields one per line
  if (el.type === 'erTable' && !opts.editingId?.includes?.(el.id) && opts.editingId !== el.id) {
    const [name, ...fields] = String(el.text || 'Table').split('\n');
    const tc = el.textColor ? inkFor(el.textColor, pal) : inkOnFill(el.fill, pal) || inkFor(null, pal);
    const t = svgEl('text', { x: el.x + el.w / 2, y: el.y + Math.min(32, el.h * 0.3) / 2 + 5, 'text-anchor': 'middle', 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK[el.font || 'ipop'], fill: tc }, node);
    t.textContent = name;
    fields.slice(0, Math.floor((el.h - 40) / 20)).forEach((f, i) => { const ft = svgEl('text', { x: el.x + 12, y: el.y + Math.min(32, el.h * 0.3) + 20 + i * 20, 'font-size': 12.5, 'font-family': FONT_STACK.mono, fill: tc }, node); ft.textContent = f; });
  } else if ((el.type === 'umlClass' || el.type === 'umlInterface') && opts.editingId !== el.id) {
    // name, then fields; a line with -- starts the methods
    const tc = el.textColor ? inkFor(el.textColor, pal) : inkOnFill(el.fill, pal) || inkFor(null, pal);
    const [name, ...rest] = String(el.text || (el.type === 'umlInterface' ? 'Interface' : 'Class')).split('\n');
    const head = st.head;
    if (el.type === 'umlInterface') { const k = svgEl('text', { x: el.x + el.w / 2, y: el.y + 16, 'text-anchor': 'middle', 'font-size': 11, 'font-style': 'italic', 'font-family': FONT_STACK.ipop, fill: tc, opacity: 0.7 }, node); k.textContent = '«interface»'; }
    const t = svgEl('text', { x: el.x + el.w / 2, y: el.y + head - 10, 'text-anchor': 'middle', 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK[el.font || 'ipop'], fill: tc }, node);
    t.textContent = name;
    let y = el.y + head + 18;
    for (const line of rest) {
      if (y > el.y + el.h - 4) break;
      if (/^\s*--+\s*$/.test(line)) { svgEl('path', { d: `M${el.x} ${y - 10}H${el.x + el.w}`, stroke: common.stroke, 'stroke-width': 1.2 }, node); y += 8; continue; }
      const ft = svgEl('text', { x: el.x + 12, y, 'font-size': 12.5, 'font-family': FONT_STACK.mono, fill: tc }, node); ft.textContent = line; y += 19;
    }
  } else if (el.type === 'orgCard' && opts.editingId !== el.id) {
    const tc = el.textColor ? inkFor(el.textColor, pal) : inkOnFill(el.fill, pal) || inkFor(null, pal);
    const box = stencilTextBox(el.type, el.x, el.y, el.w, el.h);
    const [name, ...more] = String(el.text || 'Name').split('\n');
    const nt = svgEl('text', { x: box.x, y: el.y + el.h / 2 - (more.length ? 3 : -5), 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: tc }, node);
    nt.textContent = name.length > 26 ? name.slice(0, 25) + '…' : name;
    more.slice(0, 2).forEach((l, i) => { const m = svgEl('text', { x: box.x, y: el.y + el.h / 2 + 15 + i * 15, 'font-size': 11.5, 'font-family': FONT_STACK.ipop, fill: tc, opacity: 0.65 }, node); m.textContent = l.length > 32 ? l.slice(0, 31) + '…' : l; });
  } else if (el.type === 'groupBox' || el.type === 'listBox' || el.type === 'fpRoom' || el.type === 'umlPackage' || el.type === 'rack') {
    const above = el.type === 'rack';
    const t = svgEl('text', { x: el.x + 12, y: above ? el.y - 10 : el.y + (el.type === 'umlPackage' ? 17 : 22), 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: pal.ink2 }, node);
    t.textContent = el.title || el.text || { groupBox: 'Group', listBox: 'List', fpRoom: 'Room', umlPackage: 'Package', rack: 'Rack' }[el.type];
  } else if (el.text && opts.editingId !== el.id) {
    // a device in a rack shows its name inside, on the right (there is no room below)
    const box0 = el.rackU ? { x: el.x + el.w * 0.42, y: el.y + 1, w: el.w * 0.55, h: Math.max(8, el.h - 2) } : stencilTextBox(el.type, el.x, el.y, el.w, el.h);
    // a progress bar along the bottom gets its own room
    const box = gr?.bar != null && !box0.below ? { ...box0, h: Math.max(14, box0.h - 10) } : box0;
    const size = el.size || (el.rackU ? 11 : box.below ? 13 : 15);
    // data fields under the title (Visio shape data), only when there is room
    const data = (el.data || []).filter(d => d && d.value);
    const color = el.textColor ? inkFor(el.textColor, pal) : (!box.below && inkOnFill(el.fill || st.fill, pal)) || inkFor(null, pal);
    if (data.length && !box.below) {
      const lines = data.map(d => (d.name ? `${d.name}: ${d.value}` : d.value)).slice(0, 3);
      const titleBox = { ...box, h: Math.max(18, box.h - lines.length * 15) };
      const lt = layoutText(el.text, titleBox, { size, font: el.font || 'ipop', weight: 600 });
      drawTextLines(node, lt.lines, titleBox, { ...lt, font: el.font || 'ipop', color, weight: 600, align: ALIGN[el.align || 'center'] || 'middle' });
      lines.forEach((l, i) => { const t = svgEl('text', { x: box.x + box.w / 2, y: titleBox.y + titleBox.h + 12 + i * 15, 'text-anchor': 'middle', 'font-size': 11.5, 'font-family': FONT_STACK.ipop, fill: color, opacity: 0.68 }, node); t.textContent = l.length > 34 ? l.slice(0, 33) + '…' : l; });
    } else if (hasFormatting(el.text) || (el.align && el.align !== 'center')) drawRichText(node, el.text, box, { size, font: el.font || 'ipop', weight: 500, color, align: ALIGN[el.align || 'center'] });
    else {
      const lt = layoutText(el.text, box, { size, font: el.font || 'ipop', weight: 500 });
      drawTextLines(node, lt.lines, box, { ...lt, font: el.font || 'ipop', color, weight: 500, valign: box.below ? 'top' : 'middle' });
    }
  }
  // a status in the shape data colours a strip on the left
  const status = (el.data || []).find(d => /status/i.test(d.name || ''))?.value;
  if (status && !gr && !STENCIL[el.type]?.cat?.match(/Network|Timeline|Floor plan/)) {
    const c = /done|selesai|complete|ok/i.test(status) ? '#34C759' : /block|late|stuck|terlambat/i.test(status) ? '#FF3B30' : /progress|doing|proses/i.test(status) ? '#FF9500' : '#8E8E93';
    svgEl('rect', { x: el.x + 1, y: el.y + 8, width: 4, height: Math.max(8, el.h - 16), rx: 2, fill: c }, node);
  }
  const step = opts.steps?.get?.(el.id);
  if (step) {
    svgEl('circle', { cx: el.x, cy: el.y, r: 11, fill: '#0A84FF', stroke: pal.board, 'stroke-width': 2 }, node);
    const t = svgEl('text', { x: el.x, y: el.y + 4, 'text-anchor': 'middle', 'font-size': 11.5, 'font-weight': 700, 'font-family': FONT_STACK.ipop, fill: '#fff' }, node);
    t.textContent = String(step);
  }
}

/// VLAN colours: the usual office VLANs have fixed colours, any other number gets a stable one.
const VLAN_COLORS = { 1: '#8E8E93', 10: '#0A84FF', 20: '#34C759', 30: '#FF9500', 40: '#AF52DE', 50: '#FF2D55', 60: '#30B0C7', 99: '#A2845E' };
export function vlanColor(v) {
  const n = parseInt(String(v).replace(/\D+/g, ''), 10);
  if (!Number.isFinite(n)) return '#8E8E93';
  if (VLAN_COLORS[n]) return VLAN_COLORS[n];
  const list = Object.values(VLAN_COLORS).slice(1);
  return list[n % list.length];
}

/// The numbered U slots of a 19" rack (U1 at the bottom).
function drawRackSlots(node, el, pal) {
  const s = rackSlots(el);
  const dark = pal === PALETTE.dark;
  for (let u = 1; u <= s.units; u++) {
    const b = s.slot(u);
    svgEl('rect', { x: b.x, y: b.y + 0.5, width: b.w, height: Math.max(1, b.h - 1), fill: dark ? 'rgba(255,255,255,.04)' : 'rgba(0,0,0,.025)', stroke: dark ? 'rgba(255,255,255,.08)' : 'rgba(0,0,0,.06)' }, node);
    if (s.uh >= 9) { const t = svgEl('text', { x: el.x + s.side - 6, y: b.y + b.h / 2 + 3.5, 'text-anchor': 'end', 'font-size': Math.min(10, s.uh * 0.7), 'font-family': FONT_STACK.mono, fill: pal.ink3 }, node); t.textContent = String(u); }
  }
  // rails
  for (const x of [el.x + s.side - 2, el.x + el.w - s.side + 2]) svgEl('path', { d: `M${x} ${el.y + s.pad}V${el.y + el.h - s.pad}`, stroke: pal.ink3, 'stroke-width': 1.4 }, node);
}

/// The photo square (or initials) of a person card.
function drawOrgAvatar(node, el, pal, opts) {
  const k = el.h - 16, x = el.x + 8, y = el.y + 8;
  const url = el.photo ? opts.files?.get(`ph:${el.photo}`) : null;
  if (url) {
    const cid = `wbo-${el.id}`;
    const cp = svgEl('clipPath', { id: cid }, node);
    svgEl('rect', { x, y, width: k, height: k, rx: 10 }, cp);
    svgEl('image', { href: url, x, y, width: k, height: k, preserveAspectRatio: 'xMidYMid slice', 'clip-path': `url(#${cid})` }, node);
    return;
  }
  const name = String(el.text || '').split('\n')[0].trim();
  const initials = name.split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase() || '?';
  const colors = ['#0A84FF', '#5856D6', '#AF52DE', '#FF2D55', '#FF9500', '#34C759', '#30B0C7', '#A2845E'];
  let hsh = 0; for (const ch of name) hsh = (hsh * 31 + ch.charCodeAt(0)) >>> 0;
  svgEl('rect', { x, y, width: k, height: k, rx: 10, fill: colors[hsh % colors.length] }, node);
  const t = svgEl('text', { x: x + k / 2, y: y + k / 2 + 5, 'text-anchor': 'middle', 'font-size': Math.min(16, k * 0.36), 'font-weight': 700, 'font-family': FONT_STACK.ipop, fill: '#fff' }, node);
  t.textContent = initials;
}

/// A swimlane: lanes with a header strip each; shapes inside are separate elements drawn above it.
function drawSwimlane(node, el, pal, opts) {
  const lanes = lanesOf(el);
  const laneFill = i => (i % 2 ? pal.board : (pal === PALETTE.dark ? 'rgba(255,255,255,.035)' : 'rgba(10,60,120,.035)'));
  const headFill = pal === PALETTE.dark ? 'rgba(255,255,255,.07)' : 'rgba(10,60,120,.07)';
  lanes.forEach((l, i) => {
    svgEl('rect', { x: l.box.x, y: l.box.y, width: l.box.w, height: l.box.h, fill: laneFill(i) }, node);
    svgEl('rect', { x: l.header.x, y: l.header.y, width: l.header.w, height: l.header.h, fill: headFill, 'data-lane': l.id }, node);
    const t = svgEl('text', l.vertical
      ? { x: l.header.x + l.header.w / 2, y: l.header.y + l.header.h / 2 + 5, 'text-anchor': 'middle', 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: pal.ink2 }
      : { x: l.header.x + 14, y: l.header.y + l.header.h / 2 + 5, 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: pal.ink2 }, node);
    t.textContent = l.name || `Lane ${i + 1}`;
    if (i) svgEl('path', { d: l.vertical ? `M${l.box.x} ${l.box.y}V${l.box.y + l.box.h}` : `M${l.box.x} ${l.box.y}H${l.box.x + l.box.w}`, stroke: pal.frameLine, 'stroke-width': 1.2 }, node);
  });
  svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: 6, fill: 'none', stroke: pal.frameLine, 'stroke-width': 1.4 }, node);
  if (el.title) { const t = svgEl('text', { x: el.x + 2, y: el.y - 10, 'font-size': 14, 'font-weight': 650, 'font-family': FONT_STACK.ipop, fill: pal.ink3 }, node); t.textContent = el.title; }
}

/// Shared <defs>: arrow heads that take the connector's colour, a soft shadow for stickies.
export function defs(svg) {
  const d = svgEl('defs', {}, svg);
  const head = (id, orient) => {
    const m = svgEl('marker', { id, viewBox: '0 0 10 10', refX: 8.5, refY: 5, markerWidth: 7, markerHeight: 7, orient, markerUnits: 'strokeWidth' }, d);
    svgEl('path', { d: 'M1 1L9 5L1 9', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, m);
  };
  head('wb-head', 'auto'); head('wb-head-start', 'auto-start-reverse');
  // draw.io / Visio heads: open, filled, diamond, crow's foot (many), one
  const mark = (id, orient, w, hgt, ref, draw) => { const m = svgEl('marker', { id, viewBox: `0 0 ${w} ${hgt}`, refX: ref, refY: hgt / 2, markerWidth: w * 0.75, markerHeight: hgt * 0.75, orient, markerUnits: 'strokeWidth' }, d); draw(m); };
  for (const [suffix, orient] of [['', 'auto'], ['-s', 'auto-start-reverse']]) {
    mark(`wb-open${suffix}`, orient, 10, 10, 9, m => svgEl('path', { d: 'M1 1L9 5L1 9', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.3, 'stroke-linejoin': 'round' }, m));
    mark(`wb-fill${suffix}`, orient, 10, 10, 9, m => svgEl('path', { d: 'M1 1L9 5L1 9Z', fill: 'currentColor' }, m));
    mark(`wb-dia${suffix}`, orient, 14, 10, 13, m => svgEl('path', { d: 'M1 5L7 1L13 5L7 9Z', fill: 'var(--wb-board, #fff)', stroke: 'currentColor', 'stroke-width': 1.2 }, m));
    mark(`wb-crow${suffix}`, orient, 12, 12, 11, m => svgEl('path', { d: 'M11 6L1 1M11 6L1 11M11 6H1M5 1V11', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.2 }, m));
    mark(`wb-one${suffix}`, orient, 12, 12, 11, m => svgEl('path', { d: 'M11 6H1M5 1V11M8 1V11', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.2 }, m));
  }
  const sh = svgEl('filter', { id: 'wb-shadow', x: '-20%', y: '-20%', width: '150%', height: '160%' }, d);
  svgEl('feDropShadow', { dx: 0, dy: 3, stdDeviation: 3, 'flood-opacity': 0.18 }, sh);
  const dot = svgEl('marker', { id: 'wb-dot', viewBox: '0 0 10 10', refX: 5, refY: 5, markerWidth: 5, markerHeight: 5, markerUnits: 'strokeWidth' }, d);
  svgEl('circle', { cx: 5, cy: 5, r: 4, fill: 'currentColor' }, dot);
  const f = svgEl('filter', { id: 'wb-soft', x: '-20%', y: '-20%', width: '140%', height: '140%' }, d);
  svgEl('feGaussianBlur', { stdDeviation: 3 }, f);
  // neon pen: a soft glow around the line
  const glow = svgEl('filter', { id: 'wb-glow', x: '-50%', y: '-50%', width: '200%', height: '200%' }, d);
  svgEl('feGaussianBlur', { stdDeviation: 4 }, glow);
  // crayon pen: grain that eats small holes into the stroke (fixed seed, so it looks the same for everyone)
  const cr = svgEl('filter', { id: 'wb-crayon', x: '-10%', y: '-10%', width: '120%', height: '120%' }, d);
  svgEl('feTurbulence', { type: 'fractalNoise', baseFrequency: 0.9, numOctaves: 2, seed: 7, result: 'n' }, cr);
  svgEl('feColorMatrix', { in: 'n', type: 'matrix', values: '0 0 0 0 0  0 0 0 0 0  0 0 0 0 0  0 0 0 -2.2 1.55', result: 'm' }, cr);
  svgEl('feComposite', { in: 'SourceGraphic', in2: 'm', operator: 'in' }, cr);
  return d;
}

/// Draws every visible element, frames first then by z. Returns the world group.
export function drawAll(world, els, get, opts) {
  for (const el of drawOrder(els.filter(e => !e.isDeleted && !e.hide))) drawElement(world, el, get, opts);
}

/// A standalone SVG string of the given elements (for SVG export and for rasterising to PNG).
export function toSvg(els, { theme = 'light', background = true, padding = 32, files, cardInfo } = {}) {
  const live = els.filter(e => !e.isDeleted);
  if (!live.length) return null;
  const byId = new Map(live.map(e => [e.id, e])), get = id => byId.get(id);
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
  for (const e of live) { const b = bounds(e, get); x1 = Math.min(x1, b.x); y1 = Math.min(y1, b.y - (e.type === 'frame' ? 26 : 0)); x2 = Math.max(x2, b.x + b.w); y2 = Math.max(y2, b.y + b.h); }
  const w = x2 - x1 + padding * 2, h = y2 - y1 + padding * 2;
  const svg = svgEl('svg', { xmlns: NS, width: Math.ceil(w), height: Math.ceil(h), viewBox: `${x1 - padding} ${y1 - padding} ${w} ${h}` });
  defs(svg);
  if (background) svgEl('rect', { x: x1 - padding, y: y1 - padding, width: w, height: h, fill: theme === 'dark' ? '#161618' : '#ffffff' }, svg);
  const world = svgEl('g', {}, svg);
  drawAll(world, live, get, { theme, files, cardInfo });
  return { svg, width: w, height: h };
}

/// PNG blob of the given elements. Pictures on the board are embedded first (an <image> pointing at a blob URL would be blank
/// once rasterised from a data URL), so `files` should map fileId -> data URL when the board has pictures.
export async function toPng(els, { scale = 2, theme = 'light', files, background = true, cardInfo } = {}) {
  const out = toSvg(els, { theme, files, background, cardInfo });
  if (!out) return null;
  const xml = new XMLSerializer().serializeToString(out.svg);
  const url = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(xml)}`;
  const img = new Image();
  img.decoding = 'async';
  await new Promise((ok, fail) => { img.onload = ok; img.onerror = () => fail(new Error('Could not draw the board as a picture')); img.src = url; });
  const maxSide = 8000;
  const s = Math.min(scale, maxSide / out.width, maxSide / out.height);
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(1, Math.round(out.width * s)); canvas.height = Math.max(1, Math.round(out.height * s));
  const ctx = canvas.getContext('2d');
  ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
  return await new Promise(ok => canvas.toBlob(ok, 'image/png'));
}

/// A one-page PDF holding a PNG (no library: a minimal PDF with one JPEG-free image XObject in Flate-less DCT is not possible from
/// PNG without decoding, so the PNG is re-encoded to JPEG through a canvas and embedded with DCTDecode).
export async function pngToPdf(png) {
  const bmp = await createImageBitmap(png);
  const c = document.createElement('canvas'); c.width = bmp.width; c.height = bmp.height;
  const ctx = c.getContext('2d'); ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, c.width, c.height); ctx.drawImage(bmp, 0, 0);
  const jpg = new Uint8Array(await (await new Promise(ok => c.toBlob(ok, 'image/jpeg', 0.92))).arrayBuffer());
  // page in points: fit A4 landscape or portrait to the picture's shape
  const landscape = c.width >= c.height;
  const pw = landscape ? 842 : 595, ph = landscape ? 595 : 842, m = 24;
  const s = Math.min((pw - m * 2) / c.width, (ph - m * 2) / c.height);
  const w = c.width * s, h = c.height * s, x = (pw - w) / 2, y = (ph - h) / 2;
  const enc = new TextEncoder();
  const parts = [], offsets = [];
  let len = 0;
  const push = b => { const u = typeof b === 'string' ? enc.encode(b) : b; parts.push(u); len += u.length; };
  const obj = (n, body) => { offsets[n] = len; push(`${n} 0 obj\n`); for (const b of [].concat(body)) push(b); push('\nendobj\n'); };
  push('%PDF-1.4\n%\xE2\xE3\xCF\xD3\n');
  obj(1, '<< /Type /Catalog /Pages 2 0 R >>');
  obj(2, '<< /Type /Pages /Kids [3 0 R] /Count 1 >>');
  obj(3, `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${pw} ${ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>`);
  obj(4, [`<< /Type /XObject /Subtype /Image /Width ${c.width} /Height ${c.height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpg.length} >>\nstream\n`, jpg, '\nendstream']);
  const content = `q ${w.toFixed(2)} 0 0 ${h.toFixed(2)} ${x.toFixed(2)} ${y.toFixed(2)} cm /Im0 Do Q`;
  obj(5, `<< /Length ${content.length} >>\nstream\n${content}\nendstream`);
  const xref = len;
  push(`xref\n0 6\n0000000000 65535 f \n${[1, 2, 3, 4, 5].map(n => `${String(offsets[n]).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF`);
  return new Blob(parts, { type: 'application/pdf' });
}
