// Drawing elements as SVG. The same code draws the live board, the minimap thumbnail and exports, so what is exported is
// exactly what is on screen. Clean ClickUp-like shapes; only the pen looks hand-drawn (perfect-freehand).
import { getStroke } from 'perfect-freehand';
import { connectorEnds, connectorPath, connectorMid, drawOrder, wrapLines, bounds } from './model.js';
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
    sticky: { yellow: '#FFF3A3', green: '#C9F2C7', blue: '#CFE6FF', pink: '#FFD3E1', purple: '#E6DAFF', orange: '#FFDDB8' },
    stickyInk: '#1D1D1F',
  },
  dark: {
    ink: '#F5F5F7', ink2: '#D1D1D6', ink3: '#8E8E93', shapeFill: '#1C1C1E', frameFill: 'rgba(255,255,255,.035)', frameLine: 'rgba(255,255,255,.2)',
    connector: '#A1A1A6', sel: '#409CFF', board: '#141416',
    sticky: { yellow: '#5A5220', green: '#244A2A', blue: '#1F3B5A', pink: '#5A2638', purple: '#3A2D5A', orange: '#5A3A1C' },
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
  }[kind] || { size: size * 1.6, thinning: 0.5 };
  const outline = getStroke(points.map(p => [p[0], p[1], p[2] ?? 0.5]), { smoothing: 0.5, streamline: 0.45, simulatePressure: kind === 'marker' ? false : simulatePressure, ...o });
  if (!outline.length) return '';
  let d = `M${outline[0][0].toFixed(2)} ${outline[0][1].toFixed(2)}`;
  for (let i = 1; i < outline.length; i++) {
    const [x0, y0] = outline[i - 1], [x1, y1] = outline[i];
    d += `Q${x0.toFixed(2)} ${y0.toFixed(2)} ${((x0 + x1) / 2).toFixed(2)} ${((y0 + y1) / 2).toFixed(2)}`;
  }
  return d + 'Z';
}

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
      } else if (el.straight) {
        let d = '';
        el.points.forEach((p, i) => { d += `${i ? 'L' : 'M'}${p[0]} ${p[1]}`; });
        svgEl('path', { d, fill: 'none', stroke: color, 'stroke-width': el.width || 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, tg);
      } else {
        svgEl('path', { d: strokeOutline(el.points, el.width || 3, { simulatePressure: !!el.simulate, kind: el.tool === 'pen' || el.tool === 'marker' ? el.tool : 'legacy' }), fill: color }, tg);
      }
      break;
    }
    case 'connector': {
      const [a, b] = connectorEnds(el, get);
      if (!a || !b) break;
      const color = inkFor(el.stroke, { ...pal, ink: pal.connector }) || pal.connector;
      const route = el.route || 'straight';
      svgEl('path', {
        d: connectorPath(a, b, route), fill: 'none', stroke: color, 'stroke-width': el.strokeWidth || 2, 'stroke-linejoin': 'round', 'stroke-linecap': 'round',
        'stroke-dasharray': el.dashed ? '6 5' : null,
        'marker-end': (el.endHead ?? 'arrow') === 'arrow' ? 'url(#wb-head)' : (el.endHead === 'dot' ? 'url(#wb-dot)' : null),
        'marker-start': el.startHead === 'arrow' ? 'url(#wb-head-start)' : (el.startHead === 'dot' ? 'url(#wb-dot)' : null),
        style: `color:${color}`,
      }, node);
      if (el.label) {
        const m = connectorMid(a, b, route), w = measure(el.label, 13) + 14;
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
    default: {
      // shapes
      const stroke = inkFor(el.stroke, pal), fill = el.fill === 'none' ? 'none' : (el.fill || pal.shapeFill), sw = el.strokeWidth || 2;
      const common = { fill, stroke, 'stroke-width': sw, 'stroke-linejoin': 'round' };
      if (el.type === 'rect') svgEl('rect', { x: el.x, y: el.y, width: el.w, height: el.h, rx: el.rounded === false ? 2 : Math.min(12, el.w / 4, el.h / 4), ...common }, node);
      else if (el.type === 'ellipse') svgEl('ellipse', { cx: el.x + el.w / 2, cy: el.y + el.h / 2, rx: el.w / 2, ry: el.h / 2, ...common }, node);
      else svgEl('path', { d: shapePath(el.type, el.x, el.y, el.w, el.h), ...common }, node);
      if (el.text && !editing) {
        const box = textBox(el);
        if (hasFormatting(el.text) || (el.align && el.align !== 'center')) drawRichText(node, el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500, color: inkFor(el.textColor, pal), align: ALIGN[el.align || 'center'] });
        else {
          const lt = layoutText(el.text, box, { size: el.size || 16, font: el.font || 'ipop', weight: 500 });
          drawTextLines(node, lt.lines, box, { ...lt, font: el.font || 'ipop', color: inkFor(el.textColor, pal), weight: 500 });
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

/// Shared <defs>: arrow heads that take the connector's colour, a soft shadow for stickies.
export function defs(svg) {
  const d = svgEl('defs', {}, svg);
  const head = (id, orient) => {
    const m = svgEl('marker', { id, viewBox: '0 0 10 10', refX: 8.5, refY: 5, markerWidth: 7, markerHeight: 7, orient, markerUnits: 'strokeWidth' }, d);
    svgEl('path', { d: 'M1 1L9 5L1 9', fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6, 'stroke-linecap': 'round', 'stroke-linejoin': 'round' }, m);
  };
  head('wb-head', 'auto'); head('wb-head-start', 'auto-start-reverse');
  const dot = svgEl('marker', { id: 'wb-dot', viewBox: '0 0 10 10', refX: 5, refY: 5, markerWidth: 5, markerHeight: 5, markerUnits: 'strokeWidth' }, d);
  svgEl('circle', { cx: 5, cy: 5, r: 4, fill: 'currentColor' }, dot);
  const f = svgEl('filter', { id: 'wb-soft', x: '-20%', y: '-20%', width: '140%', height: '140%' }, d);
  svgEl('feGaussianBlur', { stdDeviation: 3 }, f);
  return d;
}

/// Draws every visible element, frames first then by z. Returns the world group.
export function drawAll(world, els, get, opts) {
  for (const el of drawOrder(els.filter(e => !e.isDeleted))) drawElement(world, el, get, opts);
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
