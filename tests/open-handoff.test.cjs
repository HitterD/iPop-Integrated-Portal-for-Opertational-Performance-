// node --test tests/open-handoff.test.cjs — Notifier/toast links reuse the open iPop tab or installed app
// instead of opening one more tab per message (grilled 2026-11-02).
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const WEB = path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot');
const read = f => fs.readFileSync(path.join(WEB, f), 'utf8');
const { ipopOpenTarget, ipopTargetFromLaunch } = require(path.join(WEB, 'js', 'open-target.js'));
const O = 'https://ipop.test';

test('open target keeps same-origin paths and rejects everything else', () => {
  assert.equal(ipopOpenTarget('/chat/abc?m=1#x', O), '/chat/abc?m=1#x');
  for (const bad of ['https://evil.test/x', '//evil.test/x', '/\\evil.test', 'javascript:alert(1)', '', null, '/open.html?to=/x', '/open'])
    assert.equal(ipopOpenTarget(bad, O), '/dashboard', String(bad));
});

test('launch URLs: /open.html?to= and web+ipop:// via the protocol handler h=', () => {
  assert.equal(ipopTargetFromLaunch(`${O}/open.html?to=${encodeURIComponent('/mail?mailId=1')}`, O), '/mail?mailId=1');
  const h = encodeURIComponent('web+ipop://open?to=' + encodeURIComponent('/chat/r1'));
  assert.equal(ipopTargetFromLaunch(`${O}/open.html?h=${h}`, O), '/chat/r1');
  assert.equal(ipopTargetFromLaunch(`${O}/dashboard`, O), null);
});

test('manifest: web+ipop protocol to /open.html and focus-existing launch', () => {
  const m = JSON.parse(read('manifest.webmanifest'));
  assert.deepEqual(m.protocol_handlers, [{ protocol: 'web+ipop', url: '/open.html?h=%s' }]);
  assert.equal(m.launch_handler.client_mode[0], 'focus-existing');
  assert.match(read('sw.js'), /open\\\.html\)\$\/i;/, 'the worker must not answer /open.html with the SPA shell');
});

// ── /open.html: hand off to the active tab, else load the page here ──
function runHandoff({ standalone = false, answer = true }) {
  const channels = [];
  class BC {
    constructor(name) { this.name = name; this.sent = []; channels.push(this); }
    postMessage(m) { this.sent.push(m); if (answer && m.type === 'handoff') setTimeout(() => this.onmessage({ data: { type: 'handoff-ok', to: m.from } }), 5); }
    close() { this.closed = true; }
  }
  const win = { closed: false, replaced: null, classes: new Set() };
  const ctx = {
    URL, URLSearchParams, setTimeout, BroadcastChannel: BC, crypto: { randomUUID: () => 'me' },
    matchMedia: () => ({ matches: standalone }),
    location: { href: `${O}/open.html?to=${encodeURIComponent('/chat/r9')}`, origin: O, replace(u) { win.replaced = u; } },
    document: { body: { classList: { add: c => win.classes.add(c) } } },
    close() { win.closed = true; },
  };
  ctx.window = ctx; ctx.globalThis = ctx;
  vm.createContext(ctx);
  vm.runInContext(read('js/open-target.js'), ctx);
  vm.runInContext(read('js/open-handoff.js'), ctx);
  return { win, channels };
}
const same = (a, b, m) => assert.deepEqual(JSON.parse(JSON.stringify(a)), b, m);
const wait = ms => new Promise(r => setTimeout(r, ms));

test('an open iPop tab takes the page; the new tab closes itself', async () => {
  const { win, channels } = runHandoff({});
  same(channels[0].sent[0], { type: 'handoff', from: 'me', url: '/chat/r9' });
  await wait(30);
  assert.equal(win.closed, true);
  assert.equal(win.replaced, null);
  await wait(500);
  assert.equal(win.replaced, null, 'no late fallback after a handoff');
});

test('no iPop tab answers: the page loads in this tab', async () => {
  const { win } = runHandoff({ answer: false });
  await wait(500);
  assert.equal(win.replaced, '/chat/r9');
  assert.equal(win.closed, false);
});

test('inside the installed app window: load directly, no handoff', () => {
  const { win, channels } = runHandoff({ standalone: true });
  assert.equal(channels.length, 0);
  assert.equal(win.replaced, '/chat/r9');
});

