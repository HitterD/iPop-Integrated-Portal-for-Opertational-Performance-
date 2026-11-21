import test from 'node:test';
import assert from 'node:assert/strict';
import {
  create, bump, isNewer, isElement, zBetween, drawOrder, bounds, edgePoint, connectorEnds, connectorPath, hits, hitTest,
  insideBox, frameOf, moved, resizeBox, guides, recognise, wrapLines, unionBounds,
} from '../src/canvas/model.js';

const scene = els => { const m = new Map(els.map(e => [e.id, e])); return id => m.get(id); };

test('new elements start at version 1 and every change raises the version with a fresh nonce', () => {
  const r = create('rect', { x: 0, y: 0, w: 10, h: 10 });
  assert.equal(r.version, 1);
  const r2 = bump(r, { x: 5 });
  assert.equal(r2.version, 2);
  assert.equal(r.x, 0, 'the original is never changed in place');
  assert.equal(r2.x, 5);
  assert.ok(isNewer(r, r2));
  assert.ok(!isNewer(r2, r));
});

test('same version: the lower nonce wins on every client', () => {
  const a = { version: 3, versionNonce: 10 }, b = { version: 3, versionNonce: 20 };
  assert.ok(isNewer(b, a));
  assert.ok(!isNewer(a, b));
});

test('corrupt or unknown entries are recognised so they are skipped instead of breaking the board', () => {
  assert.ok(isElement(create('rect', { x: 1, y: 2, w: 3, h: 4 })));
  assert.ok(isElement(create('connector', { from: { x: 0, y: 0 }, to: { x: 1, y: 1 } })));
  for (const bad of [null, 'x', {}, { id: 'a', type: 'rect', version: 1 }, { id: 1, type: 'rect', version: 1, x: 0, y: 0 }, { id: 'a', type: 'rect', version: NaN, x: 0, y: 0 }]) assert.ok(!isElement(bad));
});

test('z keys: always something in between, never collide, and sort the way they were asked', () => {
  const keys = [];
  let lo = null;
  for (let i = 0; i < 200; i++) { const k = zBetween(lo, null); keys.push(k); lo = k; }
  assert.deepEqual([...keys].sort(), keys, 'appending keeps order');
  assert.equal(new Set(keys).size, keys.length);
  const mid = zBetween('a0', 'a1');
  assert.ok('a0' < mid && mid < 'a1', mid);
  let a = 'a0', b = 'a1';
  for (let i = 0; i < 60; i++) { const m = zBetween(a, b); assert.ok(a < m && m < b, `${a} < ${m} < ${b}`); b = m; }
  assert.throws(() => zBetween('b', 'a'));
});

test('frames are always drawn underneath, the rest in z order', () => {
  const f = create('frame', { x: 0, y: 0, w: 100, h: 100, z: 'z9' });
  const r = create('rect', { x: 0, y: 0, w: 1, h: 1, z: 'a1' });
  const s = create('sticky', { x: 0, y: 0, w: 1, h: 1, z: 'a0' });
  assert.deepEqual(drawOrder([r, f, s]).map(e => e.type), ['frame', 'sticky', 'rect']);
});

test('the outline point is right for rectangles, ellipses and diamonds in every direction', () => {
  const rect = { type: 'rect', x: 0, y: 0, w: 100, h: 50 };
  assert.deepEqual(edgePoint(rect, 200, 25), { x: 100, y: 25 });
  assert.deepEqual(edgePoint(rect, 50, -100), { x: 50, y: 0 });
  const ell = { type: 'ellipse', x: 0, y: 0, w: 100, h: 100 };
  const p = edgePoint(ell, 150, 150);
  assert.ok(Math.abs(Math.hypot(p.x - 50, p.y - 50) - 50) < 1e-6);
  const dia = { type: 'diamond', x: 0, y: 0, w: 100, h: 100 };
  assert.deepEqual(edgePoint(dia, 50, 500), { x: 50, y: 100 });
  const q = edgePoint(dia, 100, 100);
  assert.ok(Math.abs(q.x - 75) < 1e-6 && Math.abs(q.y - 75) < 1e-6);
  assert.deepEqual(edgePoint(rect, 50, 25), { x: 50, y: 25 }, 'pointing at its own centre does not divide by zero');
});

test('a connector bound to two shapes touches both outlines and follows when a shape moves', () => {
  const a = create('rect', { x: 0, y: 0, w: 100, h: 50 });
  const b = create('rect', { x: 300, y: 0, w: 100, h: 50 });
  const c = create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'straight' });
  let [p, q] = connectorEnds(c, scene([a, b, c]));
  assert.equal(p.x, 106); assert.equal(q.x, 294);
  const b2 = moved(b, 0, 200);
  [p, q] = connectorEnds(c, scene([a, b2, c]));
  assert.ok(q.y < 225 && q.y >= 194, 'end follows the moved shape');
});

