import test from 'node:test';
import assert from 'node:assert/strict';
import { handleClearsMarker, markerWidth, MARKER_GAP } from '../src/handle-position.js';

const li = pad => ({ parentElement: { pad } });
const getStyle = el => ({ paddingLeft: `${el.pad}px` });

test('a list item moves the handle left of its marker (the list padding plus a gap)', () => {
  const mw = handleClearsMarker(() => ({ type: 'listItem', el: li(24) }), { getStyle });
  assert.deepEqual(mw.fn({ x: 100 }), { x: 100 - 24 - MARKER_GAP });
});

test('a to-do item clears its checkbox the same way', () => {
  const mw = handleClearsMarker(() => ({ type: 'taskItem', el: li(30) }), { getStyle });
  assert.deepEqual(mw.fn({ x: 200 }), { x: 200 - 30 - MARKER_GAP });
});

test('paragraphs, headings and missing elements keep the default position', () => {
  for (const cur of [{ type: 'paragraph', el: li(24) }, { type: 'heading', el: li(24) }, { type: 'listItem', el: null }, null]) {
    assert.deepEqual(handleClearsMarker(() => cur, { getStyle }).fn({ x: 50 }), {});
  }
});

test('marker width is the list padding, never negative', () => {
  assert.equal(markerWidth(li(24), getStyle), 24);
  assert.equal(markerWidth(li(-5), getStyle), 0);
  assert.equal(markerWidth({ parentElement: null }, getStyle), 0);
});

// Regression (Paket C): with TipTap's allowedContainers, top-level paragraphs/headings never got the handle back once it was
// hidden (after typing). The rule must keep every top-level block and allow rows only inside the listed containers.
import { getSchema } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { nestedHandleRule } from '../src/handle-position.js';
const schema = getSchema([StarterKit]);
const doc = schema.nodeFromJSON({ type: 'doc', content: [
  { type: 'paragraph', content: [{ type: 'text', text: 'top' }] },
  { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'row' }] }] }] },
  { type: 'codeBlock', content: [{ type: 'text', text: 'x' }] },
] });
const rule = nestedHandleRule(['bulletList', 'orderedList']);
const at = (pos, depth) => rule.evaluate({ depth, $pos: doc.resolve(pos) });

test('top-level blocks always keep the drag handle (depth 1)', () => {
  assert.equal(at(2, 1), 0);          // the paragraph
  assert.equal(at(doc.content.size - 2, 1), 0);   // the code block
});

test('rows inside an allowed container get their own handle; nested blocks elsewhere do not', () => {
  const inRow = 7 + 3;                // inside the list item's paragraph
  assert.equal(at(inRow, 2), 0);      // listItem (depth 2), parent bulletList is allowed
  assert.equal(at(inRow, 3), 0);      // its paragraph: ancestor bulletList is allowed (TipTap's listItemFirstChild rule removes it)
  const only = nestedHandleRule(['orderedList']);
  assert.equal(only.evaluate({ depth: 2, $pos: doc.resolve(inRow) }), 1000);
});

// Regression (Paket C, UI): the handle was visible from the start and sat on the first line of every page until the first hover.
import { Window } from 'happy-dom';
import { createHandle } from '../src/menus.js';
test('the drag handle starts hidden (TipTap shows it on hover)', () => {
  const win = new Window();
  globalThis.document = win.document;
  try {
    const h = createHandle({});
    assert.equal(h.style.visibility, 'hidden');
    assert.equal(h.style.pointerEvents, 'none');
  } finally { win.happyDOM.abort(); }
});
