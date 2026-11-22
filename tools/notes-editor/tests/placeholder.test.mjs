import test from 'node:test';
import assert from 'node:assert/strict';
import { EditorState, TextSelection } from '@tiptap/pm/state';
import { getSchema } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { placeholderDecorations } from '../src/placeholder.js';

const schema = getSchema([StarterKit]);
const p = t => (t ? schema.node('paragraph', null, [schema.text(t)]) : schema.node('paragraph'));
const docOf = (...c) => schema.node('doc', null, c);
const at = (doc, pos) => EditorState.create({ doc, selection: TextSelection.create(doc, pos) });
const count = s => placeholderDecorations(s).find().length;

test('only the empty line with the caret shows the placeholder, however many empty lines there are', () => {
  const doc = docOf(p('a'), p(), p(), p(), p(), p());
  // caret in the 3rd paragraph (positions: "a"=0..3, then 2 each)
  assert.equal(count(at(doc, 6)), 1);
  assert.equal(count(at(doc, 2)), 0);   // caret on the text line: none
});

test('nothing when the page is read-only; a lone empty page shows it without focus', () => {
  const doc = docOf(p());
  assert.equal(placeholderDecorations(at(doc, 1), { editable: false }).find().length, 0);
  assert.equal(placeholderDecorations(at(doc, 1), { focused: false }).find().length, 1);
  assert.equal(placeholderDecorations(at(docOf(p(), p()), 1), { focused: false }).find().length, 0);
});

test('empty headings keep their label wherever the caret is', () => {
  const doc = docOf(schema.node('heading', { level: 2 }), p('x'));
  const d = placeholderDecorations(at(doc, 4)).find();
  assert.equal(d.length, 1);
  assert.equal(d[0].type.attrs['data-placeholder'], 'Heading 2');
});
