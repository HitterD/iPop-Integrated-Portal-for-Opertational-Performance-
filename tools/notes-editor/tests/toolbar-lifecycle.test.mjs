import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { createToolbar } from '../src/menus.js';

test('delayed blur never touches an editor view after toolbar disposal', () => {
  const win = new Window();
  globalThis.document = win.document;
  const previous = globalThis.setTimeout;
  const pending = [];
  globalThis.setTimeout = callback => { pending.push(callback); return pending.length; };
  const handlers = {};
  const editor = { on: (name, callback) => handlers[name] = callback, get view() { throw new Error('view disposed'); } };
  try {
    const toolbar = createToolbar(editor, { canComment: () => true });
    handlers.blur({ event: {} });
    toolbar.destroy();
    assert.doesNotThrow(() => pending.forEach(callback => callback()));
  } finally { globalThis.setTimeout = previous; win.happyDOM.abort(); }
});
