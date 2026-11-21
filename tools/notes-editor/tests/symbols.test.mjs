import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { Symbols, symbolCatalog } from '../src/symbols.js';

function mount(content = '<p></p>') {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content, extensions: [StarterKit, Symbols] });
  return { editor, win };
}
/// Types like a person: one character at a time through the view's text input handler (so input rules run).
function type(editor, text) {
  for (const ch of text) {
    const { from, to } = editor.state.selection;
    const handled = editor.view.someProp('handleTextInput', f => f(editor.view, from, to, ch, () => editor.state.tr.insertText(ch, from, to)));
    if (!handled) editor.view.dispatch(editor.state.tr.insertText(ch, from, to));
  }
}
const run = (typed, content) => {
  const m = mount(content);
  try { m.editor.commands.focus('end'); type(m.editor, typed); return m.editor.getText().trim(); }
  finally { m.editor.destroy(); m.win.happyDOM.abort(); }
};

test('arrows and maths become symbols while typing', () => {
  assert.equal(run('a -> b'), 'a → b');
  assert.equal(run('a <- b'), 'a ← b');
  assert.equal(run('a <-> b'), 'a ↔ b');
  assert.equal(run('a <=> b'), 'a ⇔ b');
  assert.equal(run('a => b'), 'a ⇒ b');
  assert.equal(run('x != y'), 'x ≠ y');
  assert.equal(run('x >= y'), 'x ≥ y');
  assert.equal(run('5 +- 1'), '5 ± 1');
  assert.equal(run('wait...'), 'wait…');
  assert.equal(run('(c) SJA'), '© SJA');
  assert.equal(run('done (v)'), 'done ✓');
});

test('em dash only after a word; "---" at line start is left for the divider rule', () => {
  assert.equal(run('a -- b'), 'a — b');
  assert.ok(!run('--').includes('—'));
});

test('fractions only when they stand alone; dates keep their slashes', () => {
  assert.equal(run('add 1/2 cup'), 'add ½ cup');
  assert.equal(run('1/2/2026'), '1/2/2026');
});

test(':name: symbols; unknown names and plain words are left alone', () => {
  assert.equal(run('25:deg: C'), '25:deg: C'.replace(':deg:', ':deg:')); // glued to a number: not a word boundary
  assert.equal(run('25 :deg: C'), '25 ° C');
  assert.equal(run('to :inf:'), 'to ∞');
  assert.equal(run('deg inf'), 'deg inf');
  assert.equal(run('a :nope: b'), 'a :nope: b');
});

test('nothing changes inside code', () => {
  assert.equal(run('a -> b', '<pre><code></code></pre>'), 'a -> b');
});

test('lettered lists "(a) (b) (c)" keep the letter', () => {
  assert.equal(run('(a) one (b) two (c)'), '(a) one (b) two (c)');
});

test('catalog lists every rule with what to type', () => {
  const c = symbolCatalog();
  assert.ok(c.length >= 40);
  assert.ok(c.find(x => x.typed === '->' && x.symbol === '→'));
  assert.ok(c.find(x => x.typed === ':deg:' && x.symbol === '°'));
});

test('Backspace right after a replacement brings back what was typed', () => {
  const m = mount();
  try {
    m.editor.commands.focus('end'); type(m.editor, 'a ->');
    assert.equal(m.editor.getText().trim(), 'a →');
    assert.ok(m.editor.commands.undoInputRule());
    assert.equal(m.editor.getText().trim(), 'a ->');
  } finally { m.editor.destroy(); m.win.happyDOM.abort(); }
});

test('"[] " at the start of a line makes a to-do (TaskItem rule)', async () => {
  const { TaskList, TaskItem } = await import('@tiptap/extension-list');
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content: '<p></p>', extensions: [StarterKit, TaskList, TaskItem, Symbols] });
  try { editor.commands.focus('end'); type(editor, '[] x'); assert.equal(editor.getJSON().content[0].type, 'taskList'); }
  finally { editor.destroy(); win.happyDOM.abort(); }
});
