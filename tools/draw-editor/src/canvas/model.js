// iPop's own whiteboard: the element model. Pure (no DOM), so it is tested without a browser.
//
// An element is plain JSON stored as ONE entry of the scene's Y.Map (same storage as before: "draw:<id>", one key per element).
// Every element carries `version` (grows on every change) and `versionNonce` (random tie-break), so two people editing the same
// element converge on one value exactly like the Excalidraw scenes did (scene.js `isNewer`).
//
// Types:
//   rect | ellipse | diamond | triangle | star | cloud | arrowShape | cylinder | bubble   shapes  {x,y,w,h, text, stroke, fill, ...}
//   sticky                                                                               {x,y,w,h, text, color}
//   text                                                                                 {x,y,w,h, text, size, font, stroke}
//   path                                                                                 {x,y,points:[[dx,dy,pressure]], stroke, width, highlight}
//   connector                                                                            {from:{id|x,y}, to:{id|x,y}, route, startHead, endHead, label, stroke}
//   frame                                                                                {x,y,w,h, name}
//   image                                                                                {x,y,w,h, fileId}
// Common: id, type, version, versionNonce, isDeleted, z (fractional order string), locked, frameId, rot.

export const SHAPES = ['rect', 'ellipse', 'diamond', 'triangle', 'star', 'cloud', 'arrowShape', 'cylinder', 'bubble'];
export const BOXED = new Set([...SHAPES, 'sticky', 'text', 'frame', 'image', 'file']);
export const STICKY_COLORS = ['yellow', 'green', 'blue', 'pink', 'purple', 'orange'];
export const FONTS = ['ipop', 'hand', 'mono'];

const rnd = () => Math.floor(Math.random() * 2 ** 31);
export const newId = () => (globalThis.crypto?.randomUUID ? globalThis.crypto.randomUUID() : `e${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`);

/// A new element with its bookkeeping filled in.
export function create(type, props = {}) {
  return { id: newId(), type, version: 1, versionNonce: rnd(), isDeleted: false, z: props.z ?? 'a0', ...props };
}

/// A changed copy: version goes up, a fresh nonce is drawn. Never mutate a stored element in place.
export function bump(el, changes = {}) {
  return { ...el, ...changes, version: (el.version || 0) + 1, versionNonce: rnd() };
}

/// Whether `incoming` should replace `current` (same id): higher version wins, lower nonce breaks a tie, the same on every client.
export function isNewer(current, incoming) {
  if (!current) return true;
  if (incoming.version !== current.version) return incoming.version > current.version;
  return incoming.versionNonce < current.versionNonce;
}

/// Light check that a stored value is something we can draw: a corrupt entry must never break the board.
export function isElement(v) {
  return !!v && typeof v === 'object' && typeof v.id === 'string' && typeof v.type === 'string' && Number.isFinite(v.version)
    && (v.type === 'connector' || v.type === 'path' ? true : Number.isFinite(v.x) && Number.isFinite(v.y));
}

// ── Ordering ───────────────────────────────────────────────────────────────
// `z` is a fractional index string: an element can be put between two others without renumbering anything else, which keeps
// "bring forward" a single-key write (two people reordering at once do not fight over a shared list).
const DIGITS = '0123456789abcdefghijklmnopqrstuvwxyz';
/// A key that sorts strictly between `a` and `b` (either may be null = open end).
export function zBetween(a, b) {
  a = a || ''; b = b || '';
  if (b && a >= b) throw new Error('zBetween: a must sort before b');
  let out = '';
  for (let i = 0; ; i++) {
    const da = i < a.length ? DIGITS.indexOf(a[i]) : 0;
    const db = b && i < b.length ? DIGITS.indexOf(b[i]) : DIGITS.length;
    if (db - da > 1) return out + DIGITS[Math.floor((da + db) / 2)];
    out += DIGITS[da];
  }
}
export const byZ = (p, q) => (p.z < q.z ? -1 : p.z > q.z ? 1 : p.id < q.id ? -1 : p.id > q.id ? 1 : 0);
/// Frames always sit underneath; everything else follows its z key.
export function drawOrder(els) {
  return [...els].sort((p, q) => (p.type === 'frame') !== (q.type === 'frame') ? (p.type === 'frame' ? -1 : 1) : byZ(p, q));
}
export const topZ = els => els.reduce((m, e) => (e.z > m ? e.z : m), '');
export const bottomZ = els => els.reduce((m, e) => (!m || e.z < m ? e.z : m), '');

