// node --test tests/ph-motion.test.cjs — phone micro-interactions (demo a–j) decision functions.
const test = require('node:test');
const assert = require('node:assert/strict');
const { pullState, fabMini, routeDirection, hapticsOn } = require('../src/IPop.Web/wwwroot/js/ph-motion.js');

test('pull to refresh rubber-bands the drag and arms at the threshold', () => {
  assert.deepEqual(pullState(0), { y: 0, progress: 0, armed: false });
  assert.equal(pullState(-30).y, 0);
  assert.equal(pullState(100).armed, false);   // 50 px of travel
  assert.equal(pullState(140).armed, true);    // 70 px
  assert.equal(pullState(1000).y, 110);        // capped
});

test('FAB collapses while scrolling down and expands on the way up or near the top', () => {
  assert.equal(fabMini(0, 30, false), false);
  assert.equal(fabMini(50, 120, false), true);
  assert.equal(fabMini(120, 80, true), false);
  assert.equal(fabMini(120, 122, true), true);   // jitter keeps the state
  assert.equal(fabMini(300, 20, true), false);
});

test('route direction: list → detail is forward, detail → list and browser back are back', () => {
  assert.equal(routeDirection('/notes', '/notes/abc', false), 'fwd');
  assert.equal(routeDirection('/notes/abc', '/notes', false), 'back');
  assert.equal(routeDirection('/chat', '/mail', false), 'fwd');
  assert.equal(routeDirection('/chat', '/mail', true), 'back');
  assert.equal(routeDirection('/mail', '/mail', false), null);
});

test('haptics are on unless switched off', () => {
  assert.equal(hapticsOn(null), true);
  assert.equal(hapticsOn('on'), true);
  assert.equal(hapticsOn('off'), false);
});
