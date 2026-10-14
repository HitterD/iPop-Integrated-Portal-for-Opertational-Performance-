// Mail read receipts (spec 2026-10-14 §3): a message counts as seen only when it is shown in the
// reading pane, on screen, the tab visible AND focused, continuously for 2 s. Once per message per session.
const test = require('node:test');
const assert = require('node:assert/strict');
const { createSeenTracker, isOnScreen, SEEN_DELAY_MS } = require('../src/IPop.Web/wwwroot/js/mail-seen.js');

function fakeClock() {
  let t = 0, seq = 0;
  const timers = new Map();
  return {
    setTimer(fn, ms) { const id = ++seq; timers.set(id, { fn, at: t + ms }); return id; },
    clearTimer(id) { timers.delete(id); },
    advance(ms) {
      const end = t + ms;
      for (;;) {
        const due = [...timers.entries()].filter(([, v]) => v.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) break;
        t = due[1].at; timers.delete(due[0]); due[1].fn();
      }
      t = end;
    },
    pending: () => timers.size,
  };
}

function setup() {
  const clock = fakeClock();
  const seen = [];
  const tr = createSeenTracker({ setTimer: clock.setTimer, clearTimer: clock.clearTimer, onSeen: id => seen.push(id) });
  return { clock, seen, tr };
}

test('the delay is 2 seconds', () => assert.equal(SEEN_DELAY_MS, 2000));

test('shown + attentive for 2 s records once', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  clock.advance(1999);
  assert.deepEqual(seen, []);
  clock.advance(1);
  assert.deepEqual(seen, ['m1']);
  clock.advance(10000);
  assert.deepEqual(seen, ['m1']);
});

test('nothing is recorded without attention (hidden tab or unfocused window)', () => {
  const { clock, seen, tr } = setup();
  tr.setShown('m1', true);
  clock.advance(5000);
  assert.deepEqual(seen, []);
  assert.equal(clock.pending(), 0);
});

test('losing focus cancels the timer; regaining restarts the full 2 s', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  clock.advance(1500);
  tr.setAttention(false);
  clock.advance(5000);
  assert.deepEqual(seen, []);
  tr.setAttention(true);
  clock.advance(1500);
  assert.deepEqual(seen, []);   // continuous, not cumulative
  clock.advance(500);
  assert.deepEqual(seen, ['m1']);
});

test('scrolling off screen or collapsing cancels the timer', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  clock.advance(1000);
  tr.setShown('m1', false);
  clock.advance(5000);
  assert.deepEqual(seen, []);
  tr.setShown('m1', true);
  clock.advance(2000);
  assert.deepEqual(seen, ['m1']);
});

test('each message has its own timer', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('a', true);
  clock.advance(1000);
  tr.setShown('b', true);
  clock.advance(1000);
  assert.deepEqual(seen, ['a']);
  clock.advance(1000);
  assert.deepEqual(seen, ['a', 'b']);
});

test('once per message per session, even after re-showing it', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  clock.advance(2000);
  tr.setShown('m1', false);
  tr.setShown('m1', true);
  tr.setAttention(false);
  tr.setAttention(true);
  clock.advance(5000);
  assert.deepEqual(seen, ['m1']);
  assert.equal(tr.wasSeen('m1'), true);
});

test('repeated shown/attention reports do not restart a running timer', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  clock.advance(1500);
  tr.setShown('m1', true);
  tr.setAttention(true);
  clock.advance(500);
  assert.deepEqual(seen, ['m1']);
});

test('reset cancels everything (mail closed, page left)', () => {
  const { clock, seen, tr } = setup();
  tr.setAttention(true);
  tr.setShown('m1', true);
  tr.reset();
  clock.advance(5000);
  assert.deepEqual(seen, []);
  assert.equal(clock.pending(), 0);
});

test('on screen = half the message visible, or a full reading block of a long one', () => {
  assert.equal(isOnScreen(0, 400, 800), false);
  assert.equal(isOnScreen(199, 400, 800), false);
  assert.equal(isOnScreen(200, 400, 800), true);
  // Long mail taller than the pane: enough of it fills the pane.
  assert.equal(isOnScreen(300, 3000, 600), true);
  assert.equal(isOnScreen(100, 3000, 600), false);
  assert.equal(isOnScreen(10, 0, 600), false);
});
