const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const all = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8');
const start = all.indexOf('// iPop Notifier "Sign in with browser" (1.4.2');
const source = all.slice(start, all.indexOf('\n};', all.indexOf('ipop.notifierLink = ', start)) + 3);

function browser(href = 'https://web.example/dashboard') {
  const session = new Map();
  const ctx = {
    ipop: {}, URL, Date, navigator: { userAgent: 'Windows NT 10.0' }, location: { href },
    sessionStorage: { getItem: k => session.get(k) ?? null, setItem: (k, v) => session.set(k, String(v)), removeItem: k => session.delete(k) },
    document: { visibilityState: 'visible', hasFocus: () => true },
  };
  ctx.window = { location: ctx.location, close() { ctx.closeCalls = (ctx.closeCalls || 0) + 1; this.closed = ctx.allowClose === true; }, closed: false };
  ctx.launched = [];
  // Any assignment of a custom scheme to location.href would be a protocol launch.
  Object.defineProperty(ctx.window.location, 'assign', { value: u => ctx.launched.push(u) });
  vm.runInNewContext(source, ctx);
  return ctx;
}

test('1.4.2 never launches ipop-notifier:// from the web: the old prepare/complete/callback paths are gone', () => {
  const b = browser();
  for (const name of ['prepare', 'complete', 'consumeCallback', 'claim', 'connected', 'forget', 'secret'])
    assert.equal(b.ipop.notifierLink[name], undefined, name);
  const code = source.replace(/^\s*\/\/.*$/gm, '').replace(/\/\*\*[^]*?\*\//g, '');
  assert.doesNotMatch(code, /location\.href\s*=/);
  assert.doesNotMatch(code, /ipop-notifier/);
  assert.equal(b.location.href, 'https://web.example/dashboard');
});

test('only a connect tab the app opened (&opened=1) counts as opened, for ten minutes', () => {
  assert.equal(browser('https://web.example/notifier/connect?request=x&opened=1').ipop.notifierLink.openedTab(), true);
  assert.equal(browser('https://web.example/notifier/connect?request=x').ipop.notifierLink.openedTab(), false);
  assert.equal(browser('https://web.example/dashboard?opened=1').ipop.notifierLink.openedTab(), false);
  const b = browser();
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now() - 601000);
  assert.equal(b.ipop.notifierLink.openedTab(), false);
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now());
  b.ipop.notifierLink.forgetOpened();
  assert.equal(b.ipop.notifierLink.openedTab(), false);
});

test('close tries window.close and reports when the browser keeps the tab', () => {
  const b = browser('https://web.example/notifier/connect?request=x&opened=1');
  assert.equal(b.ipop.notifierLink.close(), false);
  assert.equal(b.closeCalls, 1);
  assert.equal(b.ipop.notifierLink.openedTab(), false);
  const c = browser(); c.allowClose = true;
  assert.equal(c.ipop.notifierLink.close(), true);
});

test('supported only on Windows desktop, visible only when in front', () => {
  const b = browser();
  assert.equal(b.ipop.notifierLink.supported(), true);
  assert.equal(b.ipop.notifierLink.visible(), true);
  b.document.visibilityState = 'hidden';
  assert.equal(b.ipop.notifierLink.visible(), false);
  b.navigator.userAgent = 'Mozilla/5.0 (Linux; Android 14) Mobile';
  assert.equal(b.ipop.notifierLink.supported(), false);
});
