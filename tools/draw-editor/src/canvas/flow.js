// Flowchart logic (round 3): step numbers, the diagram check, "tidy up flow", where to put the next shape, connector
// routing with fixed sides and waypoints, and line jumps. Pure (no DOM), so it is tested without a browser.
import { FLOW_TYPES, STEP_TYPES, DECISION_TYPES, isTerminator, isEnd, STENCIL } from './stencils.js';

const isNode = e => e && !e.isDeleted && e.type !== 'connector' && e.type !== 'path' && e.type !== 'frame' && e.type !== 'swimlane' && e.type !== 'groupBox';
export const hasFlowchart = els => els.some(e => !e.isDeleted && FLOW_TYPES.has(e.type));

/// The graph of shapes joined by connectors (only connectors attached at both ends count).
export function graph(els) {
  const nodes = els.filter(isNode);
  const ids = new Set(nodes.map(n => n.id));
  const edges = els.filter(e => !e.isDeleted && e.type === 'connector' && ids.has(e.from?.id) && ids.has(e.to?.id))
    .map(e => ({ id: e.id, from: e.from.id, to: e.to.id, label: e.label || '' }));
  const out = new Map(nodes.map(n => [n.id, []])), inn = new Map(nodes.map(n => [n.id, []]));
  for (const e of edges) { out.get(e.from).push(e); inn.get(e.to).push(e); }
  return { nodes, edges, out, inn, byId: new Map(nodes.map(n => [n.id, n])) };
}

/// Where the flow starts: a Start terminator, else a flow shape with nothing coming in (top-left first).
export function starts(g) {
  const flow = g.nodes.filter(n => FLOW_TYPES.has(n.type) || n.type === 'bpmnTask');
  const marked = flow.filter(n => isTerminator(n) && !isEnd(n) && !g.inn.get(n.id).length);
  if (marked.length) return marked;
  return flow.filter(n => !g.inn.get(n.id).length && g.out.get(n.id).length).sort((a, b) => a.y - b.y || a.x - b.x);
}

/// Shapes in flow order (breadth first from the starts; "Yes" before "No" so the main path is numbered first).
export function flowOrder(g) {
  const seen = new Set(), order = [];
  const q = [...starts(g)];
  const rank = l => (/^(yes|ya|ok|approved|true)$/i.test(l) ? 0 : /^(no|tidak|reject|rejected|false)$/i.test(l) ? 2 : 1);
  while (q.length) {
    const n = q.shift();
    if (!n || seen.has(n.id)) continue;
    seen.add(n.id); order.push(n);
    for (const e of [...g.out.get(n.id)].sort((a, b) => rank(a.label) - rank(b.label))) q.push(g.byId.get(e.to));
  }
  return order;
}

/// Step numbers for the "do something" shapes, following the flow. Map(id -> number).
export function stepNumbers(els) {
  // each separate flowchart on the board starts again at 1
  const g = graph(els), nums = new Map(), seen = new Set();
  for (const start of starts(g)) {
    if (seen.has(start.id)) continue;
    const sub = { ...g, nodes: g.nodes };
    const order = flowOrder({ ...sub, nodes: g.nodes.filter(n => n.id === start.id || !seen.has(n.id)) }).filter(n => !seen.has(n.id));
    // flowOrder starts from every start; keep only what this start reaches
    const reach = new Set([start.id]), q = [start.id];
    while (q.length) { const id = q.shift(); for (const e of g.out.get(id) || []) if (!reach.has(e.to)) { reach.add(e.to); q.push(e.to); } }
    let n = 0;
    for (const s of order) { if (!reach.has(s.id)) continue; seen.add(s.id); if (STEP_TYPES.has(s.type) && !nums.has(s.id)) nums.set(s.id, ++n); }
  }
  return nums;
}

