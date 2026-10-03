import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { galleryItems, NoteImage, NoteGallery, NoteBookmark, NoteEmbed, RichTaskItem, tableExtensions } from '../src/rich.js';
import { BlockBackground, Callout, NoteWidget, SubPage, NoteFile } from '../src/nodes.js';
import { TEMPLATES } from '../src/templates.js';

const require = createRequire(import.meta.url);
const { getSchema } = require('@tiptap/core');
const { default: StarterKit } = require('@tiptap/starter-kit');
const { TaskList } = require('@tiptap/extension-list');
const { TextStyle, Color, BackgroundColor } = require('@tiptap/extension-text-style');
const { Details, DetailsSummary, DetailsContent } = require('@tiptap/extension-details');
const { default: Mention } = require('@tiptap/extension-mention');
const { EditorState } = require('@tiptap/pm/state');

const schema = getSchema([
  StarterKit.configure({ heading: { levels: [1, 2, 3] } }),
  TaskList, RichTaskItem.configure({ nested: true }), ...tableExtensions(),
  TextStyle, Color, BackgroundColor, Details, DetailsSummary, DetailsContent,
  BlockBackground, Callout, NoteWidget, SubPage, NoteFile,
  NoteImage, NoteGallery, NoteBookmark, NoteEmbed, Mention,
]);
const paragraph = text => schema.nodes.paragraph.create(null, text ? schema.text(text) : undefined);
const imageAttrs = { id: 'image', name: 'photo', size: 10, caption: 'caption', width: 60, align: 'right' };
const gallery = items => schema.nodes.noteGallery.create({ items: JSON.stringify(items) });

test('galleries retain more than 60 images in reading order', () => {
  const items = Array.from({ length: 75 }, (_, i) => ({ ...imageAttrs, id: String(i) }));
  assert.deepEqual(galleryItems(gallery(items)), items);
});

test('gallery image attributes round-trip without losing captions or formatting', () => {
  const [item] = galleryItems(gallery([imageAttrs]));
  assert.deepEqual(schema.nodes.noteImage.create(item).attrs, schema.nodes.noteImage.create(imageAttrs).attrs);
});

for (const name of ['tableCell', 'tableHeader']) {
  test(`${name} accepts paragraphs and rejects images and nested tables`, () => {
    const type = schema.nodes[name];
    assert.equal(type.createAndFill().firstChild.type.name, 'paragraph');
    assert.notEqual(type.contentMatch.matchType(schema.nodes.paragraph), null);
    for (const blocked of ['noteImage', 'noteGallery', 'noteEmbed', 'table', 'taskList']) {
      assert.equal(type.contentMatch.matchType(schema.nodes[blocked]), null, blocked);
    }
  });
}

function taskState() {
  const task = schema.nodes.taskItem.create({ checked: false }, paragraph('task'));
  const doc = schema.nodes.doc.create(null, [paragraph('first'), schema.nodes.taskList.create(null, [task, task])]);
  return { state: EditorState.create({ schema, doc }), pos: doc.firstChild.nodeSize + 1, task };
}

test('task target maps past text inserted earlier in the document', () => {
  const { state, pos } = taskState();
  const tr = state.tr.insertText('new ', 1);
  const mapped = tr.mapping.mapResult(pos, 1);
  assert.equal(mapped.pos, pos + 4);
  assert.equal(mapped.deleted, false);
  assert.equal(tr.doc.nodeAt(mapped.pos).type.name, 'taskItem');
});

test('deleted task is marked deleted even when another task occupies its former position', () => {
  const { state, pos, task } = taskState();
  const tr = state.tr.delete(pos, pos + task.nodeSize);
  const mapped = tr.mapping.mapResult(pos, 1);
  assert.equal(mapped.deleted, true);
  assert.equal(tr.doc.nodeAt(mapped.pos).type.name, 'taskItem');
});

for (const [name, template] of Object.entries(TEMPLATES)) {
  test(`template ${name} is valid against the Notes editor schema`, () => {
    const node = schema.nodeFromJSON(template());
    assert.equal(node.type.name, 'doc');
    assert.doesNotThrow(() => node.check());
  });
}
