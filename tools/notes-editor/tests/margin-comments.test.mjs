import test from 'node:test';
import assert from 'node:assert/strict';
import { stack, pickMode } from '../src/margin-comments.js';

const tops = r => r.map(x => Math.round(x.top));
const card = (id, want, height = 100, focus = false) => ({ id, want, height, focus });

test('cards sit level with their text when there is room', () => {
  assert.deepEqual(tops(stack([card('a', 10), card('b', 300), card('c', 700)])), [10, 300, 700]);
});

test('cards that would overlap are pushed down, never on top of each other', () => {
  const r = stack([card('a', 100, 120), card('b', 150, 120), card('c', 160, 80)], 8);
  assert.deepEqual(tops(r), [100, 228, 356]);
  for (let i = 1; i < r.length; i++) assert.ok(r[i].top >= r[i - 1].top + r[i - 1].height + 8);
});

test('the focused card keeps its place at its text and the cards above it make room', () => {
  const r = stack([card('a', 100, 120), card('b', 150, 120, true), card('c', 160, 80)], 8);
  assert.equal(Math.round(r[1].top), 150);          // b is exactly at its text
  assert.ok(r[0].top + r[0].height + 8 <= r[1].top); // a moved up
  assert.ok(r[2].top >= r[1].top + r[1].height + 8); // c moved down
});

test('making room above never pushes a card above the top of the page', () => {
  const r = stack([card('a', 0, 200), card('b', 20, 200, true)], 8);
  assert.ok(r.every(x => x.top >= 0));
  assert.ok(r[1].top >= r[0].top + r[0].height + 8);
});

test('no cards, one card', () => {
  assert.deepEqual(stack([]), []);
  assert.deepEqual(tops(stack([card('a', 55)])), [55]);
});

test('margin mode needs a 640 px page column plus the card column, and normal width', () => {
  assert.equal(pickMode({ scrollerWidth: 1071 }), 'margin');     // 1600 px screen with both sidebars open
  assert.equal(pickMode({ scrollerWidth: 960 }), 'margin');
  assert.equal(pickMode({ scrollerWidth: 940 }), 'bubbles');      // 640 + 280 + 32 = 952
  assert.equal(pickMode({ scrollerWidth: 700 }), 'bubbles');
  assert.equal(pickMode({ scrollerWidth: 1600, fullWidth: true }), 'bubbles');
});
