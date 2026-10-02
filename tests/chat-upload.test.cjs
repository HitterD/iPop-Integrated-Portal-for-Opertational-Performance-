const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8');
const start = source.indexOf('  chatBindPaste(');
const end = source.indexOf('  chatUnbindPaste(', start);
assert.ok(start >= 0 && end > start);
const input = { dispatchEvent(e) { assert.equal(e.type, 'change'); this.changed = true; } };
const handlers = {};
const notices = [];
const wrap = {
  isConnected: true, dataset: {}, classList: { add() {}, remove() {} },
  querySelector() { return input; }, addEventListener(name, callback) { handlers[name] = callback; },
};
class DataTransfer {
  constructor() { this.files = []; this.items = { add: file => this.files.push(file) }; }
}
const sandbox = { DataTransfer, Event: class { constructor(type) { this.type = type; } } };
vm.createContext(sandbox);
vm.runInContext('helpers={' + source.slice(start, end) + '};', sandbox);
sandbox.helpers.chatBindPaste(wrap, { invokeMethodAsync(method, text) { notices.push([method, text]); return Promise.resolve(); } });
(async () => {
  const files = Array.from({ length: 7 }, (_, i) => ({ name: i + '.txt', size: i === 0 ? 60 * 1024 * 1024 : 10 }));
  await handlers.drop({ dataTransfer: { files }, preventDefault() {} });
  assert.equal(input.files.length, 7); // .NET validator reports oversize/excess rather than JS silently dropping them
  assert.ok(input.changed);
  input.changed = false; wrap.dataset.uploading = 'true';
  await handlers.drop({ dataTransfer: { files }, preventDefault() {} });
  assert.equal(input.changed, false);
  assert.equal(notices[0][0], 'OnPasteUploadNotice');
  wrap.dataset.uploading = 'false';
  const file = { name: 'clipboard.png', size: 10 };
  await handlers.paste({ clipboardData: { items: [{ kind: 'file', getAsFile() { return file; } }] }, preventDefault() {} });
  assert.equal(input.files[0], file);
  const empty = { name: 'empty.txt', size: 0 };
  await handlers.drop({ dataTransfer: { files: [empty] }, preventDefault() {} });
  assert.equal(input.files[0], empty); // no silent loss: the shared .NET validator explains it
  wrap.isConnected = false; input.changed = false;
  await handlers.drop({ dataTransfer: { files }, preventDefault() {} });
  assert.equal(input.changed, false);
  console.log('Chat upload JS checks passed: streaming, metadata preservation, busy guard, clipboard items.');
})().catch(error => { console.error(error); process.exitCode = 1; });