/// Problems a flowchart commonly has. Each: { level: 'bad'|'warn', text, ids, fix? }.
export function checkFlow(els) {
  const g = graph(els);
  const flow = g.nodes.filter(n => FLOW_TYPES.has(n.type));
  if (!flow.length) return [];
  const out = [];
  const label = n => `"${String(n.text || STENCIL[n.type]?.label || 'Shape').split('\n')[0].slice(0, 40)}"`;
  if (!flow.some(n => isTerminator(n) && !isEnd(n))) out.push({ level: 'bad', text: 'There is no Start. Every flowchart begins with one.', ids: [], fix: 'start' });
  if (!flow.some(n => isEnd(n))) out.push({ level: 'warn', text: 'There is no End. Show where the flow stops.', ids: [], fix: 'end' });
  for (const d of flow.filter(n => DECISION_TYPES.has(n.type))) {
    const outs = g.out.get(d.id);
    if (outs.length < 2) out.push({ level: 'warn', text: `${label(d)} needs two ways out, for example Yes and No.`, ids: [d.id], fix: 'branches' });
    else if (outs.filter(e => !e.label).length) out.push({ level: 'warn', text: `Name the lines out of ${label(d)} (Yes / No) so people know which way to go.`, ids: [d.id] });
  }
  for (const n of flow) {
    if (!g.out.get(n.id).length && !g.inn.get(n.id).length && flow.length > 1) out.push({ level: 'warn', text: `${label(n)} is not connected to anything.`, ids: [n.id] });
    else if (!g.out.get(n.id).length && !isEnd(n) && !isTerminator(n) && flow.length > 1) out.push({ level: 'warn', text: `The flow stops at ${label(n)}. Add the next step or an End.`, ids: [n.id], fix: 'next' });
  }
  for (const n of flow.filter(isEnd)) if (g.out.get(n.id).length) out.push({ level: 'warn', text: `${label(n)} is an End but the flow carries on after it.`, ids: [n.id] });
  return out;
}

/// What to suggest under a selected flow shape: [{ kind, label, type?, branch? }].
export function nextHints(el, els) {
  if (!el || !FLOW_TYPES.has(el.type)) return [];
  const g = graph(els);
  const outs = g.out.get(el.id) || [];
  if (isEnd(el)) return [{ kind: 'info', label: 'The flow ends here.' }];
  if (DECISION_TYPES.has(el.type)) {
    const has = re => outs.some(e => re.test(e.label));
    const hints = [];
    if (!has(/^(yes|ya)$/i)) hints.push({ kind: 'add', label: '+ Yes', type: 'process', branch: 'Yes', dir: 'b' });
    if (!has(/^(no|tidak)$/i)) hints.push({ kind: 'add', label: '+ No', type: 'process', branch: 'No', dir: 'r' });
    return hints.length ? [{ kind: 'info', label: 'A decision needs two ways out.' }, ...hints] : [{ kind: 'info', label: 'Both branches are there.' }];
  }
  if (isTerminator(el)) return outs.length ? [] : [{ kind: 'info', label: 'Next: the first step.' }, { kind: 'add', label: '+ Process', type: 'process', dir: 'b' }];
  if (outs.length) return [];
  return [{ kind: 'info', label: 'Next:' }, { kind: 'add', label: '+ Process', type: 'process', dir: 'b' }, { kind: 'add', label: '+ Decision', type: 'decision', dir: 'b' }, { kind: 'add', label: '+ End', type: 'terminator', text: 'End', dir: 'b' }];
}

/// A box for a new shape next to `src` in direction dir ('t'|'r'|'b'|'l'), moved along until it overlaps nothing.
export function placeNext(src, dir, size, others, gap = 64) {
  let x = src.x + src.w / 2 - size.w / 2, y = src.y + src.h / 2 - size.h / 2;
  if (dir === 'b') y = src.y + src.h + gap;
  if (dir === 't') y = src.y - gap - size.h;
  if (dir === 'r') x = src.x + src.w + gap;
  if (dir === 'l') x = src.x - gap - size.w;
  const pad = 16;
  const hits = () => others.some(o => !o.isDeleted && isNode(o) && o.id !== src.id && x < o.x + o.w + pad && x + size.w + pad > o.x && y < o.y + o.h + pad && y + size.h + pad > o.y);
  for (let i = 0; i < 30 && hits(); i++) { if (dir === 'b' || dir === 't') x += size.w + 48; else y += size.h + 40; }
  return { x: Math.round(x), y: Math.round(y), w: size.w, h: size.h };
}