// ── Geometry ───────────────────────────────────────────────────────────────

/// Axis-aligned bounds in board units. Connectors need the scene to resolve their ends, so they take `get(id)`.
export function bounds(el, get = () => null) {
  if (el.type === 'path') {
    if (!el.points?.length) return { x: el.x, y: el.y, w: 0, h: 0 };
    let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
    for (const [dx, dy] of el.points) { x1 = Math.min(x1, dx); y1 = Math.min(y1, dy); x2 = Math.max(x2, dx); y2 = Math.max(y2, dy); }
    const pad = (el.width || 3) / 2;
    return { x: el.x + x1 - pad, y: el.y + y1 - pad, w: x2 - x1 + pad * 2, h: y2 - y1 + pad * 2 };
  }
  if (el.type === 'connector') {
    const [a, b] = connectorEnds(el, get);
    if (!a || !b) return { x: 0, y: 0, w: 0, h: 0 };
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(a.x - b.x), h: Math.abs(a.y - b.y) };
  }
  return { x: el.x, y: el.y, w: el.w || 0, h: el.h || 0 };
}
export const centre = (el, get) => { const b = bounds(el, get); return { x: b.x + b.w / 2, y: b.y + b.h / 2 }; };

export function unionBounds(els, get) {
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity;
  for (const e of els) { const b = bounds(e, get); x1 = Math.min(x1, b.x); y1 = Math.min(y1, b.y); x2 = Math.max(x2, b.x + b.w); y2 = Math.max(y2, b.y + b.h); }
  return Number.isFinite(x1) ? { x: x1, y: y1, w: x2 - x1, h: y2 - y1 } : null;
}

/// Where a line from the shape's centre towards (tx, ty) leaves its outline, `gap` units further out.
/// Rectangles, ellipses and diamonds are exact; the other shapes use their box (close enough for a connector to touch them).
export function edgePoint(el, tx, ty, gap = 0) {
  const w = el.w || 0, h = el.h || 0, cx = el.x + w / 2, cy = el.y + h / 2;
  const dx = tx - cx, dy = ty - cy, len = Math.hypot(dx, dy);
  if (!len) return { x: cx, y: cy };
  const hw = w / 2 || 1e-9, hh = h / 2 || 1e-9;
  let t;
  if (el.type === 'ellipse') t = 1 / Math.hypot(dx / hw, dy / hh);
  else if (el.type === 'diamond') t = 1 / (Math.abs(dx) / hw + Math.abs(dy) / hh);
  else t = Math.min(hw / (Math.abs(dx) || 1e-9), hh / (Math.abs(dy) || 1e-9));
  const reach = t * len + gap;
  return { x: cx + (dx / len) * reach, y: cy + (dy / len) * reach };
}

/// The two end points of a connector. An end bound to a shape touches that shape's outline facing the other end;
/// an end whose shape was deleted stays where it last was (`x`/`y` remembered on the end).
export function connectorEnds(c, get) {
  const target = end => { if (end?.id) { const e = get(end.id); if (e && !e.isDeleted) return e; } return null; };
  const ta = target(c.from), tb = target(c.to);
  const free = (end, t) => t ? centre(t, get) : (Number.isFinite(end?.x) ? { x: end.x, y: end.y } : null);
  const ca = free(c.from, ta), cb = free(c.to, tb);
  if (!ca || !cb) return [null, null];
  const a = ta ? edgePoint(ta, cb.x, cb.y, 6) : ca;
  const b = tb ? edgePoint(tb, ca.x, ca.y, 6) : cb;
  return [a, b];
}

/// SVG path data for a connector between a and b: straight, elbow (two right angles) or curved.
export function connectorPath(a, b, route = 'straight') {
  if (route === 'elbow') {
    const horizontal = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y);
    if (horizontal) { const mx = (a.x + b.x) / 2; return `M${a.x} ${a.y}H${mx}V${b.y}H${b.x}`; }
    const my = (a.y + b.y) / 2; return `M${a.x} ${a.y}V${my}H${b.x}V${b.y}`;
  }
  if (route === 'curved') {
    const horizontal = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y);
    if (horizontal) { const mx = (a.x + b.x) / 2; return `M${a.x} ${a.y}C${mx} ${a.y} ${mx} ${b.y} ${b.x} ${b.y}`; }
    const my = (a.y + b.y) / 2; return `M${a.x} ${a.y}C${a.x} ${my} ${b.x} ${my} ${b.x} ${b.y}`;
  }
  return `M${a.x} ${a.y}L${b.x} ${b.y}`;
}