// ── session-lock.js: only the active tab accepts a handoff ──
test('active tab answers handoff-ok and navigates; a locked tab ignores it', () => {
  const sent = [], nav = [];
  class BC { constructor() { } postMessage(m) { sent.push(m); } }
  const ctx = { BroadcastChannel: BC, crypto: { randomUUID: () => 'tabA' }, setTimeout, setInterval, clearInterval,
    document: { visibilityState: 'visible', title: 'iPop', head: {}, hasFocus: () => false, addEventListener() { }, removeEventListener() { } },
    MutationObserver: class { observe() { } disconnect() { } }, clearTimeout, addEventListener() { }, removeEventListener() { },
    Blazor: { navigateTo: u => nav.push(u) }, location: { origin: O }, URL };
  ctx.window = ctx; ctx.focus = () => { };
  vm.createContext(ctx);
  vm.runInContext(read('js/open-target.js'), ctx);
  vm.runInContext(read('js/session-lock.js'), ctx);
  const s = ctx.window.ipopSession;
  s._ch = { postMessage: m => sent.push(m) }; s._id = 'tabA';
  s._state = 'locked';
  s._onMessage({ type: 'handoff', from: 'new', url: '/chat/r1' });
  assert.equal(nav.length, 0);
  s._state = 'active';
  s._onMessage({ type: 'handoff', from: 'new', url: 'https://evil.test/' });
  same(sent.at(-1), { type: 'handoff-ok', from: 'tabA', to: 'new' });
  assert.deepEqual(nav, ['/dashboard']);
  s._onMessage({ type: 'handoff', from: 'new2', url: '/mail' });
  assert.equal(nav.at(-1), '/mail');
  // The tab that took the page marks its title for the Notifier (keeping the unread count first).
  ctx.document.title = '(3) Mail — IPop';
  s._markOn = false; s._mark();
  assert.equal(ctx.document.title, '(3) ● New in iPop · Mail — IPop');
});

// ── sw.js: a toast click prefers the installed app window ──
function worker() {
  const events = {}, cache = new Map(), opened = [];
  let wins = [];
  const ctx = { URL, Response, MessageChannel, setTimeout, clearTimeout, Date,
    caches: { async open() { return { async match(k) { const r = cache.get(k); return r ? r.clone() : null; }, async put(k, r) { cache.set(k, r); }, async keys() { return []; }, async delete() { } }; } },
    self: { location: { origin: O }, addEventListener(n, h) { events[n] = h; }, skipWaiting() { },
      clients: { async matchAll() { return wins; }, async claim() { }, async openWindow(u) { opened.push(u); } },
      registration: { scope: O + '/', async getNotifications() { return []; } } } };
  vm.createContext(ctx); vm.runInContext(read('sw.js'), ctx);
  const click = async url => { let t; events.notificationclick({ notification: { close() { }, data: { url } }, action: '', waitUntil(p) { t = p; } }); await t; };
  const msg = async data => { let t; events.message({ data, source: { url: O + '/dashboard' }, waitUntil(p) { t = p; } }); await t; };
  return { setWins: w => { wins = w; }, opened, click, msg };
}
function client(url, standalone) {
  const c = { url, got: [], focused: 0, focus: async () => { c.focused++; },
    postMessage(m, ports) {
      if (m.type === 'ipop-display') { if (standalone !== null) ports[0].postMessage({ standalone }); return; }
      c.got.push(m);
    } };
  return c;
}

test('toast click: app window beats a browser tab', async () => {
  const w = worker();
  const tab = client(O + '/mail', false), app = client(O + '/chat', true);
  w.setWins([tab, app]);
  await w.click('/chat/r2');
  same(app.got, [{ type: 'ipop-open', url: '/chat/r2' }]);
  assert.equal(tab.got.length, 0);
});

test('toast click: only a browser tab and no app installed → that tab', async () => {
  const w = worker();
  const tab = client(O + '/mail', false);
  w.setWins([tab]);
  await w.click('/mail?mailId=3');
  same(tab.got, [{ type: 'ipop-open', url: '/mail?mailId=3' }]);
  assert.equal(w.opened.length, 0);
});

test('toast click: app installed but closed, a tab open → reuse that tab (never one tab per click)', async () => {
  const w = worker();
  await w.msg({ type: 'ipop-app-installed', v: 1, installed: true });
  const tab = client(O + '/mail', false);
  w.setWins([tab]);
  await w.click('/chat/r5');
  same(tab.got, [{ type: 'ipop-open', url: '/chat/r5' }]);
  assert.equal(w.opened.length, 0);
});

test('toast click: app installed, nothing open → open it (the browser routes it to the app)', async () => {
  const w = worker();
  await w.msg({ type: 'ipop-app-installed', v: 1, installed: true });
  w.setWins([]);
  await w.click('/chat/r6');
  assert.deepEqual(w.opened, [O + '/chat/r6']);
});

test('toast click: nothing open → one new window', async () => {
  const w = worker();
  w.setWins([]);
  await w.click('/notifications');
  assert.deepEqual(w.opened, [O + '/notifications']);
});
