// Run with: node --test tests/notifier-link.test.cjs
// iPop Notifier "Sign in with browser" (1.4.3, OAuth loopback): the web no longer has any broadcast/claim helpers
// (1.4.2 pushed a Connect card to every tab "on the same address", which behind the proxy was everyone), and the
// Notifier sign-in window keeps one attempt at a time with a paste-code fallback.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const ipop = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8');
const signin = fs.readFileSync(path.join(__dirname, '../src/IPop.Notifier/Ui/signin.js'), 'utf8');
const signinHtml = fs.readFileSync(path.join(__dirname, '../src/IPop.Notifier/Ui/signin.html'), 'utf8');

test('the web has no Notifier broadcast, claim or protocol-launch helpers any more', () => {
  assert.doesNotMatch(ipop, /notifierLink/);
  assert.doesNotMatch(ipop, /ipop-notifier:/);
  assert.doesNotMatch(ipop, /notifierLinkIntent/);
});

function el(id) {
  return {
    id, hidden: false, disabled: false, textContent: '', value: '', innerHTML: '', offsetWidth: 0,
    classList: { set: new Set(), add(c) { this.set.add(c); }, remove(c) { this.set.delete(c); }, contains(c) { return this.set.has(c); },
      toggle(c, on) { if (on === undefined ? !this.set.has(c) : on) this.set.add(c); else this.set.delete(c); } },
    focus() {}, select() {},
  };
}

function window_() {
  const ids = ['nik', 'pw', 'server', 'auto', 'err', 'go', 'browser', 'again', 'hint', 'adv', 'f', 'pasteLink', 'paste', 'pcode', 'pgo', 'perr'];
  const els = Object.fromEntries(ids.map(i => [i, el(i)]));
  els.again.hidden = true; els.pasteLink.hidden = true;
  els.hint.textContent = 'default hint';
  const extra = { '[data-min]': el('min'), '[data-close]': el('close') };
  const body = el('body'); body.dataset = {};
  const handlers = {}, posted = [], timers = [];
  const ctx = {
    document: { querySelector: s => extra[s] || els[s.replace(/^#/, '')], body },
    ICON: { min: '', x: '' },
    app: { post: m => posted.push(m), on: (t, f) => { handlers[t] = f; }, ready() {} },
    setTimeout: (f, ms) => { timers.push({ f, ms }); return timers.length; }, clearTimeout() {},
  };
  vm.runInNewContext(signin, ctx);
  return { els, body, handlers, posted, timers };
}

test('Sign in with browser posts once; clicks while waiting do nothing; paste link appears', () => {
  const w = window_();
  w.els.browser.onclick();
  w.els.browser.onclick();
  assert.deepEqual(w.posted.filter(m => m.type === 'withBrowser').length, 1);
  assert.equal(w.els.browser.disabled, true);
  assert.equal(w.els.browser.textContent, 'Waiting for browser…');
  assert.equal(w.els.pasteLink.hidden, false);
  // "Open browser again" after 20 s.
  assert.equal(w.timers.at(-1).ms, 20000);
  w.timers.at(-1).f();
  assert.equal(w.els.again.hidden, false);
  w.els.again.onclick();
  assert.deepEqual({ ...w.posted.at(-1) }, { type: 'withBrowser', again: true, server: '' });
});

test('Paste code from browser sends the typed code; a refusal shows its message and keeps the field', () => {
  const w = window_();
  w.els.browser.onclick();
  w.els.pasteLink.onclick();
  assert.equal(w.body.classList.contains('pasting'), true);
  w.els.pcode.value = ' abcd-2345 ';
  w.els.pgo.onclick();
  assert.deepEqual({ ...w.posted.at(-1) }, { type: 'pasteCode', code: 'abcd-2345' });
  assert.equal(w.els.pgo.disabled, true);
  w.handlers.pasteResult({ ok: false, message: "That code didn't work." });
  assert.equal(w.els.perr.textContent, "That code didn't work.");
  assert.equal(w.els.pgo.disabled, false);
  assert.equal(w.body.classList.contains('pasting'), true);
});

test('Decline in the browser returns the window to normal with a note', () => {
  const w = window_();
  w.els.browser.onclick();
  w.handlers.browserState({ state: 'opened' });
  assert.match(w.els.hint.textContent, /click Authorize/);
  w.handlers.browserState({ state: 'denied', message: 'Sign-in cancelled in the browser. You can try again or sign in here.' });
  assert.equal(w.els.browser.disabled, false);
  assert.equal(w.els.browser.textContent, 'Sign in with browser');
  assert.equal(w.els.pasteLink.hidden, true);
  assert.match(w.els.hint.textContent, /cancelled in the browser/);
});

test('the sign-in window has the paste field and no Connect-card wording', () => {
  assert.match(signinHtml, /id="pasteLink"[^>]*>Paste code from browser</);
  assert.match(signinHtml, /id="pcode"/);
  assert.doesNotMatch(signin + signinHtml, /click Connect there/);
});