test('a connector whose shape was deleted keeps its last free point; with no point at all it is not drawn', () => {
  const a = create('rect', { x: 0, y: 0, w: 100, h: 50, isDeleted: true });
  const c = create('connector', { from: { id: a.id, x: 10, y: 10 }, to: { x: 200, y: 10 } });
  const [p, q] = connectorEnds(c, scene([a, c]));
  assert.deepEqual([p, q], [{ x: 10, y: 10 }, { x: 200, y: 10 }]);
  const lost = create('connector', { from: { id: 'gone' }, to: { x: 1, y: 1 } });
  assert.deepEqual(connectorEnds(lost, scene([lost])), [null, null]);
});

test('connector routes: straight is one line, elbow has right angles only, curved is a cubic', () => {
  const a = { x: 0, y: 0 }, b = { x: 100, y: 40 };
  assert.equal(connectorPath(a, b), 'M0 0L100 40');
  assert.equal(connectorPath(a, b, 'elbow'), 'M0 0H50V40H100');
  assert.equal(connectorPath({ x: 0, y: 0 }, { x: 10, y: 100 }, 'elbow'), 'M0 0V50H10V100');
  assert.match(connectorPath(a, b, 'curved'), /^M0 0C50 0 50 40 100 40$/);
});

test('hit testing: shapes by their outline area, paths by their line, frames only by title or border', () => {
  const r = create('rect', { x: 0, y: 0, w: 100, h: 100 });
  const e = create('ellipse', { x: 200, y: 0, w: 100, h: 100 });
  const p = create('path', { x: 0, y: 300, points: [[0, 0], [100, 0]], width: 4 });
  const f = create('frame', { x: 500, y: 0, w: 300, h: 300, name: 'Ideas' });
  const get = scene([r, e, p, f]);
  assert.ok(hits(r, 50, 50, get));
  assert.ok(hits(e, 250, 50, get));
  assert.ok(!hits(e, 205, 5, get, 2), 'the corner outside an ellipse is not the ellipse');
  assert.ok(hits(p, 50, 302, get));
  assert.ok(!hits(p, 50, 330, get));
  assert.ok(hits(f, 520, -10, get), 'frame title');
  assert.ok(hits(f, 501, 150, get), 'frame border');
  assert.ok(!hits(f, 650, 150, get), 'inside an empty frame is not the frame (so a selection box can start there)');
});

test('the top-most element wins a click; deleted elements are never picked', () => {
  const low = create('rect', { x: 0, y: 0, w: 100, h: 100, z: 'a0' });
  const high = create('sticky', { x: 50, y: 50, w: 100, h: 100, z: 'a5' });
  const gone = create('rect', { x: 0, y: 0, w: 200, h: 200, z: 'z0', isDeleted: true });
  const els = [low, high, gone];
  assert.equal(hitTest(els, 75, 75, scene(els)).id, high.id);
  assert.equal(hitTest(els, 10, 10, scene(els)).id, low.id);
  assert.equal(hitTest(els, 190, 190, scene(els)), null);
});

test('a selection box picks only what is fully inside it', () => {
  const a = create('rect', { x: 10, y: 10, w: 20, h: 20 });
  const b = create('rect', { x: 90, y: 90, w: 30, h: 30 });
  assert.deepEqual(insideBox([a, b], { x: 0, y: 0, w: 100, h: 100 }, scene([a, b])).map(e => e.id), [a.id]);
});

test('an element belongs to the smallest frame that fully contains it', () => {
  const big = create('frame', { x: 0, y: 0, w: 1000, h: 1000 });
  const small = create('frame', { x: 100, y: 100, w: 300, h: 300 });
  const s = create('sticky', { x: 150, y: 150, w: 100, h: 100 });
  const half = create('sticky', { x: 350, y: 350, w: 100, h: 100 });
  const els = [big, small, s, half];
  assert.equal(frameOf(s, els, scene(els)).id, small.id);
  assert.equal(frameOf(half, els, scene(els)).id, big.id);
  assert.equal(frameOf(small, els, scene(els)), null, 'frames do not nest into frames');
});

test('moving: boxes shift, path points stay relative, bound connector ends stay bound', () => {
  const p = create('path', { x: 10, y: 10, points: [[0, 0], [5, 5]] });
  const p2 = moved(p, 3, 4);
  assert.deepEqual([p2.x, p2.y, p2.points], [13, 14, [[0, 0], [5, 5]]]);
  const c = create('connector', { from: { id: 'x' }, to: { x: 10, y: 10 } });
  const c2 = moved(c, 5, 5);
  assert.deepEqual(c2.from, { id: 'x' });
  assert.deepEqual(c2.to, { x: 15, y: 15 });
});