/// "Tidy up flow": layered layout. dir 'TB' (top to bottom) or 'LR'. Returns Map(id -> {x, y}) for the shapes that move.
/// Layers follow the longest path from the start (loops back are ignored); within a layer the order keeps branches apart.
export function layoutFlow(els, { dir = 'TB', gapMain = 70, gapCross = 60, origin } = {}) {
  const g = graph(els);
  const flow = g.nodes;
  if (!flow.length) return new Map();
  const order = flowOrder(g);
  const index = new Map(order.map((n, i) => [n.id, i]));
  const level = new Map();
  for (const n of order) {
    if (!level.has(n.id)) level.set(n.id, 0);
    for (const e of g.out.get(n.id)) {
      if ((index.get(e.to) ?? -1) <= (index.get(n.id) ?? -1)) continue;     // a loop back up
      level.set(e.to, Math.max(level.get(e.to) ?? 0, level.get(n.id) + 1));
    }
  }
  const loose = flow.filter(n => !level.has(n.id));
  const maxL = Math.max(0, ...level.values());
  loose.forEach(n => level.set(n.id, maxL + 1));
  const layers = new Map();
  for (const n of [...order, ...loose]) { const l = level.get(n.id); if (!layers.has(l)) layers.set(l, []); if (!layers.get(l).includes(n)) layers.get(l).push(n); }
  const tb = dir !== 'LR';
  const ox = origin?.x ?? Math.min(...flow.map(n => n.x)), oy = origin?.y ?? Math.min(...flow.map(n => n.y));
  const pos = new Map();
  const widest = Math.max(...[...layers.values()].map(row => row.reduce((s, n) => s + (tb ? n.w : n.h), 0) + (row.length - 1) * gapCross));
  let main = tb ? oy : ox;
  for (const l of [...layers.keys()].sort((a, b) => a - b)) {
    const row = layers.get(l);
    const span = row.reduce((s, n) => s + (tb ? n.w : n.h), 0) + (row.length - 1) * gapCross;
    const thick = Math.max(...row.map(n => (tb ? n.h : n.w)));
    let cross = (tb ? ox : oy) + (widest - span) / 2;
    for (const n of row) {
      const p = tb ? { x: Math.round(cross), y: Math.round(main + (thick - n.h) / 2) } : { x: Math.round(main + (thick - n.w) / 2), y: Math.round(cross) };
      if (p.x !== n.x || p.y !== n.y) pos.set(n.id, p);
      cross += (tb ? n.w : n.h) + gapCross;
    }
    main += thick + gapMain;
  }
  return pos;
}

// ── Connector routing ────────────────────────────────────────────────────────

