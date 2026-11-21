import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TaskList } from '@tiptap/extension-list';
import { RichTaskItem } from '../src/rich.js';
import { todoDueItems } from '../src/nodes.js';

function docOf(items) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  const el = win.document.createElement('div'); win.document.body.appendChild(el);
  const content = { type: 'doc', content: [{ type: 'taskList', content: items.map(([text, attrs]) => ({ type: 'taskItem', attrs: { checked: false, ...attrs }, content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) }] };
  const editor = new Editor({ element: el, content, extensions: [StarterKit, TaskList, RichTaskItem.configure({ nested: true })] });
  return { editor, win };
}

test('only to-dos with both a person and a due date are listed, with their done state', () => {
  const { editor, win } = docOf([
    ['Order SSD', { assigneeId: 'u1', due: '2026-12-01' }],
    ['No person', { due: '2026-12-01' }],
    ['No date', { assigneeId: 'u1' }],
    ['Finished', { assigneeId: 'u2', due: '2026-12-02', checked: true }],
  ]);
  try {
    const items = todoDueItems(editor.state.doc);
    assert.deepEqual(items.map(i => [i.text, i.assigneeId, i.due, i.done]), [['Order SSD', 'u1', '2026-12-01', false], ['Finished', 'u2', '2026-12-02', true]]);
    assert.ok(items.every(i => i.taskId === null));
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('an item keeps the same key when the page is saved again, and changes it when its date or person changes', () => {
  const a = docOf([['Order SSD', { assigneeId: 'u1', due: '2026-12-01' }]]);
  const b = docOf([['Order SSD', { assigneeId: 'u1', due: '2026-12-01' }]]);
  const moved = docOf([['Order SSD', { assigneeId: 'u1', due: '2026-12-05' }]]);
  const other = docOf([['Order SSD', { assigneeId: 'u2', due: '2026-12-01' }]]);
  try {
    const key = e => todoDueItems(e.editor.state.doc)[0].key;
    assert.equal(key(a), key(b));
    assert.notEqual(key(a), key(moved));
    assert.notEqual(key(a), key(other));
    assert.ok(key(a).length <= 64);
  } finally { for (const x of [a, b, moved, other]) { x.editor.destroy(); x.win.happyDOM.abort(); } }
});

test('two identical items on one page get different keys, stable in order', () => {
  const { editor, win } = docOf([['Call', { assigneeId: 'u1', due: '2026-12-01' }], ['Call', { assigneeId: 'u1', due: '2026-12-01' }]]);
  try {
    const [x, y] = todoDueItems(editor.state.doc);
    assert.notEqual(x.key, y.key);
    assert.deepEqual(todoDueItems(editor.state.doc).map(i => i.key), [x.key, y.key]);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

import { todoTaskItems } from '../src/nodes.js';

test('a to-do turned into a task is reported with its done state, even without a person or date', () => {
  const t1 = '11111111-1111-1111-1111-111111111111', t2 = '22222222-2222-2222-2222-222222222222';
  const { editor, win } = docOf([
    ['Linked, open', { taskId: t1 }],
    ['Linked, done', { taskId: t2, checked: true }],
    ['Plain', {}],
  ]);
  try {
    assert.deepEqual(todoTaskItems(editor.state.doc), [{ taskId: t1, done: false }, { taskId: t2, done: true }]);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('two to-dos pointing at one task: the task counts as done when either is ticked', () => {
  const t = '33333333-3333-3333-3333-333333333333';
  const { editor, win } = docOf([['A', { taskId: t }], ['B', { taskId: t, checked: true }]]);
  try { assert.deepEqual(todoTaskItems(editor.state.doc), [{ taskId: t, done: true }]); } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('the task id survives HTML round trips and a junk id is dropped', () => {
  const { editor, win } = docOf([['Linked', { taskId: '44444444-4444-4444-4444-444444444444', taskName: 'Q4 plan' }]]);
  try {
    const html = editor.getHTML();
    assert.match(html, /data-task-id="44444444-4444-4444-4444-444444444444"/);
    editor.commands.setContent('<ul data-type="taskList"><li data-type="taskItem" data-checked="false" data-task-id="not-a-guid"><p>x</p></li></ul>');
    assert.deepEqual(todoTaskItems(editor.state.doc), []);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('a to-do with a task and a date is listed with its task id even without a person', () => {
  const t = '55555555-5555-5555-5555-555555555555';
  const { editor, win } = docOf([['Linked, no person', { due: '2026-12-05', taskId: t }], ['Linked, no date', { taskId: t }]]);
  try {
    const items = todoDueItems(editor.state.doc);
    assert.deepEqual(items.map(i => [i.text, i.assigneeId, i.taskId]), [['Linked, no person', null, t]]);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});
