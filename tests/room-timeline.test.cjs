// Meeting scheduling v3 §2 (mockup screen 2): pure geometry of the room day timeline (drag to move, edges to resize).
// Minutes are local minutes of the day; a window is { start, end, floor? } (floor = earliest start, e.g. now).
const test = require('node:test');
const assert = require('node:assert/strict');
const { toMinutes, snap, clampMove, clampResize } = require('../src/IPop.Web/wwwroot/js/room-timeline.js');

const W = { start: 420, end: 1140 };               // 07:00–19:00

test('toMinutes maps pixels onto the window', () => {
  assert.equal(toMinutes(0, 720, W), 420);
  assert.equal(toMinutes(720, 720, W), 1140);
  assert.equal(toMinutes(360, 720, W), 780);
  assert.equal(toMinutes(10, 0, W), 420);           // no width yet: the window start
});

test('snap rounds to 15 minutes (or any step)', () => {
  assert.equal(snap(7), 0);
  assert.equal(snap(8), 15);
  assert.equal(snap(-8), -15);
  assert.equal(snap(52, 30), 60);
  assert.ok(Object.is(snap(-7), 0));               // never -0
});

test('move keeps the length and stays inside the window', () => {
  assert.deepEqual(clampMove(600, 660, 30, W), { s: 630, e: 690 });
  assert.deepEqual(clampMove(600, 660, -600, W), { s: 420, e: 480 });
  assert.deepEqual(clampMove(1020, 1110, 120, W), { s: 1050, e: 1140 });
});

test('move never starts before the floor (now)', () => {
  assert.deepEqual(clampMove(600, 660, -120, { ...W, floor: 590 }), { s: 590, e: 650 });
});

test('resize keeps at least 15 minutes and at most 8 hours', () => {
  assert.deepEqual(clampResize(600, 660, 'r', -60, W), { s: 600, e: 615 });
  assert.deepEqual(clampResize(600, 660, 'l', 60, W), { s: 645, e: 660 });
  assert.deepEqual(clampResize(600, 660, 'r', 600, W), { s: 600, e: 1080 });   // 8 h cap
  assert.deepEqual(clampResize(600, 1080, 'l', -60, W), { s: 600, e: 1080 }); // already 8 h
});

test('resize is clamped to the window and the floor', () => {
  assert.deepEqual(clampResize(600, 660, 'l', -300, W), { s: 420, e: 660 });
  assert.deepEqual(clampResize(1080, 1110, 'r', 120, W), { s: 1080, e: 1140 });
  assert.deepEqual(clampResize(600, 660, 'l', -60, { ...W, floor: 590 }), { s: 590, e: 660 });
  assert.deepEqual(clampResize(600, 660, 'x', 30, W), { s: 600, e: 660 });       // unknown edge: unchanged
});

// Phone (v3 Task 10): the track is 960 px inside a sideways scroller; on open the selection is scrolled into view.
const { revealLeft } = require('../src/IPop.Web/wwwroot/js/room-timeline.js');

test('revealLeft keeps the scroll when the selection is already visible', () => {
  assert.equal(revealLeft(100, 120, 0, 358, 988), 0);
  assert.equal(revealLeft(500, 100, 300, 358, 988), 300);
});

test('revealLeft scrolls so the selection starts 90 px in, clamped to the content', () => {
  assert.equal(revealLeft(600, 120, 0, 358, 988), 510);       // 13:00 block on a 960 px track
  assert.equal(revealLeft(900, 80, 0, 358, 988), 630);        // late block: clamped to the end
  assert.equal(revealLeft(40, 80, 500, 358, 988), 0);         // left of view: back to the start, not below 0
  assert.equal(revealLeft(400, 80, 0, 358, 300), 0);          // content narrower than the view
  assert.equal(revealLeft(600, 120, 0, 358, 988, 20), 580);   // custom pad
});
