import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { convertScene, convertInto } from '../src/canvas/convert.js';
import { isElement } from '../src/canvas/model.js';

const ex = (o) => ({ version: 3, versionNonce: 7, isDeleted: false, strokeColor: '#1e1e1e', backgroundColor: 'transparent', strokeWidth: 2, ...o });

const OLD = [
  ex({ id: 'r1', type: 'rectangle', x: 10, y: 20, width: 190, height: 70, index: 'a1' }),
  ex({ id: 't1', type: 'text', x: 30, y: 40, width: 80, height: 25, text: 'Request', fontFamily: 5, fontSize: 18, containerId: 'r1', index: 'a2' }),
  ex({ id: 'd1', type: 'diamond', x: 300, y: 10, width: 120, height: 90, index: 'a3' }),
  ex({ id: 'a1', type: 'arrow', x: 200, y: 55, width: 100, height: 0, points: [[0, 0], [100, 0]], startBinding: { elementId: 'r1' }, endBinding: { elementId: 'd1' }, endArrowhead: 'arrow', index: 'a4' }),
  ex({ id: 's1', type: 'rectangle', x: 0, y: 200, width: 170, height: 120, backgroundColor: '#FFE27A', strokeColor: 'transparent', index: 'a5' }),
  ex({ id: 's1t', type: 'text', x: 0, y: 200, width: 100, height: 20, text: 'Went well', fontFamily: 5, containerId: 's1', index: 'a6' }),
  ex({ id: 'p1', type: 'freedraw', x: 500, y: 500, points: [[0, 0], [3, 4], [10, 12]], pressures: [0.2, 0.6, 0.9], strokeColor: '#e03131', index: 'a7' }),
  ex({ id: 'n1', type: 'text', x: 600, y: 50, width: 200, height: 50, text: 'Kevin', fontFamily: 5, fontSize: 36, index: 'a8' }),
  ex({ id: 'f1', type: 'frame', x: -20, y: 180, width: 400, height: 300, name: 'Went well', index: 'a0' }),
  ex({ id: 'gone', type: 'rectangle', x: 0, y: 0, width: 5, height: 5, isDeleted: true }),
  ex({ id: 'emb', type: 'embeddable', x: 0, y: 0, width: 5, height: 5 }),
  ex({ id: 'l3', type: 'line', x: 0, y: 900, points: [[0, 0], [50, 30], [100, 0]], index: 'a9' }),
];

test('shapes keep their place and size, and text inside a shape becomes the shape\'s own text in its handwriting font', () => {
  const out = convertScene(OLD), by = Object.fromEntries(out.map(e => [e.id, e]));
  assert.deepEqual([by.r1.type, by.r1.x, by.r1.y, by.r1.w, by.r1.h], ['rect', 10, 20, 190, 70]);
  assert.equal(by.r1.text, 'Request');
  assert.equal(by.r1.font, 'hand');
  assert.equal(by.t1, undefined, 'the bound text is merged, not duplicated');
  assert.equal(by.d1.type, 'diamond');
});

test('template stickies become real stickies; free text keeps its font and size', () => {
  const by = Object.fromEntries(convertScene(OLD).map(e => [e.id, e]));
  assert.equal(by.s1.type, 'sticky');
  assert.equal(by.s1.color, 'yellow');
  assert.equal(by.s1.text, 'Went well');
  assert.equal(by.n1.type, 'text');
  assert.equal(by.n1.font, 'hand');
  assert.equal(by.n1.size, 36);
});

test('pen strokes stay exactly as drawn (same points, colour, pressure)', () => {
  const by = Object.fromEntries(convertScene(OLD).map(e => [e.id, e]));
  assert.equal(by.p1.type, 'path');
  assert.deepEqual(by.p1.points, [[0, 0, 0.2], [3, 4, 0.6], [10, 12, 0.9]]);
  assert.equal(by.p1.stroke, '#e03131');
  assert.equal(by.p1.x, 500);
});

test('an attached arrow becomes a connector bound to both shapes; a free polyline keeps its shape', () => {
  const by = Object.fromEntries(convertScene(OLD).map(e => [e.id, e]));
  assert.equal(by.a1.type, 'connector');
  assert.equal(by.a1.from.id, 'r1');
  assert.equal(by.a1.to.id, 'd1');
  assert.equal(by.a1.endHead, 'arrow');
  assert.equal(by.l3.type, 'path');
  assert.equal(by.l3.points.length, 3);
});

test('frames keep their name; deleted and unknown elements are skipped; every result is a valid element', () => {
  const out = convertScene(OLD), by = Object.fromEntries(out.map(e => [e.id, e]));
  assert.equal(by.f1.name, 'Went well');
  assert.equal(by.gone, undefined);
  assert.equal(by.emb, undefined);
  assert.ok(out.every(isElement), JSON.stringify(out.filter(e => !isElement(e))));
});

test('conversion is deterministic: two people converting at once write identical values', () => {
  assert.deepEqual(convertScene(OLD), convertScene(structuredClone(OLD)));
});

test('z order follows the old board, so what was on top stays on top', () => {
  const out = convertScene(OLD);
  const z = id => out.find(e => e.id === id).z;
  assert.ok(z('r1') < z('a1') && z('a1') < z('n1'));
});

test('convertInto fills the new scene once; the old scene is never touched; a second run does nothing', () => {
  const doc = new Y.Doc();
  const oldMap = doc.getMap('draw:board'), newMap = doc.getMap('wb:board');
  OLD.forEach(e => oldMap.set(e.id, e));
  const n = convertInto(oldMap, newMap);
  assert.ok(n >= 8);
  assert.equal(oldMap.size, OLD.length, 'old scene kept as it was');
  assert.equal(convertInto(oldMap, newMap), 0);
  assert.equal(newMap.size, n);
});

test('two clients converting the same old board at the same time end up with one copy of each element', () => {
  const a = new Y.Doc(), b = new Y.Doc();
  for (const d of [a, b]) OLD.forEach(e => d.getMap('draw:x').set(e.id, e));
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  convertInto(a.getMap('draw:x'), a.getMap('wb:x'));
  convertInto(b.getMap('draw:x'), b.getMap('wb:x'));
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b));
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  assert.equal(a.getMap('wb:x').size, convertScene(OLD).length);
  assert.deepEqual(a.getMap('wb:x').toJSON(), b.getMap('wb:x').toJSON());
});

test('garbage in the old scene never throws', () => {
  assert.deepEqual(convertScene([null, 5, 'x', {}, { id: 'a' }]), []);
  assert.deepEqual(convertScene(null), []);
});
