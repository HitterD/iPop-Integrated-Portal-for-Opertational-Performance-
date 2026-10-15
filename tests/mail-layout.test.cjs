// node --test tests/mail-layout.test.cjs — Mail's resizable panes (mockup docs/mockups/mail-layout-outlook.html v3):
// Mailboxes 180–320, list 280–560, reading pane ≥ 420; rail 56 px; double-click resets; saved per user per device.
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('../src/IPop.Web/wwwroot/js/mail-layout.js');

test('defaults fit as stored on a wide surface', () => {
  assert.deepEqual(L.layout(L.DEF, 1176), { w1: 232, w2: 380, rail: false });
});

test('a narrower surface takes width from the list first, then from Mailboxes', () => {
  // reading pane needs 420: 1000 - 232 - 380 = 388 → list gives 32
  assert.deepEqual(L.layout(L.DEF, 1000), { w1: 232, w2: 348, rail: false });
  // 900: list goes to its 280 minimum (−100), Mailboxes gives the last 32
  assert.deepEqual(L.layout(L.DEF, 900), { w1: 200, w2: 280, rail: false });
});

test('too narrow for three panes at their minimums: Mailboxes becomes the rail', () => {
  const r = L.layout(L.DEF, 850);
  assert.equal(r.rail, true);
  assert.equal(r.w1, 56);
  assert.equal(r.w2, 374);   // 850 − 56 − 420
});

test('collapsed Mailboxes is a 56 px rail and stored widths are clamped to the limits', () => {
  assert.deepEqual(L.layout({ w1: 232, w2: 380, collapsed: true }, 1200), { w1: 56, w2: 380, rail: true });
  assert.deepEqual(L.layout({ w1: 999, w2: 10, collapsed: false }, 1600), { w1: 320, w2: 280, rail: false });
});

test('dragging clamps to the pane limits and keeps the reading pane at 420', () => {
  const eff = { w1: 232, w2: 380 };
  assert.equal(L.dragTo('1', 232, 200, eff, 1400), 320);
  assert.equal(L.dragTo('1', 232, -100, eff, 1400), 180);
  assert.equal(L.dragTo('2', 380, 200, eff, 1100), 448);   // 1100 − 232 − 420
  assert.equal(L.dragTo('2', 380, 500, eff, 1600), 560);
  assert.equal(L.dragTo('2', 380, -500, eff, 1600), 280);
  // even when the reading pane can't get 420, a pane never goes under its minimum
  assert.equal(L.dragTo('2', 380, 0, eff, 700), 280);
});

test('keyboard steps 16 px and clamps', () => {
  assert.equal(L.step('1', { w1: 232, w2: 380 }, 1), 248);
  assert.equal(L.step('1', { w1: 316, w2: 380 }, 1), 320);
  assert.equal(L.step('2', { w1: 232, w2: 290 }, -1), 280);
});

test('parse merges saved values over defaults and survives junk', () => {
  assert.deepEqual(L.parse(null), L.DEF);
  assert.deepEqual(L.parse('not json'), L.DEF);
  assert.deepEqual(L.parse('{"w2":300}'), { w1: 232, w2: 300, collapsed: false });
  assert.deepEqual(L.parse('{"w1":"wide","w2":null,"collapsed":"yes"}'), L.DEF);
  assert.deepEqual(L.parse('{"w1":200,"w2":420,"collapsed":true}'), { w1: 200, w2: 420, collapsed: true });
  assert.deepEqual(L.parse(L.serialize({ w1: 250, w2: 333, collapsed: true })), { w1: 250, w2: 333, collapsed: true });
});

test('saved per user: one account never sees another account\'s widths; no user, no write', () => {
  const m = new Map();
  const store = { get: (nik, k) => (m.has(`${k}@${nik}`) ? m.get(`${k}@${nik}`) : null), set: (nik, k, v) => m.set(`${k}@${nik}`, v) };
  L.save(store, 'A', { w1: 300, w2: 500, collapsed: true });
  assert.deepEqual(L.load(store, 'A'), { w1: 300, w2: 500, collapsed: true });
  assert.deepEqual(L.load(store, 'B'), L.DEF);
  L.save(store, '', { w1: 300, w2: 500, collapsed: true });
  L.save(store, null, { w1: 300, w2: 500, collapsed: true });
  assert.equal(m.size, 1);
  assert.equal([...m.keys()][0], `${L.KEY}@A`);
});

test('reset keeps the collapsed choice', () => {
  assert.deepEqual(L.reset({ w1: 300, w2: 500, collapsed: true }), { w1: 232, w2: 380, collapsed: true });
});