/// Middle of a connector, where its label sits.
export function connectorMid(a, b, route = 'straight') {
  if (route === 'elbow') {
    const horizontal = Math.abs(b.x - a.x) >= Math.abs(b.y - a.y);
    return horizontal ? { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 } : { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
  }
  return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
}

// ── Hit testing ────────────────────────────────────────────────────────────

const distToSegment = (px, py, ax, ay, bx, by) => {
  const dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
  const t = l2 ? Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / l2)) : 0;
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy));
};

/// Whether the board point (px, py) touches the element. `tol` is in board units (callers pass screen px / zoom).
export function hits(el, px, py, get, tol = 6) {
  if (el.type === 'path') {
    const pts = el.points || [], r = (el.width || 3) / 2 + tol;
    if (pts.length === 1) return Math.hypot(px - el.x - pts[0][0], py - el.y - pts[0][1]) <= r;
    for (let i = 1; i < pts.length; i++) if (distToSegment(px, py, el.x + pts[i - 1][0], el.y + pts[i - 1][1], el.x + pts[i][0], el.y + pts[i][1]) <= r) return true;
    return false;
  }
  if (el.type === 'connector') {
    const [a, b] = connectorEnds(el, get);
    if (!a || !b) return false;
    if ((el.route || 'straight') === 'straight') return distToSegment(px, py, a.x, a.y, b.x, b.y) <= tol + 2;
    const bb = bounds(el, get);
    // elbow / curved: close to the bend lines is good enough for picking
    const mx = (a.x + b.x) / 2;
    return px >= bb.x - tol && px <= bb.x + bb.w + tol && py >= bb.y - tol && py <= bb.y + bb.h + tol
      && (distToSegment(px, py, a.x, a.y, mx, a.y) <= tol + 2 || distToSegment(px, py, mx, a.y, mx, b.y) <= tol + 2 || distToSegment(px, py, mx, b.y, b.x, b.y) <= tol + 2
        || distToSegment(px, py, a.x, a.y, a.x, (a.y + b.y) / 2) <= tol + 2 || distToSegment(px, py, a.x, (a.y + b.y) / 2, b.x, (a.y + b.y) / 2) <= tol + 2 || distToSegment(px, py, b.x, (a.y + b.y) / 2, b.x, b.y) <= tol + 2);
  }
  const b = bounds(el, get);
  if (el.type === 'frame') {
    // a frame is picked by its title strip or its border, so clicks inside it reach what is in it (or start a selection box)
    const inTitle = px >= b.x && px <= b.x + Math.max(120, Math.min(b.w, 260)) && py >= b.y - 26 && py <= b.y;
    const onEdge = px >= b.x - tol && px <= b.x + b.w + tol && py >= b.y - tol && py <= b.y + b.h + tol
      && (px <= b.x + tol || px >= b.x + b.w - tol || py <= b.y + tol || py >= b.y + b.h - tol);
    return inTitle || onEdge;
  }
  if (el.type === 'ellipse') {
    const rx = b.w / 2 + tol, ry = b.h / 2 + tol, cx = b.x + b.w / 2, cy = b.y + b.h / 2;
    return ((px - cx) / rx) ** 2 + ((py - cy) / ry) ** 2 <= 1;
  }
  if (el.type === 'diamond') {
    const cx = b.x + b.w / 2, cy = b.y + b.h / 2;
    return Math.abs(px - cx) / (b.w / 2 + tol) + Math.abs(py - cy) / (b.h / 2 + tol) <= 1;
  }
  return px >= b.x - tol && px <= b.x + b.w + tol && py >= b.y - tol && py <= b.y + b.h + tol;
}

/// The top-most element under the point, or null. Locked elements are found too (so they can be unlocked) but report it.
export function hitTest(els, px, py, get, tol) {
  const ordered = drawOrder(els.filter(e => !e.isDeleted)).reverse();
  return ordered.find(e => hits(e, px, py, get, tol)) || null;
}

