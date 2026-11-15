import test from 'node:test';
import assert from 'node:assert/strict';
import { checkCoverFile, nextPos, MAX_COVER_BYTES } from '../src/covers.js';

const f = (name, size, type) => ({ name, size, type });

test('files over 10 MB are refused with the size in the message', () => {
  const msg = checkCoverFile(f('IMG_2041.jpg', 14 * 1048576, 'image/jpeg'));
  assert.match(msg, /IMG_2041\.jpg/);
  assert.match(msg, /14\.0 MB/);
  assert.match(msg, /10 MB/);
  assert.equal(checkCoverFile(f('a.jpg', MAX_COVER_BYTES, 'image/jpeg')), null);
});

test('only jpeg, png and webp are accepted', () => {
  assert.equal(checkCoverFile(f('a.png', 10, 'image/png')), null);
  assert.equal(checkCoverFile(f('a.webp', 10, 'image/webp')), null);
  assert.equal(checkCoverFile(f('a.JPG', 10, '')), null);   // some browsers give no type; the extension decides
  assert.match(checkCoverFile(f('a.gif', 10, 'image/gif')), /JPG, PNG or WebP/);
  assert.match(checkCoverFile(f('a.pdf', 10, 'application/pdf')), /JPG, PNG or WebP/);
  assert.match(checkCoverFile(null), /Choose/);
});

test('dragging up shows the lower part of the photo, down the upper part, always within 0..100', () => {
  // band 200 px high, photo 600 px high => 400 px of it is hidden
  assert.equal(nextPos(50, -200, 200, 600), 100);
  assert.equal(nextPos(50, -100, 200, 600), 75);
  assert.equal(nextPos(50, 100, 200, 600), 25);
  assert.equal(nextPos(50, 9999, 200, 600), 0);
  assert.equal(nextPos(50, -9999, 200, 600), 100);
  assert.equal(nextPos(40, 0, 200, 600), 40);
});

test('a photo no taller than the band cannot divide by zero', () => {
  const p = nextPos(50, 80, 200, 150);
  assert.ok(p >= 0 && p <= 100);
});
