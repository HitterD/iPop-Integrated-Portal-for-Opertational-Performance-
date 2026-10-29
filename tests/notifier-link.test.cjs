const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8').split('ipop.notifierLink = ')[1];
function browser() {
  const storage = new Map();
  const ctx = { ipop: {}, URL, Uint8Array, crypto: require('node:crypto').webcrypto, btoa: s => Buffer.from(s, 'binary').toString('base64'), Date,
    navigator: { userAgent: 'Windows NT' }, location: { href: 'https://web.example/dashboard' },
    localStorage: { getItem: k => storage.get(k) ?? null, setItem: (k,v) => storage.set(k,v), removeItem: k => storage.delete(k) } };
  ctx.window = { location: ctx.location }; ctx.history = { state: null, replaceState: (_,__,url) => { ctx.cleaned = url; } };
  vm.runInNewContext('ipop.notifierLink = ' + source, ctx);
  return ctx;
}
function callback(ctx, nonce, extra = '') {
  ctx.location.href = `https://web.example/dashboard?notifierNonce=${nonce}&notifierRequest=${'r'.repeat(43)}&notifierChallenge=${'c'.repeat(43)}${extra}`;
}
test('nonce belongs to this browser, is consumed once, and callback secrets leave URL', () => {
  const b = browser(); b.ipop.notifierLink.prepare();
  const pending = JSON.parse(b.localStorage.getItem('ipop.notifierLink.pending'));
  assert.match(b.location.href, /^ipop-notifier:\/\/prepare\?nonce=[\w-]{43}$/);
  callback(b, pending.nonce); assert.ok(b.ipop.notifierLink.consumeCallback());
  assert.equal(b.cleaned, '/dashboard'); assert.equal(b.ipop.notifierLink.consumeCallback(), null);
});
test('foreign/expired nonce and cross-scheme callbacks cannot approve', () => {
  const b = browser(); callback(b, 'x'.repeat(43)); assert.equal(b.ipop.notifierLink.consumeCallback(), null);
  b.localStorage.setItem('ipop.notifierLink.pending', JSON.stringify({nonce:'x'.repeat(43), scheme:'ipop-notifier',at:Date.now()-61000}));
  assert.equal(b.ipop.notifierLink.consumeCallback(), null);
  b.localStorage.setItem('ipop.notifierLink.pending', JSON.stringify({nonce:'x'.repeat(43), scheme:'ipop-notifier',at:Date.now()}));
  callback(b, 'x'.repeat(43), '&notifierScheme=ipop-notifier-qa'); assert.equal(b.ipop.notifierLink.consumeCallback(), null);
});
test('protocol output allowlist rejects arbitrary URLs and requires request proof', () => {
  const b = browser(); const original = b.location.href;
  b.ipop.notifierLink.complete('https://evil.example', 'c'.repeat(43), 'r'.repeat(43)); assert.equal(b.location.href, original);
  b.ipop.notifierLink.complete('ipop-notifier', 'c'.repeat(43), ''); assert.equal(b.location.href, original);
  b.ipop.notifierLink.complete('ipop-notifier-qa', 'c'.repeat(43), 'r'.repeat(43)); assert.match(b.location.href, /^ipop-notifier-qa:\/\/signin\?code=/);
});

// 1.4.0: one tab, prepare from the tab the person is on, close only a tab the app opened.
function browser2() {
  const b = browser(), session = new Map();
  b.sessionStorage = { getItem: k => session.get(k) ?? null, setItem: (k, v) => session.set(k, String(v)), removeItem: k => session.delete(k) };
  const node = () => ({ style: {}, children: [], append(...c) { this.children.push(...c); }, setAttribute() {}, remove() { this.removed = true; } });
  b.added = [];
  b.document = { getElementById: () => null, createElement: node, body: { append: el => b.added.push(el) } };
  b.timers = []; b.setTimeout = fn => b.timers.push(fn);
  b.window.close = () => { b.closed = true; };
  b.Blazor = { navigateTo: url => { b.navigated = url; } };
  return b;
}
test('prepare uses the scheme of the app intent, returns the nonce, and forget drops it', () => {
  const b = browser2();
  const nonce = b.ipop.notifierLink.prepare('ipop-notifier-qa');
  assert.match(b.location.href, /^ipop-notifier-qa:\/\/prepare\?nonce=[\w-]{43}$/);
  assert.equal(JSON.parse(b.localStorage.getItem('ipop.notifierLink.pending')).nonce, nonce);
  b.ipop.notifierLink.forget('x'.repeat(43)); assert.ok(b.localStorage.getItem('ipop.notifierLink.pending'));
  b.ipop.notifierLink.forget(nonce); assert.equal(b.localStorage.getItem('ipop.notifierLink.pending'), null);
  // a handed-off nonce can no longer approve a callback tab
  callback(b, nonce); assert.equal(b.ipop.notifierLink.consumeCallback(), null);
  b.ipop.notifierLink.prepare('https://evil.example'); assert.match(b.location.href, /^ipop-notifier:\/\/prepare/);
});
test('only a tab the app opened counts as opened, for ten minutes', () => {
  const b = browser2();
  assert.equal(b.ipop.notifierLink.openedTab(), false);
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now()); assert.equal(b.ipop.notifierLink.openedTab(), true);
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now() - 601000); assert.equal(b.ipop.notifierLink.openedTab(), false);
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now()); b.ipop.notifierLink.forgetOpened(); assert.equal(b.ipop.notifierLink.openedTab(), false);
});
test('connected shows the check, closes after 2 s, and goes back to the dashboard if the browser keeps the tab', () => {
  const b = browser2();
  b.sessionStorage.setItem('ipop.notifierLink.opened', Date.now());
  b.ipop.notifierLink.connected('Bagastyo');
  assert.equal(b.added.length, 1);
  const text = JSON.stringify(b.added[0]);
  assert.match(text, /iPop Notifier is connected/); assert.match(text, /Signed in as Bagastyo/); assert.match(text, /Closing this tab in 2 s/);
  assert.equal(b.ipop.notifierLink.openedTab(), false);
  assert.equal(b.closed, undefined); b.timers.shift()(); assert.equal(b.closed, true);
  b.timers.shift()(); assert.equal(b.navigated, '/dashboard'); assert.equal(b.added[0].removed, true);
});