/// Elements whose bounds sit entirely inside the box (selection by dragging a rectangle).
export function insideBox(els, box, get) {
  return els.filter(e => {
    if (e.isDeleted) return false;
    const b = bounds(e, get);
    return b.x >= box.x && b.y >= box.y && b.x + b.w <= box.x + box.w && b.y + b.h <= box.y + box.h;
  });
}

/// The frame an element sits in (its box fully inside the frame's), the smallest one when frames overlap.
export function frameOf(el, els, get) {
  if (el.type === 'frame' || el.type === 'connector') return null;
  const b = bounds(el, get);
  let best = null;
  for (const f of els) {
    if (f.type !== 'frame' || f.isDeleted || f.id === el.id) continue;
    if (b.x >= f.x && b.y >= f.y && b.x + b.w <= f.x + f.w && b.y + b.h <= f.y + f.h && (!best || f.w * f.h < best.w * best.h)) best = f;
  }
  return best;
}

// ── Moving and resizing ────────────────────────────────────────────────────

/// A moved copy (new version). Paths move by their origin; connectors are moved through their free ends only.
export function moved(el, dx, dy) {
  if (el.type === 'connector') {
    const shift = end => (end && !end.id && Number.isFinite(end.x) ? { ...end, x: end.x + dx, y: end.y + dy } : end);
    return bump(el, { from: shift(el.from), to: shift(el.to) });
  }
  return bump(el, { x: el.x + dx, y: el.y + dy });
}

/// Resize from a handle. `handle` is one of nw n ne e se s sw w. Returns {x,y,w,h}; never smaller than `min`.
/// `keepRatio` (Shift, or images by default) keeps the shape's proportions.
export function resizeBox(box, handle, dx, dy, { min = 8, keepRatio = false } = {}) {
  let { x, y, w, h } = box;
  const ratio = box.w / (box.h || 1);
  if (handle.includes('e')) w += dx;
  if (handle.includes('s')) h += dy;
  if (handle.includes('w')) { x += dx; w -= dx; }
  if (handle.includes('n')) { y += dy; h -= dy; }
  if (keepRatio && /[ns]/.test(handle) && /[ew]/.test(handle)) {
    if (Math.abs(w / ratio) > Math.abs(h)) { const nh = w / ratio; if (handle.includes('n')) y += h - nh; h = nh; }
    else { const nw = h * ratio; if (handle.includes('w')) x += w - nw; w = nw; }
  }
  if (w < min) { if (handle.includes('w')) x -= min - w; w = min; }
  if (h < min) { if (handle.includes('n')) y -= min - h; h = min; }
  return { x, y, w, h };
}

/// Grid snap (board units).
export const snap = (v, grid = 8) => Math.round(v / grid) * grid;

/// Alignment guides: given the moving box and the other boxes, the dx/dy to apply so an edge or centre lines up (within `tol`),
/// and the guide lines to draw. Only the nearest match per axis is used.
export function guides(moving, others, tol = 5) {
  const xs = b => [b.x, b.x + b.w / 2, b.x + b.w], ys = b => [b.y, b.y + b.h / 2, b.y + b.h];
  let bestX = null, bestY = null;
  for (const o of others) {
    for (const mx of xs(moving)) for (const ox of xs(o)) { const d = ox - mx; if (Math.abs(d) <= tol && (!bestX || Math.abs(d) < Math.abs(bestX.d))) bestX = { d, at: ox, o }; }
    for (const my of ys(moving)) for (const oy of ys(o)) { const d = oy - my; if (Math.abs(d) <= tol && (!bestY || Math.abs(d) < Math.abs(bestY.d))) bestY = { d, at: oy, o }; }
  }
  const lines = [];
  if (bestX) lines.push({ x1: bestX.at, y1: Math.min(moving.y, bestX.o.y) - 16, x2: bestX.at, y2: Math.max(moving.y + moving.h, bestX.o.y + bestX.o.h) + 16 });
  if (bestY) lines.push({ x1: Math.min(moving.x, bestY.o.x) - 16, y1: bestY.at, x2: Math.max(moving.x + moving.w, bestY.o.x + bestY.o.w) + 16, y2: bestY.at });
  return { dx: bestX?.d ?? 0, dy: bestY?.d ?? 0, lines };
}

// ── Shape recognition ("make it neat") ─────────────────────────────────────

