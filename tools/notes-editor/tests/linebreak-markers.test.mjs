import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TaskList, TaskItem } from '@tiptap/extension-list';
import { LineBreakMarkers, markerKind } from '../src/linebreak-markers.js';

function mount(json) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content: json, extensions: [StarterKit, TaskList, TaskItem.configure({ nested: true }), LineBreakMarkers] });
  return { editor, win };
}
function type(editor, text) {
  for (const ch of text) {
    const { from, to } = editor.state.selection;
    const handled = editor.view.someProp('handleTextInput', f => f(editor.view, from, to, ch, () => editor.state.tr.insertText(ch, from, to)));
    if (!handled) editor.view.dispatch(editor.state.tr.insertText(ch, from, to));
  }
}
const p = (...c) => ({ type: 'paragraph', content: c });
const t = x => ({ type: 'text', text: x });
const br = { type: 'hardBreak' };
const done = ({ editor, win }) => { editor.destroy(); win.happyDOM.abort(); };

test('marker text is recognised', () => {
  assert.deepEqual(markerKind('1.'), { type: 'orderedList', start: 1 });
  assert.deepEqual(markerKind('12)'), { type: 'orderedList', start: 12 });
  assert.equal(markerKind('-').type, 'bulletList');
  assert.equal(markerKind('[]').type, 'taskList');
  assert.deepEqual(markerKind('##'), { type: 'heading', level: 2 });
  assert.equal(markerKind('hello'), null);
  assert.equal(markerKind('1.5'), null);
  assert.equal(markerKind('####'), null);
});

test('"1. " on a Shift+Enter line of a numbered item starts a nested numbered list under that item', () => {
  const m = mount({ type: 'doc', content: [{ type: 'orderedList', content: [{ type: 'listItem', content: [p(t('Develop iPop'), br)] }] }] });
  try {
    m.editor.commands.focus('end');
    type(m.editor, '1.'); type(m.editor, ' ');
    const json = m.editor.getJSON();
    const item = json.content[0].content[0];
    assert.equal(item.content[0].type, 'paragraph');
    assert.equal(item.content[0].content.map(c => c.text ?? '<br>').join(''), 'Develop iPop');   // the dangling line break and the marker are gone
    assert.equal(item.content[1].type, 'orderedList');                                           // nested under the item
  } finally { done(m); }
});

test('"- " and "[] " on a Shift+Enter line start a nested bullet list and a to-do list', () => {
  for (const [marker, want] of [['- ', 'bulletList'], ['[] ', 'taskList']]) {
    const m = mount({ type: 'doc', content: [{ type: 'bulletList', content: [{ type: 'listItem', content: [p(t('Item'), br)] }] }] });
    try { m.editor.commands.focus('end'); type(m.editor, marker.trim()); type(m.editor, ' '); assert.equal(m.editor.getJSON().content[0].content[0].content[1].type, want); }
    finally { done(m); }
  }
});

test('in a plain paragraph "# " on a Shift+Enter line makes a heading after the text', () => {
  const m = mount({ type: 'doc', content: [p(t('Intro'), br)] });
  try {
    m.editor.commands.focus('end'); type(m.editor, '##'); type(m.editor, ' ');
    const [first, second] = m.editor.getJSON().content;
    assert.equal(first.type, 'paragraph');
    assert.equal(second.type, 'heading');
    assert.equal(second.attrs.level, 2);
  } finally { done(m); }
});

test('a heading after Shift+Enter also works inside a numbered item', () => {
  const m = mount({ type: 'doc', content: [{ type: 'orderedList', content: [{ type: 'listItem', content: [p(t('Item'), br)] }] }] });
  try {
    m.editor.commands.focus('end'); type(m.editor, '## ');
    const item = m.editor.getJSON().content[0].content[0];
    assert.equal(item.content[1].type, 'heading');
    assert.equal(item.content[1].attrs.level, 2);
  } finally { done(m); }
});

test('Backspace immediately after conversion restores the marker and line break', () => {
  const m = mount({ type: 'doc', content: [p(t('Intro'), br)] });
  try {
    m.editor.commands.focus('end'); type(m.editor, '1. ');
    assert.equal(m.editor.getJSON().content[1].type, 'orderedList');
    const handled = m.editor.view.someProp('handleKeyDown', f => f(m.editor.view, new m.win.KeyboardEvent('keydown', { key: 'Backspace' })));
    assert.equal(handled, true);
    assert.equal(m.editor.getJSON().content.some(n => n.type === 'orderedList'), false);
    assert.equal(m.editor.getJSON().content[0].content[1].type, 'hardBreak');
    assert.equal(m.editor.getJSON().content[0].content.at(-1).text, '1. ');
  } finally { done(m); }
});

test('checked marker with a space is recognised', () => {
  const m = mount({ type: 'doc', content: [p(t('Intro'), br)] });
  try {
    m.editor.commands.focus('end'); type(m.editor, '[ ] ');
    assert.equal(m.editor.getJSON().content[1].type, 'taskList');
  } finally { done(m); }
});

test('typed text that only looks like a marker later in a line is left alone', () => {
  const m = mount({ type: 'doc', content: [p(t('Item'), br, t('see 1.'))] });
  try {
    m.editor.commands.focus('end'); type(m.editor, ' ');
    assert.equal(m.editor.getJSON().content.length, 1);
  } finally { done(m); }
});
