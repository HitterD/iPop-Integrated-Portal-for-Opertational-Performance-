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

test('after a reload on a stale overlay entry, new overlays still close one per Back', async () => {
  const { history, ov, ref } = setup();
  ov.open(ref('old'));                       // entry { ipopLayer: 1 } stays in history across the reload
  const closed = [];
  const fresh = createOverlayHistory({ history, isPhone: () => true }); // page reloaded: a new instance
  history.onPop(() => fresh.onPopState());
  const r = name => ({ invokeMethodAsync: m => { closed.push(name); return Promise.resolve(); } });
  fresh.open(r('more')); fresh.open(r('edit'));
  history.back(); await tick();
  assert.deepEqual(closed, ['edit']);
  history.back(); await tick();
  assert.deepEqual(closed, ['edit', 'more']);
  assert.equal(fresh.count(), 0);
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

// Real browsers apply history.back() asynchronously: a pushState issued right after back() (a link's
// navigation) is applied first, then the queued back() traverses away from the new page.
function asyncBackHistory() {
  const entries = [{ state: null }]; let idx = 0; const listeners = []; let pendingBacks = 0;
  const h = {
    get state() { return entries[idx].state; },
    get length() { return idx + 1; },
    pushState(state) { entries.splice(idx + 1); entries.push({ state }); idx++; },
    back() { pendingBacks++; setImmediate(() => { pendingBacks--; if (idx === 0) return; idx--; listeners.forEach(f => f()); }); },
    onPop(f) { listeners.push(f); },
  };
  return h;
}

test('a link inside a sheet: close then navigate does not bounce back (More tile)', async () => {
  // Blazor runs the tile's @onclick (close the sheet) and then the <a href> navigation pushes the page.
  // The sheet's own history entry must not be removed with history.back() in that case, or the queued
  // back() lands after the navigation and takes the user back to the previous page.
  const history = asyncBackHistory(); const closed = [];
  const ov = createOverlayHistory({ history, isPhone: () => true });
  history.onPop(() => ov.onPopState());
  const ref = { invokeMethodAsync: m => { closed.push(m); return Promise.resolve(); } };
  const a = ov.open(ref);
  ov.close(a, { navigating: true });     // tile tapped: the sheet closes because a link is navigating
  history.pushState({ page: '/notes' }); // the link's navigation
  await tick(); await tick(); await tick();
  assert.deepEqual(history.state, { page: '/notes' }, 'still on the page the tile opened');
  assert.deepEqual(closed, []);
});

test('closing a sheet in-app without navigating still removes its entry', async () => {
  const history = asyncBackHistory();
  const ov = createOverlayHistory({ history, isPhone: () => true });
  history.onPop(() => ov.onPopState());
  const a = ov.open({ invokeMethodAsync: () => Promise.resolve() });
  assert.equal(history.length, 2);
  ov.close(a);
  await tick(); await tick();
  assert.equal(history.length, 1);
});

test('the link-tap signal (navigatingSoon) also keeps a closing sheet from stepping back', async () => {
  const history = asyncBackHistory(); let navigating = false;
  const ov = createOverlayHistory({ history, isPhone: () => true, navigatingSoon: () => navigating });
  history.onPop(() => ov.onPopState());
  const a = ov.open({ invokeMethodAsync: () => Promise.resolve() });
  navigating = true;                     // a tap on <a href> inside the sheet was just seen
  ov.close(a);                           // MoreSheet's @onclick closes it with no extra arguments
  history.pushState({ page: '/projects' });
  await tick(); await tick(); await tick();
  assert.deepEqual(history.state, { page: '/projects' });
});
