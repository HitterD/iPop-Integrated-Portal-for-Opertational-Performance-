import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

// Exercise the actual WebView script through its DOM/bridge boundary without a Windows install.
test('a blocked or failed installation restores both buttons and displays actionable feedback', () => {
  const elements = new Map();
  const handlers = new Map();
  const posted = [];
  const element = selector => {
    if (!elements.has(selector)) elements.set(selector, { disabled: false, focus() {} });
    return elements.get(selector);
  };
  runInNewContext(readFileSync(new URL('../src/IPop.Notifier/Ui/update.js', import.meta.url), 'utf8'), {
    document: { querySelector: element },
    ICON: { min: '', x: '' }, IC: () => '',
    app: { post: m => posted.push(m), on: (type, handler) => handlers.set(type, handler), ready() {} },
  });
  element('#now').onclick();
  assert.equal(element('#now').disabled, true);
  assert.equal(element('#later').disabled, true);
  assert.equal(posted[0].type, 'now');

  handlers.get('installBlocked')({ message: 'Finish your call, then try again.' });
  assert.equal(element('#now').disabled, false);
  assert.equal(element('#later').disabled, false);
  assert.equal(element('#now').textContent, 'Update now');
  assert.equal(element('#note').textContent, 'Finish your call, then try again.');
  element('#now').onclick();
  assert.equal(posted.length, 2);
});