/// Looks at a finished pen stroke. A closed loop that is roughly round becomes an ellipse, one with corners a rectangle,
/// three corners a triangle; open strokes and scribbles return null. Points are [x, y] in board units.
export function recognise(points) {
  if (!points || points.length < 10) return null;
  let x1 = Infinity, y1 = Infinity, x2 = -Infinity, y2 = -Infinity, len = 0;
  for (let i = 0; i < points.length; i++) {
    const [x, y] = points[i];
    x1 = Math.min(x1, x); y1 = Math.min(y1, y); x2 = Math.max(x2, x); y2 = Math.max(y2, y);
    if (i) len += Math.hypot(x - points[i - 1][0], y - points[i - 1][1]);
  }
  const w = x2 - x1, h = y2 - y1;
  if (w < 24 || h < 24) return null;
  const first = points[0], last = points.at(-1);
  if (Math.hypot(first[0] - last[0], first[1] - last[1]) > Math.max(w, h) * 0.3) return null;   // not closed
  // a scribble goes round far more than once
  const perimeterEllipse = Math.PI * (3 * (w + h) / 2 - Math.sqrt((3 * w / 2 + h / 2) * (w / 2 + 3 * h / 2)));
  if (len > perimeterEllipse * 1.6) return null;
  const box = { x: x1, y: y1, w, h };
  // corners: sharp direction changes along a resampled stroke
  const step = len / 48, res = [points[0]];
  let acc = 0;
  for (let i = 1; i < points.length; i++) {
    acc += Math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1]);
    if (acc >= step) { res.push(points[i]); acc = 0; }
  }
  // The stroke is closed, so walk it as a loop: a corner where the pen started counts like any other.
  if (res.length > 1 && Math.hypot(res[0][0] - res.at(-1)[0], res[0][1] - res.at(-1)[1]) < step) res.pop();
  const n = res.length, at = i => res[((i % n) + n) % n];
  const turn = i => {
    const a = Math.atan2(at(i)[1] - at(i - 2)[1], at(i)[0] - at(i - 2)[0]);
    const b = Math.atan2(at(i + 2)[1] - at(i)[1], at(i + 2)[0] - at(i)[0]);
    let d = Math.abs(a - b); return d > Math.PI ? 2 * Math.PI - d : d;
  };
  // a corner is a sharp turn that is the strongest within 3 samples either side (one bend is counted once)
  let corners = 0;
  for (let i = 0; i < n; i++) {
    const d = turn(i);
    if (d <= 1.0) continue;
    let peak = true;
    for (let k = 1; k <= 3; k++) if (turn(i - k) > d || turn(i + k) >= d) { peak = false; break; }
    if (peak) corners++;
  }
  // how far the stroke stays from an ellipse fitted to the box
  const cx = x1 + w / 2, cy = y1 + h / 2;
  let dev = 0;
  for (const [x, y] of points) dev += Math.abs(Math.hypot((x - cx) / (w / 2), (y - cy) / (h / 2)) - 1);
  dev /= points.length;
  if (corners <= 1 && dev < 0.14) return { type: 'ellipse', ...box };
  if (corners === 2 || corners === 3) return { type: 'triangle', ...box };
  if (corners >= 3 && corners <= 5) return { type: 'rect', ...box };
  if (dev < 0.2) return { type: 'ellipse', ...box };
  return null;
}

// ── Text measuring (approximate, for wrapping inside shapes and stickies) ───

/// Splits text into lines that fit `maxWidth` at `size`; `measure(str)` returns a width (the canvas measures with the real font,
/// tests pass a simple per-character estimate).
export function wrapLines(text, maxWidth, measure) {
  const out = [];
  for (const para of String(text ?? '').split('\n')) {
    if (!para) { out.push(''); continue; }
    let line = '';
    for (const word of para.split(/(\s+)/)) {
      if (!word) continue;
      const next = line + word;
      if (measure(next.trimEnd()) <= maxWidth || !line.trim()) {
        // a single word longer than the line is broken by characters
        if (measure(next.trimEnd()) > maxWidth && !line.trim()) {
          let chunk = '';
          for (const ch of word) { if (measure(chunk + ch) > maxWidth && chunk) { out.push(chunk); chunk = ch; } else chunk += ch; }
          line = chunk;
        } else line = next;
      } else { out.push(line.trimEnd()); line = word.trimStart(); }
    }
    out.push(line.trimEnd());
  }
  return out;
}