/// The point on a box's side: side 't'|'r'|'b'|'l'.
export function portPoint(b, side) {
  if (side === 't') return { x: b.x + b.w / 2, y: b.y };
  if (side === 'b') return { x: b.x + b.w / 2, y: b.y + b.h };
  if (side === 'l') return { x: b.x, y: b.y + b.h / 2 };
  return { x: b.x + b.w, y: b.y + b.h / 2 };
}
/// The side of `b` facing point p.
export function facingSide(b, p) {
  const cx = b.x + b.w / 2, cy = b.y + b.h / 2, dx = p.x - cx, dy = p.y - cy;
  if (Math.abs(dx) * b.h > Math.abs(dy) * b.w) return dx > 0 ? 'r' : 'l';
  return dy > 0 ? 'b' : 't';
}
/// The side nearest to a point (for attaching a dragged end to a port).
export function nearestSide(b, p) {
  const d = { t: Math.abs(p.y - b.y), b: Math.abs(p.y - (b.y + b.h)), l: Math.abs(p.x - b.x), r: Math.abs(p.x - (b.x + b.w)) };
  return Object.entries(d).sort((a, c) => a[1] - c[1])[0][0];
}
const out = (p, side, k) => ({ x: p.x + (side === 'r' ? k : side === 'l' ? -k : 0), y: p.y + (side === 'b' ? k : side === 't' ? -k : 0) });
const segHitsBox = (a, b, o, pad = 6) => {
  const x1 = Math.min(a.x, b.x), x2 = Math.max(a.x, b.x), y1 = Math.min(a.y, b.y), y2 = Math.max(a.y, b.y);
  return x2 > o.x - pad && x1 < o.x + o.w + pad && y2 > o.y - pad && y1 < o.y + o.h + pad;
};

/// An elbow route between two side ports that avoids the given obstacle boxes when it can.
/// Returns the list of points (first = start port, last = end port).
export function elbowRoute(a, sa, b, sb, obstacles = [], stub = 22) {
  const p1 = out(a, sa, stub), p2 = out(b, sb, stub);
  const horizA = sa === 'l' || sa === 'r';
  const candidates = [];
  // one bend
  candidates.push([a, p1, horizA ? { x: p2.x, y: p1.y } : { x: p1.x, y: p2.y }, p2, b]);
  candidates.push([a, p1, horizA ? { x: p1.x, y: p2.y } : { x: p2.x, y: p1.y }, p2, b]);
  // two bends through the middle
  const mx = (p1.x + p2.x) / 2, my = (p1.y + p2.y) / 2;
  candidates.push([a, p1, { x: mx, y: p1.y }, { x: mx, y: p2.y }, p2, b]);
  candidates.push([a, p1, { x: p1.x, y: my }, { x: p2.x, y: my }, p2, b]);
  // around obstacles: go past the furthest one on each side
  if (obstacles.length) {
    const top = Math.min(...obstacles.map(o => o.y)) - 30, bottom = Math.max(...obstacles.map(o => o.y + o.h)) + 30;
    const left = Math.min(...obstacles.map(o => o.x)) - 30, right = Math.max(...obstacles.map(o => o.x + o.w)) + 30;
    for (const yy of [top, bottom]) candidates.push([a, p1, { x: p1.x, y: yy }, { x: p2.x, y: yy }, p2, b]);
    for (const xx of [left, right]) candidates.push([a, p1, { x: xx, y: p1.y }, { x: xx, y: p2.y }, p2, b]);
  }
  const clean = pts => pts.filter((p, i) => i === 0 || Math.abs(p.x - pts[i - 1].x) > 0.5 || Math.abs(p.y - pts[i - 1].y) > 0.5);
  const cost = pts => {
    let len = 0, hits = 0;
    for (let i = 1; i < pts.length; i++) {
      len += Math.abs(pts[i].x - pts[i - 1].x) + Math.abs(pts[i].y - pts[i - 1].y);
      if (i > 1 && i < pts.length - 1) for (const o of obstacles) if (segHitsBox(pts[i - 1], pts[i], o)) hits++;
    }
    return hits * 100000 + len + pts.length * 20;
  };
  return clean(candidates.map(clean).sort((x, y) => cost(x) - cost(y))[0]);
}

/// Orthogonal path through waypoints the person placed (each waypoint is a corner).
export function waypointRoute(a, b, waypoints) {
  const pts = [a];
  let cur = a;
  for (const w of [...waypoints, b]) {
    if (Math.abs(cur.x - w.x) > 0.5 && Math.abs(cur.y - w.y) > 0.5) pts.push({ x: w.x, y: cur.y });
    pts.push(w); cur = w;
  }
  return pts;
}

