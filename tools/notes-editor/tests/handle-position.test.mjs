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
