// node --test tests/chat-swipe.test.cjs — pure parts of the phone message swipe (wwwroot/js/chat-swipe.js).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const S = require(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'chat-swipe.js'));
const K = require(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'ph-kit.js'));

test('touches within 24 px of either screen edge are left to the OS back gesture', () => {
  assert.equal(S.edgeStart(10, 390), true);
  assert.equal(S.edgeStart(23.9, 390), true);
  assert.equal(S.edgeStart(24, 390), false);
  assert.equal(S.edgeStart(200, 390), false);
  assert.equal(S.edgeStart(370, 390), true);
});

test('the first 8 px decide the axis; mostly vertical is a scroll', () => {
  assert.equal(S.axis(4, 3), null);
  assert.equal(S.axis(20, 4), true);
  assert.equal(S.axis(-20, 4), true);
  assert.equal(S.axis(6, 20), false);
  assert.equal(S.axis(12, 11), false, 'a diagonal is not a swipe');
});

test('threshold matches ph-kit swipeDecision (22 % of the row, 64-96 px)', () => {
  for (const w of [200, 340, 390, 600]) {
    const t = S.threshold(w);
    assert.equal(K.swipeDecision(t, w), 'right');
    assert.equal(K.swipeDecision(-t, w), 'left');
    assert.equal(K.swipeDecision(t - 1, w), null);
  }
  assert.equal(S.threshold(100), 64);
  assert.equal(S.threshold(1000), 96);
});

test('the bubble follows 1:1 up to the threshold, then rubber-bands', () => {
  assert.equal(S.follow(50, 80), 50);
  assert.equal(S.follow(-80, 80), -80);
  assert.ok(Math.abs(S.follow(140, 80) - (80 + 60 * 0.35)) < 1e-9);
  assert.ok(S.follow(-140, 80) < -80);
});

test('long-press still cancels on movement, so a swipe never also opens the menu', () => {
  assert.equal(K.longPress(500, 30, 0), false);
  assert.equal(K.longPress(500, 4, 3), true);
});
