import test from 'node:test';
import assert from 'node:assert/strict';
import { originalImageWidth, pastedImageWidth } from '../src/image-size.js';

test('screenshots retain their CSS pixel width, including HiDPI', () => {
  assert.equal(originalImageWidth(320, 1), 320);
  assert.equal(originalImageWidth(640, 2), 320);
  assert.equal(originalImageWidth(600, 1.25), 480);
  assert.equal(originalImageWidth(320, 0), 320);
  assert.equal(originalImageWidth(0, 2), null);
  assert.equal(originalImageWidth(NaN), null);
});

test('image decoder is released and failures keep legacy sizing', async () => {
  const previous = globalThis.createImageBitmap;
  let closed = false;
  try {
    globalThis.createImageBitmap = async () => ({ width: 640, close() { closed = true; } });
    assert.equal(await pastedImageWidth({}, 2), 320);
    assert.equal(closed, true);
    globalThis.createImageBitmap = async () => { throw new Error('unsupported'); };
    assert.equal(await pastedImageWidth({}, 2), null);
  } finally { globalThis.createImageBitmap = previous; }
});
