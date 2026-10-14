// node --test tests/emoji-placement.test.cjs — anchoring of the shared emoji picker popup.
// EMOJI_PICKER_JS overrides the module path (used to prove the test fails on the old file).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

const file = process.env.EMOJI_PICKER_JS || path.join(__dirname, '../src/IPop.Web/wwwroot/js/emoji-picker.js');
const load = () => import(pathToFileURL(file).href);
const rect = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height });
const desk = { width: 1440, height: 900 }, phone = { width: 390, height: 844 };

test('quick reply: opens above the button, left-aligned to it', async () => {
  const { placement } = await load();
  assert.deepEqual(placement(rect(962, 460, 32, 32), 402, desk, 'quick'), { left: 962, top: 52, width: 344 });
});

test('quick reply: flips below when there is no room above, and stays in the viewport', async () => {
  const { placement } = await load();
  const p = placement(rect(27, 120, 32, 32), 382, phone, 'quick');
  assert.equal(p.top, 158);
  assert.equal(p.left, 27);
  assert.ok(p.left + p.width <= phone.width - 8);
});

test('quick reply near the right edge is clamped, never past the viewport', async () => {
  const { placement } = await load();
  const p = placement(rect(1400, 600, 32, 32), 402, desk, 'quick');
  assert.equal(p.left, 1440 - 344 - 8);
});

test('compose: below the button, right-aligned; flips above when it does not fit', async () => {
  const { placement } = await load();
  assert.deepEqual(placement(rect(1372, 398, 28, 28), 402, desk, 'compose'), { left: 1056, top: 432, width: 344 });
  const up = placement(rect(1372, 700, 28, 28), 402, desk, 'compose');
  assert.equal(up.top, 700 - 6 - 402);
});

test('a popup taller than the space either side is clamped inside the viewport', async () => {
  const { placement } = await load();
  const p = placement(rect(20, 400, 32, 32), 900, { width: 390, height: 700 }, 'quick');
  assert.equal(p.top, 8);
});

test('an anchor that is not laid out or is off screen gives no position (never 0,0)', async () => {
  const { placement } = await load();
  assert.equal(placement(rect(0, 0, 0, 0), 402, desk, 'quick'), null);
  assert.equal(placement(null, 402, desk, 'quick'), null);
  assert.equal(placement(rect(10, -80, 32, 32), 402, desk, 'quick'), null);
  assert.equal(placement(rect(10, 950, 32, 32), 402, desk, 'compose'), null);
});

test('transformed containing block: the correction lands the box where it was wanted', async () => {
  const { corrected } = await load();
  // A pressed row scaled 0.96 drew the box at (39,-387) while (962,52) was set and wanted.
  assert.deepEqual(corrected({ left: 962, top: 52 }, { left: 962, top: 52 }, { left: 39, top: -387 }), { left: 1885, top: 491 });
});
