import test from 'node:test';
import assert from 'node:assert/strict';
import { getSchema, Node } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TextStyle, Color } from '@tiptap/extension-text-style';
import { ListNumbering, listNumbers, itemColor, listDecorations } from '../src/lists.js';

const Img = Node.create({ name: 'noteImage', group: 'block', atom: true });
const schema = getSchema([StarterKit, TextStyle, Color, ListNumbering, Img]);
const p = t => ({ type: 'paragraph', content: t ? [{ type: 'text', text: t }] : undefined });
const ol = (n, attrs = {}) => ({ type: 'orderedList', attrs: { start: 1, ...attrs }, content: Array.from({ length: n }, (_, i) => ({ type: 'listItem', content: [p(`i${i}`)] })) });
const img = { type: 'noteImage' };
const firsts = (...c) => listNumbers(schema.nodeFromJSON({ type: 'doc', content: c })).map(x => x.first);

test('a list split by a picture keeps counting: 1-4, picture, 5', () => {
  assert.deepEqual(firsts(ol(4), img, ol(2)), [1, 5]);
  assert.deepEqual(firsts(ol(4), img, img, ol(1), img, ol(1)), [1, 5, 6]);
});

test('a text paragraph in between starts again at 1, unless the list says Continue', () => {
  assert.deepEqual(firsts(ol(3), p('text'), ol(2)), [1, 1]);
  assert.deepEqual(firsts(ol(3), p('text'), ol(2, { numbering: 'continue' })), [1, 4]);
});

test('Restart and Start at win over continuing', () => {
  assert.deepEqual(firsts(ol(3), img, ol(2, { numbering: 'restart' })), [1, 1]);
  assert.deepEqual(firsts(ol(3), img, ol(2, { start: 10 })), [1, 10]);
  assert.deepEqual(firsts(ol(3, { start: 7 }), img, ol(1)), [7, 10]);
});

test('the continued list gets a start decoration; a normal list none', () => {
  const doc = schema.nodeFromJSON({ type: 'doc', content: [ol(2), img, ol(1)] });
  const d = listDecorations(doc).find();
  assert.equal(d.length, 1);
  assert.equal(d[0].type.attrs.start, '3');
});

test('number colour follows a one-colour first line, not a mixed one', () => {
  const red = t => ({ type: 'text', text: t, marks: [{ type: 'textStyle', attrs: { color: 'var(--nt-red)' } }] });
  const item = c => schema.nodeFromJSON({ type: 'listItem', content: [{ type: 'paragraph', content: c }] });
  assert.equal(itemColor(item([red('a'), red('b')])), 'var(--nt-red)');
  assert.equal(itemColor(item([red('a'), { type: 'text', text: 'b' }])), null);
  assert.equal(itemColor(item([{ type: 'text', text: 'b' }])), null);
});
