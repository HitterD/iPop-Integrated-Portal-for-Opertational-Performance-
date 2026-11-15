import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TaskList } from '@tiptap/extension-list';
import { RichTaskItem, TodoExtras } from '../src/rich.js';

function mount(content) {
  const win = new Window();
  globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser'])
    if (win[k]) globalThis[k] = win[k];
  const el = win.document.createElement('div'); win.document.body.appendChild(el);
  const editor = new Editor({
    element: el, content,
    extensions: [StarterKit, TaskList, RichTaskItem.configure({ nested: true }), TodoExtras.configure({})],
  });
  return { editor, win, el };
}

const doc = (n, checked = []) => ({
  type: 'doc', content: [{ type: 'taskList', content: Array.from({ length: n }, (_, i) => ({
    type: 'taskItem', attrs: { checked: checked.includes(i) }, content: [{ type: 'paragraph', content: [{ type: 'text', text: `item ${i}` }] }],
  })) }],
});

test('checking an item stamps completedOn on that item only, and unchecking clears it', () => {
  const { editor, win } = mount(doc(4));
  try {
    const items = () => { const r = []; editor.state.doc.descendants((n, p) => { if (n.type.name === 'taskItem') r.push({ n, p }); }); return r; };
    const second = items()[1];
    editor.view.dispatch(editor.state.tr.setNodeMarkup(second.p, undefined, { ...second.n.attrs, checked: true }));
    const after = items().map(i => i.n.attrs);
    assert.ok(after[1].completedOn, 'checked item gets a completion date');
    assert.equal(after[0].completedOn ?? null, null);
    assert.equal(after[2].completedOn ?? null, null);
    const again = items()[1];
    editor.view.dispatch(editor.state.tr.setNodeMarkup(again.p, undefined, { ...again.n.attrs, checked: false }));
    assert.equal(items()[1].n.attrs.completedOn ?? null, null);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('progress bar shows for 3+ items and follows edits; decorations track editable', () => {
  const { editor, win, el } = mount(doc(3, [0]));
  try {
    assert.match(el.textContent, /1 of 3 done/);
    const it = (() => { let r; editor.state.doc.descendants((n, p) => { if (n.type.name === 'taskItem' && n.attrs.checked === false && !r) r = { n, p }; }); return r; })();
    editor.view.dispatch(editor.state.tr.setNodeMarkup(it.p, undefined, { ...it.n.attrs, checked: true }));
    assert.match(el.textContent, /2 of 3 done/);
    // Typing (no structure change) keeps the bar; a selection-only move must not drop it.
    editor.commands.setTextSelection(3);
    assert.match(el.textContent, /2 of 3 done/);
    // The "more" button exists while editable and vanishes when the editor becomes read-only, with no transaction in between.
    assert.ok(el.querySelector('.nt-tmeta .more'), 'editable: options button present');
    editor.setEditable(false);
    editor.view.updateState(editor.view.state);   // what a re-render does; decorations must notice editable changed
    assert.equal(el.querySelector('.nt-tmeta .more'), null, 'read-only: options button gone');
  } finally { editor.destroy(); win.happyDOM.abort(); }
});
