import test from 'node:test';
import assert from 'node:assert/strict';
import { moveGalleryItem } from '../src/rich.js';

const items = ['a', 'b', 'c', 'd'].map(id => ({ id }));
const ids = l => l.map(x => x.id).join('');

test('moving an image earlier or later reorders the rest around it', () => {
  assert.equal(ids(moveGalleryItem(items, 2, 1)), 'acbd');
  assert.equal(ids(moveGalleryItem(items, 0, 1)), 'bacd');
  assert.equal(ids(moveGalleryItem(items, 3, 0)), 'dabc');
});

test('moving past either end clamps; no move returns the same array; bad input is left alone', () => {
  assert.equal(ids(moveGalleryItem(items, 1, -5)), 'bacd');
  assert.equal(ids(moveGalleryItem(items, 1, 99)), 'acdb');
  assert.equal(moveGalleryItem(items, 0, 0), items);
  assert.equal(moveGalleryItem(items, 0, -1), items);                 // already first
  assert.equal(moveGalleryItem(items, 9, 0), items);
  assert.equal(moveGalleryItem(items, -1, 0), items);
  assert.equal(moveGalleryItem(null, 0, 1), null);
  assert.equal(ids(items), 'abcd');                                    // never mutates the input
});