test('resizing from each handle; never smaller than the minimum; Shift keeps the proportions', () => {
  const box = { x: 0, y: 0, w: 100, h: 50 };
  assert.deepEqual(resizeBox(box, 'se', 20, 10), { x: 0, y: 0, w: 120, h: 60 });
  assert.deepEqual(resizeBox(box, 'nw', 20, 10), { x: 20, y: 10, w: 80, h: 40 });
  assert.deepEqual(resizeBox(box, 'e', -500, 0), { x: 0, y: 0, w: 8, h: 50 });
  assert.deepEqual(resizeBox(box, 'w', 500, 0), { x: 92, y: 0, w: 8, h: 50 });
  const r = resizeBox(box, 'se', 100, 0, { keepRatio: true });
  assert.equal(r.w / r.h, 2);
});

test('alignment guides snap the nearest edge or centre, and only within the tolerance', () => {
  const moving = { x: 103, y: 300, w: 50, h: 50 };
  const other = { x: 100, y: 0, w: 50, h: 50 };
  const g = guides(moving, [other]);
  assert.equal(g.dx, -3);
  assert.equal(g.dy, 0);
  assert.equal(g.lines.length, 1);
  assert.equal(guides({ x: 133, y: 300, w: 50, h: 50 }, [other]).dx, 0, 'too far: no snap');
});

const circle = (cx, cy, r, n = 60, wobble = 0) => Array.from({ length: n + 1 }, (_, i) => { const a = (i / n) * Math.PI * 2; const rr = r * (1 + wobble * Math.sin(a * 5)); return [cx + rr * Math.cos(a), cy + rr * Math.sin(a)]; });
const square = (x, y, s, per = 15) => { const pts = []; const side = (x1, y1, x2, y2) => { for (let i = 0; i < per; i++) pts.push([x1 + (x2 - x1) * i / per, y1 + (y2 - y1) * i / per]); }; side(x, y, x + s, y); side(x + s, y, x + s, y + s); side(x + s, y + s, x, y + s); side(x, y + s, x, y); pts.push([x, y]); return pts; };

test('make it neat: a rough circle becomes an ellipse, a rough box a rectangle', () => {
  const c = recognise(circle(100, 100, 60, 60, 0.04));
  assert.equal(c?.type, 'ellipse');
  assert.ok(Math.abs(c.w - 120) < 15);
  const s = recognise(square(0, 0, 120));
  assert.equal(s?.type, 'rect');
  assert.equal(recognise(square(40, 40, 90, 22))?.type, 'rect', 'denser sampling');
  const tri = []; const side = (x1, y1, x2, y2) => { for (let i = 0; i < 20; i++) tri.push([x1 + (x2 - x1) * i / 20, y1 + (y2 - y1) * i / 20]); };
  side(60, 0, 120, 100); side(120, 100, 0, 100); side(0, 100, 60, 0); tri.push([60, 0]);
  assert.equal(recognise(tri)?.type, 'triangle');
});

test('make it neat stays quiet for open lines, tiny marks and scribbles', () => {
  assert.equal(recognise(Array.from({ length: 30 }, (_, i) => [i * 10, Math.sin(i) * 20])), null, 'open line');
  assert.equal(recognise(circle(0, 0, 6)), null, 'tiny');
  const scribble = [...circle(0, 0, 60), ...circle(0, 0, 55), ...circle(0, 0, 50)];
  assert.equal(recognise(scribble), null, 'goes round three times');
  assert.equal(recognise([[0, 0], [1, 1]]), null);
});

test('text wraps by words, keeps blank lines, and breaks a word longer than the line', () => {
  const measure = s => s.length * 10;
  assert.deepEqual(wrapLines('one two three four', 90, measure), ['one two', 'three', 'four']);
  assert.deepEqual(wrapLines('a\n\nb', 100, measure), ['a', '', 'b']);
  assert.deepEqual(wrapLines('abcdefghij', 40, measure), ['abcd', 'efgh', 'ij']);
  assert.deepEqual(wrapLines('', 100, measure), ['']);
});

test('bounds of a path include its pen width; union bounds cover everything', () => {
  const p = create('path', { x: 10, y: 10, points: [[0, 0], [10, 20]], width: 4 });
  assert.deepEqual(bounds(p), { x: 8, y: 8, w: 14, h: 24 });
  const r = create('rect', { x: 100, y: 100, w: 10, h: 10 });
  assert.deepEqual(unionBounds([p, r], () => null), { x: 8, y: 8, w: 102, h: 102 });
  assert.equal(unionBounds([], () => null), null);
});
