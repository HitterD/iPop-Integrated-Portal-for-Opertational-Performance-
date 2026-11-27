// node --test tests/chat-datepill.test.cjs — which day the floating date pill shows (wwwroot/js/chat-datepill.js).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const D = require(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'chat-datepill.js'));

const seps = [{ y: 0, label: 'Mon' }, { y: 800, label: 'Yesterday' }, { y: 1600, label: 'Today' }];

test('shows the day whose separator is at or above the top of the viewport', () => {
  assert.equal(D.currentLabel(seps, 10), 'Mon');
  assert.equal(D.currentLabel(seps, 799), 'Mon');
  assert.equal(D.currentLabel(seps, 800), 'Yesterday');
  assert.equal(D.currentLabel(seps, 5000), 'Today');
});

test('no separator above the top: nothing to show', () => {
  assert.equal(D.currentLabel([{ y: 50, label: 'Today' }], 10), null);
  assert.equal(D.currentLabel([], 100), null);
});

// Scroll perf (2026-11-27): while scrolling only the pill may change. A class on an ancestor of the
// list, or new text in the pill, restyled the whole ~16k-node chat (~150 ms per scroll start/stop).
test('bind touches only the pill: label as data attribute, no class on the list parent', () => {
  const classes = () => { const s = new Set(); return { add: c => s.add(c), remove: c => s.delete(c), contains: c => s.has(c), _s: s }; };
  const flow = { classList: classes() };
  let onScroll = null;
  const seps = [{ offsetTop: 0, textContent: ' Mon ' }, { offsetTop: 500, textContent: 'Today' }];
  const list = { scrollTop: 0, offsetTop: 40, parentElement: flow, querySelectorAll: () => seps,
    addEventListener: (t, f) => { if (t === 'scroll') onScroll = f; } };
  let pillTextWrites = 0;
  const pill = { classList: classes(), dataset: {}, style: {}, set textContent(v) { pillTextWrites++; }, get textContent() { return ''; } };
  const raf = [];
  const timers = [];
  const win = { document: {}, requestAnimationFrame: f => { raf.push(f); return 1; } };
  const src = require('node:fs').readFileSync(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'chat-datepill.js'), 'utf8');
  new Function('window', 'module', 'setTimeout', 'clearTimeout', src.replace(/typeof window !== 'undefined' \? window : null/, 'window'))(
    win, undefined, f => { timers.push(f); return timers.length; }, () => {});
  win.ipop.chatDatePill.bind(list, pill);
  list.scrollTop = 600; onScroll(); raf.shift()();
  assert.equal(pill.dataset.label, 'Today');
  assert.ok(pill.classList.contains('on'));
  assert.equal(pillTextWrites, 0);
  assert.equal(flow.classList._s.size, 0);
  timers.pop()();
  assert.ok(!pill.classList.contains('on'));
});
