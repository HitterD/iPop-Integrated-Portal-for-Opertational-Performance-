import test from 'node:test';
import assert from 'node:assert/strict';
import * as Y from 'yjs';
import { restoreScenes } from '../src/restore-scenes.js';

const el = (id, version, extra = {}) => ({ id, type: 'rectangle', version, versionNonce: version, isDeleted: false, x: 0, y: 0, ...extra });
const doc = scenes => { const d = new Y.Doc(); for (const [n, els] of Object.entries(scenes)) els.forEach(e => d.getMap(`draw:${n}`).set(e.id, e)); return d; };
const live = (d, n = 'a') => { const o = {}; d.getMap(`draw:${n}`).forEach(v => { o[v.id] = v; }); return o; };

test('elements drawn after the version are removed, and the ones it had come back', () => {
  const old = doc({ a: [el('keep', 1, { x: 5 }), el('erased-later', 1)] });
  const now = doc({ a: [el('keep', 4, { x: 99 }), el('erased-later', 3, { isDeleted: true }), el('new-one', 2)] });
  const n = restoreScenes(now, old);
  const r = live(now);
  assert.ok(n >= 3);
  assert.equal(r.keep.x, 5);                    // moved shape goes back
  assert.equal(r.keep.isDeleted, false);
  assert.equal(r['erased-later'].isDeleted, false);   // erased after the version: comes back
  assert.equal(r['new-one'].isDeleted, true);   // drawn after the version: gone (kept as a tombstone)
});

test('restored elements outrank every existing copy, so other clients accept them', () => {
  const old = doc({ a: [el('x', 2, { x: 1 })] });
  const now = doc({ a: [el('x', 7, { x: 50 })] });
  restoreScenes(now, old);
  assert.ok(live(now).x.version > 7);
  // a client that still holds version 7 would reject version 2 but must accept this
});

test('restoring an identical scene writes nothing', () => {
  const same = [el('x', 3), el('y', 5)];
  assert.equal(restoreScenes(doc({ a: same }), doc({ a: same })), 0);
});

test('a scene that did not exist in the old version is emptied; one that only existed then is brought back', () => {
  const old = doc({ onlyOld: [el('o', 1)] });
  const now = doc({ onlyNow: [el('n', 1)] });
  restoreScenes(now, old);
  assert.equal(live(now, 'onlyNow').n.isDeleted, true);
  assert.equal(live(now, 'onlyOld').o.isDeleted, false);
});

test('two clients restoring the same version converge', () => {
  const old = doc({ a: [el('x', 2, { x: 1 })] });
  const A = doc({ a: [el('x', 6, { x: 60 })] }), B = new Y.Doc();
  Y.applyUpdate(B, Y.encodeStateAsUpdate(A));
  restoreScenes(A, old); restoreScenes(B, old);
  Y.applyUpdate(A, Y.encodeStateAsUpdate(B)); Y.applyUpdate(B, Y.encodeStateAsUpdate(A));
  assert.equal(live(A).x.x, 1); assert.equal(live(B).x.x, 1);
  assert.deepEqual(live(A), live(B));
});