/// SVG path through points, with small jumps where a horizontal segment crosses a vertical segment of another line
/// (`others` = list of point lists already drawn). Rounded corners keep elbows soft.
export function pathWithJumps(pts, others = [], { jump = 6, round = 6 } = {}) {
  const vsegs = [];
  for (const o of others) for (let i = 1; i < o.length; i++) if (Math.abs(o[i].x - o[i - 1].x) < 0.5) vsegs.push([o[i - 1], o[i]]);
  let d = `M${pts[0].x.toFixed(1)} ${pts[0].y.toFixed(1)}`;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    const horizontal = Math.abs(a.y - b.y) < 0.5;
    if (horizontal) {
      const dir = Math.sign(b.x - a.x) || 1;
      const xs = vsegs.filter(([p, q]) => (p.x - a.x) * dir > jump * 2 && (b.x - p.x) * dir > jump * 2 && a.y > Math.min(p.y, q.y) + 1 && a.y < Math.max(p.y, q.y) - 1).map(([p]) => p.x).sort((u, v) => (u - v) * dir);
      for (const x of xs) d += `L${(x - jump * dir).toFixed(1)} ${a.y.toFixed(1)}A${jump} ${jump} 0 0 ${dir > 0 ? 1 : 0} ${(x + jump * dir).toFixed(1)} ${a.y.toFixed(1)}`;
    }
    const next = pts[i + 1];
    if (next && round > 0 && i < pts.length - 1) {
      const l1 = Math.hypot(b.x - a.x, b.y - a.y), l2 = Math.hypot(next.x - b.x, next.y - b.y);
      const r = Math.min(round, l1 / 2, l2 / 2);
      if (r > 0.5) {
        const p = { x: b.x - (b.x - a.x) / l1 * r, y: b.y - (b.y - a.y) / l1 * r };
        const q = { x: b.x + (next.x - b.x) / l2 * r, y: b.y + (next.y - b.y) / l2 * r };
        d += `L${p.x.toFixed(1)} ${p.y.toFixed(1)}Q${b.x.toFixed(1)} ${b.y.toFixed(1)} ${q.x.toFixed(1)} ${q.y.toFixed(1)}`;
        continue;
      }
    }
    d += `L${b.x.toFixed(1)} ${b.y.toFixed(1)}`;
  }
  return d;
}

/// A point at fraction t (0..1) along a polyline, for labels the person slides along the line.
export function pointAlong(pts, t = 0.5) {
  const lens = []; let total = 0;
  for (let i = 1; i < pts.length; i++) { const l = Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); lens.push(l); total += l; }
  let want = total * Math.max(0, Math.min(1, t));
  for (let i = 1; i < pts.length; i++) {
    if (want <= lens[i - 1] || i === pts.length - 1) { const k = lens[i - 1] ? want / lens[i - 1] : 0; return { x: pts[i - 1].x + (pts[i].x - pts[i - 1].x) * k, y: pts[i - 1].y + (pts[i].y - pts[i - 1].y) * k }; }
    want -= lens[i - 1];
  }
  return pts[0];
}
/// The fraction along a polyline nearest to a point (dragging a label).
export function fractionAt(pts, p) {
  let best = { d: Infinity, t: 0.5 }, total = 0; const lens = [];
  for (let i = 1; i < pts.length; i++) { const l = Math.hypot(pts[i].x - pts[i - 1].x, pts[i].y - pts[i - 1].y); lens.push(l); total += l; }
  let acc = 0;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i], l = lens[i - 1] || 1e-9;
    const k = Math.max(0, Math.min(1, ((p.x - a.x) * (b.x - a.x) + (p.y - a.y) * (b.y - a.y)) / (l * l)));
    const d = Math.hypot(a.x + (b.x - a.x) * k - p.x, a.y + (b.y - a.y) * k - p.y);
    if (d < best.d) best = { d, t: (acc + k * l) / (total || 1) };
    acc += l;
  }
  return best.t;
}
