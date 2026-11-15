import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { NoteToc, HeadingFlash, flashKey } from '../src/nodes.js';

function mount(content) {
  const win = new Window();
  globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser'])
    if (win[k]) globalThis[k] = win[k];
  const el = win.document.createElement('div'); win.document.body.appendChild(el);
  const editor = new Editor({ element: el, content, extensions: [StarterKit.configure({ heading: { levels: [1, 2, 3] } }), NoteToc, HeadingFlash] });
  return { editor, win, el };
}

const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] });
const p = text => ({ type: 'paragraph', content: [{ type: 'text', text }] });

test('the contents block lists the headings and follows edits', () => {
  const { editor, win, el } = mount({ type: 'doc', content: [{ type: 'noteToc' }, h(1, 'Intro'), p('x'), h(2, 'Steps')] });
  try {
    const items = () => [...el.querySelectorAll('.nt-toc a')].map(a => `${a.className}:${a.textContent}`);
    assert.deepEqual(items(), ['l1:Intro', 'l2:Steps']);
    editor.commands.insertContentAt(editor.state.doc.content.size, h(3, 'Later'));
    assert.deepEqual(items(), ['l1:Intro', 'l2:Steps', 'l3:Later']);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('an empty page shows a hint instead of an empty box', () => {
  const { editor, win, el } = mount({ type: 'doc', content: [{ type: 'noteToc' }, p('no headings')] });
  try { assert.match(el.querySelector('.nt-toc').textContent, /Add headings/); }
  finally { editor.destroy(); win.happyDOM.abort(); }
});

test('headings in the contents block carry their document position, and the block holds no data of its own', () => {
  const { editor, win, el } = mount({ type: 'doc', content: [{ type: 'noteToc' }, h(1, 'Intro')] });
  try {
    const a = el.querySelector('.nt-toc a');
    const pos = Number(a.dataset.pos);
    assert.equal(editor.state.doc.nodeAt(pos).type.name, 'heading');
    assert.deepEqual(editor.getJSON().content[0], { type: 'noteToc' });
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('the highlight is a decoration: it survives re-renders and clears when told to', () => {
  const { editor, win, el } = mount({ type: 'doc', content: [h(1, 'Intro'), p('x'), h(2, 'Steps')] });
  try {
    let pos = 0; editor.state.doc.descendants((n, p0) => { if (n.type.name === 'heading' && n.textContent === 'Steps') pos = p0; });
    const node = editor.state.doc.nodeAt(pos);
    editor.view.dispatch(editor.state.tr.setMeta(flashKey, { from: pos, to: pos + node.nodeSize }));
    assert.equal(el.querySelectorAll('.nt-flash').length, 1);
    assert.equal(el.querySelector('.nt-flash').textContent, 'Steps');
    editor.commands.insertContentAt(1, 'typing before it ');          // a re-render with the decoration mapped
    assert.equal(el.querySelector('.nt-flash').textContent, 'Steps');
    editor.view.dispatch(editor.state.tr.setMeta(flashKey, null));
    assert.equal(el.querySelectorAll('.nt-flash').length, 0);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});
