// Task 8: the recording player's chat timestamps seek the <video> (ipopRooms.seek).
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const file = path.join(__dirname, '../src/IPop.Web/wwwroot/js/meeting-rooms.js');

function load(video) {
  const window = { LivekitClient: {} };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), {
    window, document: { getElementById: (id) => (id === 'mrt-player-video' ? video : null), querySelector: () => null },
    navigator: { userAgent: 'Desktop' }, location: { protocol: 'http:' }, console, setTimeout, clearTimeout,
  });
  return window.ipopRooms;
}

test('seek jumps to the second and plays', () => {
  let played = 0;
  const v = { duration: 3800, currentTime: 0, play: () => { played++; return Promise.resolve(); } };
  assert.equal(load(v).seek('mrt-player-video', 3735), true);
  assert.equal(v.currentTime, 3735);
  assert.equal(played, 1);
});

test('seek past the end stops at the end; metadata not loaded still seeks', () => {
  const v = { duration: 39, currentTime: 0, play: () => Promise.reject(new Error('autoplay')) };
  load(v).seek('mrt-player-video', 120);
  assert.equal(v.currentTime, 39);
  const w = { duration: NaN, currentTime: 0, play: () => undefined };
  load(w).seek('mrt-player-video', 12);
  assert.equal(w.currentTime, 12);
});

test('seek ignores bad input and a missing player', () => {
  const v = { duration: 39, currentTime: 5, play: () => undefined };
  const api = load(v);
  assert.equal(api.seek('mrt-player-video', -1), false);
  assert.equal(api.seek('mrt-player-video', 'x'), false);
  assert.equal(api.seek('nope', 3), false);
  assert.equal(v.currentTime, 5);
});
