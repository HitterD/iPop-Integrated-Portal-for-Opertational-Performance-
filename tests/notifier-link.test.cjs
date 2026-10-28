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
