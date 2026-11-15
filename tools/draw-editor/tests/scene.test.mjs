import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { isNewer, pushElements, readElements, reassert, visible, compact, isElement } from '../src/scene.js';

const el = (id, version = 1, extra = {}) => ({ id, type: 'rectangle', version, versionNonce: version * 7 + id.length, isDeleted: false, index: id, x: 0, y: 0, ...extra });

// two peers connected by copying updates both ways
function pair() {
  const a = new Y.Doc(), b = new Y.Doc();
  a.on('update', u => Y.applyUpdate(b, u, 'remote'));
  b.on('update', u => Y.applyUpdate(a, u, 'remote'));
  return [a, b];
}

test('higher version wins; equal versions break the tie the same way on every client', () => {
  assert.equal(isNewer(el('a', 1), el('a', 2)), true);
  assert.equal(isNewer(el('a', 3), el('a', 2)), false);
  const x = { ...el('a', 2), versionNonce: 5 }, y = { ...el('a', 2), versionNonce: 9 };
  assert.notEqual(isNewer(x, y), isNewer(y, x));       // exactly one of them wins, from either side
  assert.equal(isNewer(undefined, el('a', 1)), true);
});

test('only changed elements are written to the shared map', () => {
  const doc = new Y.Doc(), m = doc.getMap('d'), prev = new Map();
  assert.equal(pushElements(m, [el('a'), el('b')], prev), 2);
  assert.equal(pushElements(m, [el('a'), el('b')], prev), 0);              // nothing changed
  assert.equal(pushElements(m, [el('a'), el('b', 2)], prev), 1);           // only b
  assert.equal(m.get('b').version, 2);
});

test('two people drawing at the same time both keep their strokes', () => {
  const [a, b] = pair();
  const ma = a.getMap('d'), mb = b.getMap('d');
  pushElements(ma, [el('stroke-a')], new Map());
  pushElements(mb, [el('stroke-b')], new Map());
  assert.deepEqual(readElements(ma).map(e => e.id), ['stroke-a', 'stroke-b']);
  assert.deepEqual(readElements(mb).map(e => e.id), ['stroke-a', 'stroke-b']);
});

test('offline edits merge on reconnect: different elements both survive, the same element converges to one value', () => {
  const a = new Y.Doc(), b = new Y.Doc();
  const ma = a.getMap('d'), mb = b.getMap('d');
  pushElements(ma, [el('shared', 1, { x: 1 })], new Map());
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  // now both go offline and edit
  pushElements(ma, [el('shared', 2, { x: 100 }), el('only-a')], new Map());
  pushElements(mb, [el('shared', 3, { x: 200 }), el('only-b')], new Map());
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b));
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  const ra = readElements(ma), rb = readElements(mb);
  assert.deepEqual(ra, rb, 'both clients end with identical content');
  assert.deepEqual(ra.map(e => e.id).sort(), ['only-a', 'only-b', 'shared']);
});

test('an erased element stays as a tombstone, so a late joiner does not get it back', () => {
  const a = new Y.Doc(), ma = a.getMap('d');
  pushElements(ma, [el('x')], new Map());
  pushElements(ma, [el('x', 2, { isDeleted: true })], new Map());
  const late = new Y.Doc();
  Y.applyUpdate(late, Y.encodeStateAsUpdate(a));
  const all = readElements(late.getMap('d'));
  assert.equal(all.length, 1);
  assert.equal(visible(all).length, 0);
});

test('a newer local element that lost the Yjs tie-break is written back, and both sides converge on it', () => {
  const a = new Y.Doc(), b = new Y.Doc();
  const ma = a.getMap('d'), mb = b.getMap('d');
  pushElements(ma, [el('k', 1, { x: 1 })], new Map());
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  // offline: A edits to version 5, B edits to version 3
  const localA = el('k', 5, { x: 500 }), localB = el('k', 3, { x: 300 });
  pushElements(ma, [localA], new Map());
  pushElements(mb, [localB], new Map());
  const keysA = [], keysB = [];
  ma.observe(e => e.keys.forEach((_, k) => keysA.push(k)));
  mb.observe(e => e.keys.forEach((_, k) => keysB.push(k)));
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b));
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  // whichever copy Yjs kept, each client re-asserts its newer local element for the changed keys
  reassert(ma, new Map([['k', localA]]), ['k']);
  reassert(mb, new Map([['k', localB]]), ['k']);
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b));
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a));
  assert.equal(ma.get('k').version, 5);
  assert.equal(mb.get('k').version, 5);
  assert.equal(ma.get('k').x, 500);
});

test('re-asserting does nothing when the shared copy is already the newest', () => {
  const doc = new Y.Doc(), m = doc.getMap('d');
  pushElements(m, [el('k', 4)], new Map());
  assert.equal(reassert(m, new Map([['k', el('k', 2)]]), ['k']), 0);
  assert.equal(reassert(m, new Map(), ['k', 'unknown']), 0);
  assert.equal(m.get('k').version, 4);
});

test('stored data is plain JSON: undefined and functions never reach the shared document', () => {
  const doc = new Y.Doc(), m = doc.getMap('d');
  pushElements(m, [el('a', 1, { fn() {}, nothing: undefined, nested: { ok: 1 } })], new Map());
  const v = m.get('a');
  assert.equal('fn' in v, false);
  assert.equal('nothing' in v, false);
  assert.deepEqual(v.nested, { ok: 1 });
});

test('compaction drops the oldest tombstones only when there are too many, never live elements', () => {
  const doc = new Y.Doc(), m = doc.getMap('d');
  for (let i = 0; i < 10; i++) m.set(`dead${i}`, el(`dead${i}`, 2, { isDeleted: true }));
  m.set('live', el('live'));
  assert.equal(compact(m, { maxTombstones: 20 }), 0);
  assert.equal(compact(m, { maxTombstones: 4 }), 6);
  assert.ok(m.has('live'));
  assert.equal([...m.keys()].filter(k => k.startsWith('dead')).length, 4);
});

test('corrupt entries are recognised so they can be skipped', () => {
  assert.equal(isElement(el('a')), true);
  for (const bad of [null, 5, 'x', {}, { id: 1, type: 'x', version: 1 }, { id: 'a', type: 'x' }]) assert.equal(isElement(bad), false);
});
