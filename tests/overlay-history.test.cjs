// node --test tests/overlay-history.test.cjs — phone Back closes the top overlay (mobile Stage 1 hotfix #14).
// Each overlay opened on a phone pushes one same-URL history entry; Back (popstate) closes the top one;
// closing in-app drops its own entry without closing anything else; details (list/detail pages) still work.
const test = require('node:test');
const assert = require('node:assert/strict');
const { createOverlayHistory } = require('../src/IPop.Web/wwwroot/js/mobile.js');

// Minimal browser history: back() fires popstate asynchronously like a browser does.
function fakeHistory() {
  const entries = [{ state: null }]; let idx = 0; const listeners = [];
  const h = {
    get state() { return entries[idx].state; },
    get length() { return idx + 1; },
    pushState(state) { entries.splice(idx + 1); entries.push({ state }); idx++; },
    back() { if (idx === 0) return; idx--; queueMicrotask(() => listeners.forEach(f => f())); },
    onPop(f) { listeners.push(f); },
  };
  return h;
}
const tick = () => new Promise(r => setImmediate(r));

function setup(phone = true) {
  const history = fakeHistory();
  const closed = [];
  const ov = createOverlayHistory({ history, isPhone: () => phone });
  history.onPop(() => ov.onPopState());
  const ref = name => ({ invokeMethodAsync: m => { closed.push(name + ':' + m); return Promise.resolve(); } });
  return { history, ov, closed, ref };
}

test('Back closes only the top overlay, one per press', async () => {
  const { history, ov, closed, ref } = setup();
  ov.open(ref('drawer')); ov.open(ref('menu'));
  assert.equal(history.length, 3);
  history.back(); await tick();
  assert.deepEqual(closed, ['menu:OnBack']);
  history.back(); await tick();
  assert.deepEqual(closed, ['menu:OnBack', 'drawer:OnBack']);
  assert.equal(ov.count(), 0);
});

test('closing in-app removes its own history entry without closing anything', async () => {
  const { history, ov, closed, ref } = setup();
  const a = ov.open(ref('drawer'));
  ov.close(a); await tick();
  assert.equal(history.length, 1);
  assert.deepEqual(closed, []);
  assert.equal(ov.count(), 0);
});

test('an overlay closed by navigation (its entry is no longer current) does not navigate back', async () => {
  const { history, ov, closed, ref } = setup();
  const a = ov.open(ref('drawer'));
  history.pushState({ other: true });  // e.g. a link inside the drawer opened another page
  ov.close(a); await tick();
  assert.equal(history.length, 3);
  assert.deepEqual(closed, []);
});

test('details pushed before an overlay still close after it', async () => {
  const { history, ov, closed, ref } = setup();
  ov.pushDetail(ref('reader'), 'MobileBack');
  ov.open(ref('sheet'));
  history.back(); await tick();
  history.back(); await tick();
  assert.deepEqual(closed, ['sheet:OnBack', 'reader:MobileBack']);
});

test('desktop: no history entries are added (Escape still closes through the .NET stack)', () => {
  const { history, ov, ref } = setup(false);
  assert.equal(ov.open(ref('drawer')), null);
  assert.equal(history.length, 1);
});

// ── keyboard: keep the caret inside the visible viewport (#6, #9) ──
const { revealDelta } = require('../src/IPop.Web/wwwroot/js/mobile.js');

test('caret below the keyboard edge scrolls down just enough', () => {
  // visible band 0..490 (keyboard took the rest); caret at 603..623
  assert.equal(revealDelta({ top: 603, bottom: 623 }, 0, 490), 145);
});
test('caret above the visible band (scrolled off the top) scrolls up', () => {
  assert.equal(revealDelta({ top: -93, bottom: -73 }, 0, 490), -105);
});
test('a visible caret needs no scroll', () => {
  assert.equal(revealDelta({ top: 200, bottom: 220 }, 0, 490), 0);
});
test('a box taller than the band keeps its top visible', () => {
  assert.equal(revealDelta({ top: 300, bottom: 1200 }, 0, 490), 288);
});
