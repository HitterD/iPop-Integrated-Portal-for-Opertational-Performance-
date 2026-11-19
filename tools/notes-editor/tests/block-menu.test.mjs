import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@tiptap/pm/state';
import { getSchema } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { menuMatch, moveBlockTr, currentTurn } from '../src/block-menu.js';

const schema = getSchema([StarterKit]);
const p = t => schema.node('paragraph', null, [schema.text(t)]);

test('menu search: short words find their action', () => {
  assert.ok(menuMatch('Heading 2 h2 h2 subtitle', 'h2'));
  assert.ok(menuMatch('duplicate copy salin', 'dup'));
  assert.ok(menuMatch('red colour color background warna', 'red'));
  assert.ok(!menuMatch('delete remove hapus', 'red'));
  assert.ok(menuMatch('anything', ''));
});

test('move block up and down; nothing at the edges', () => {
  const doc = schema.node('doc', null, [p('a'), p('b'), p('c')]);
  const st = EditorState.create({ doc });
  const up = moveBlockTr(st, 3, -1);   // "b" starts at 3
  assert.deepEqual(up.doc.content.content.map(n => n.textContent), ['b', 'a', 'c']);
  const down = moveBlockTr(st, 3, 1);
  assert.deepEqual(down.doc.content.content.map(n => n.textContent), ['a', 'c', 'b']);
  assert.equal(moveBlockTr(st, 0, -1), null);
  assert.equal(moveBlockTr(st, 6, 1), null);
});

test('current block type is marked', () => {
  assert.equal(currentTurn(schema.node('heading', { level: 2 }, [schema.text('x')])), 'h2');
  assert.equal(currentTurn(p('x')), 'text');
});
