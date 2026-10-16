// node --test tests/ph-kit.test.cjs — pure parts of the phone kit (wwwroot/js/ph-kit.js).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const K = require(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'ph-kit.js'));

test('sheet drag dismisses past 30 % of its height or on a fast flick', () => {
  assert.equal(K.dismissDecision(100, 600, 0.1), false);
  assert.equal(K.dismissDecision(181, 600, 0.1), true);
  assert.equal(K.dismissDecision(40, 600, 0.7), true);
  assert.equal(K.dismissDecision(-50, 600, 2), false, 'dragging up never dismisses');
  assert.equal(K.dismissDecision(0, 0, 0), false);
});

test('large title collapses once it has scrolled under the nav bar', () => {
  assert.equal(K.collapseState({ titleBottom: 120, navBottom: 94 }), false);
  assert.equal(K.collapseState({ titleBottom: 94, navBottom: 94 }), true);
  assert.equal(K.collapseState({ titleBottom: 10, navBottom: 94 }), true);
});

test('reorder target follows the pointer over row centres', () => {
  const centres = [26, 78, 130, 182];
  assert.equal(K.reorderTarget(centres, 0, 20), 0);
  assert.equal(K.reorderTarget(centres, 0, 100), 1);
  assert.equal(K.reorderTarget(centres, 3, 10), 0);
  assert.equal(K.reorderTarget(centres, 1, 500), 3);
  assert.equal(K.reorderTarget(centres, 2, 131), 2);
});

test('platform from the user agent', () => {
  assert.equal(K.platform('Mozilla/5.0 (Linux; Android 14; Pixel 7) Chrome/141 Mobile Safari/537.36'), 'android');
  assert.equal(K.platform('Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Safari/604.1'), 'ios');
  assert.equal(K.platform('Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)'), 'ios');
  assert.equal(K.platform('Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/141'), 'desktop');
});

test('keyboard counts as open when the visual viewport lost a quarter of the height', () => {
  assert.equal(K.keyboardOpen(844, 844), false);
  assert.equal(K.keyboardOpen(844, 500), true);
  assert.equal(K.keyboardOpen(844, 700), false);
});
