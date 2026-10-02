// Run with node tests/emoji-caret.test.cjs. Exercise the real helpers without a browser.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8');
const start = source.indexOf('  _emojiCarets:');
const end = source.indexOf('  // PageCache', start);
assert.ok(start >= 0 && end > start);
const inputs = new Map();
const sandbox = {
  window: {},
  document: { getElementById: id => inputs.get(id) },
  Event: class { constructor(type) { this.type = type; } },
};
vm.runInNewContext('window.ipop={' + source.slice(start, end) + '};', sandbox);
const helpers = sandbox.window.ipop;
function input(value, start, end = start) {
  return {
    value, selectionStart: start, selectionEnd: end,
    setRangeText(text, a, b) { this.value = this.value.slice(0, a) + text + this.value.slice(b); },
    dispatchEvent(e) { assert.equal(e.type, 'input'); this.changed = true; },
    focus() { this.focused = true; },
  };
}

const chat = input('first last', 6);
helpers.emojiSaveCaret(chat);
chat.selectionStart = chat.selectionEnd = 0; // picker takes focus
assert.equal(helpers.emojiInsert(chat, '👍🏽'), 'first 👍🏽last');
assert.ok(chat.changed && chat.focused);
chat.selectionStart = 0; chat.selectionEnd = 5;
helpers.emojiSaveCaret(chat);
assert.equal(helpers.emojiInsert(chat, '😀'), '😀 👍🏽last');

const reply = input('reply', 5);
inputs.set('reply', reply);
helpers.emojiSaveCaret('reply');
assert.equal(helpers.emojiInsert('reply', '🇮🇩'), 'reply🇮🇩');
assert.equal(helpers.emojiInsert('missing', '😀'), '');

const other = input('other', 2);
helpers.emojiSaveCaret(other);
helpers.emojiSaveCaret(reply);
assert.equal(helpers.emojiInsert(other, '👨‍👩‍👧'), 'ot👨‍👩‍👧her');
// Rich-text insertion uses escaped text, never interprets picker content as HTML, and flushes
// through the existing editor change hook so autosave sees the insertion.
const rteStart = source.indexOf('    saveCaret(id) {');
const rteEnd = source.indexOf('    // Templates/signatures', rteStart);
assert.ok(rteStart >= 0 && rteEnd > rteStart);
sandbox.document.createElement = () => ({
  set textContent(value) { this.innerHTML = value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;'); },
});
vm.runInNewContext('window.rte={' + source.slice(rteStart, rteEnd) + '};', sandbox);
const rte = sandbox.window.rte;
let inserted, saved = false, focused = false, flushed = false;
rte._eds = { mail: { ed: { s: { insertHTML: value => inserted = value } } } };
rte._saveRange = () => saved = true;
rte._focus = () => focused = true;
rte._flush = id => { assert.equal(id, 'mail'); flushed = true; };
rte.saveCaret('mail');
rte.insertText('mail', '👍 <script>');
assert.ok(saved && focused && flushed);
assert.equal(inserted, '👍 &lt;script&gt;');
rte.insertText('missing', '😀');
console.log('Emoji caret checks passed: saved cursor, selection replacement, Unicode, editor isolation, escaped rich text, change hook.');
