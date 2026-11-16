import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { Details, DetailsContent } from '@tiptap/extension-details';
import { Columns, Column, columnsJson, ToggleSummary, toggleJson } from '../src/extras.js';
import { insertAt } from '../src/menus.js';

function mount(paragraphs) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const content = { type: 'doc', content: paragraphs.map(t => ({ type: 'paragraph', content: [{ type: 'text', text: t }] })) };
  const editor = new Editor({ element, content, extensions: [StarterKit, Columns, Column, Details, ToggleSummary, DetailsContent] });
  return { editor, win };
}
const done = m => { m.editor.destroy(); m.win.happyDOM.abort(); };
// the slash text "/x" sits at the end of the LAST paragraph: its range is the last two characters
const rangeOfSlash = ed => { const end = ed.state.doc.content.size - 1; return { from: end - 2, to: end }; };

test('after inserting columns the cursor is in the FIRST column, whether the slash was on its own line or after text', () => {
  for (const paras of [['/x'], ['intro', '/x'], ['one', 'two', 'three', '/x']]) {
    const m = mount(paras);
    try {
      insertAt(m.editor, rangeOfSlash(m.editor), columnsJson(3));
      m.editor.commands.insertContent('typed');
      const cols = m.editor.state.doc.descendants ? null : null;
      let found = null;
      m.editor.state.doc.descendants(n => { if (n.type.name === 'columns') found = n; });
      assert.ok(found, 'columns block exists');
      assert.equal(found.childCount, 3);
      assert.equal(found.child(0).textContent, 'typed', paras.join('|'));
      assert.equal(found.child(1).textContent, '');
      assert.equal(m.editor.state.doc.firstChild.textContent, paras.length > 1 ? paras[0] : m.editor.state.doc.firstChild.textContent);
    } finally { done(m); }
  }
});

test('after inserting a toggle heading the cursor is in its title with the chosen level', () => {
  for (const paras of [['/x'], ['intro', '/x']]) {
    const m = mount(paras);
    try {
      insertAt(m.editor, rangeOfSlash(m.editor), toggleJson(2));
      m.editor.commands.insertContent('Title');
      let d = null;
      m.editor.state.doc.descendants(n => { if (n.type.name === 'details') d = n; });
      assert.ok(d);
      assert.equal(d.firstChild.textContent, 'Title', paras.join('|'));
      assert.equal(d.firstChild.attrs.level, 2);
    } finally { done(m); }
  }
});
