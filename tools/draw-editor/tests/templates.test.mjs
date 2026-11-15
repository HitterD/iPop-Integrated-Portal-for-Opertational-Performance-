import test from 'node:test';
import assert from 'node:assert/strict';
import { BOARD_TEMPLATES, TEMPLATE_NAMES, link, inFrames } from '../src/templates.js';

const kinds = els => els.reduce((o, e) => (o[e.type] = (o[e.type] || 0) + 1, o), {});

test('every template builds, has a name, and has no arrow pointing at a missing shape', () => {
  for (const key of Object.keys(TEMPLATE_NAMES)) {
    const els = BOARD_TEMPLATES[key]();
    assert.ok(els.length > 3, key);
    const ids = new Set(els.filter(e => e.id).map(e => e.id));
    for (const e of els.filter(x => x.type === 'arrow')) {
      assert.ok(ids.has(e.start.id) && ids.has(e.end.id), `${key}: arrow ${e.start.id} -> ${e.end.id}`);
    }
  }
  assert.deepEqual(Object.keys(BOARD_TEMPLATES).sort(), Object.keys(TEMPLATE_NAMES).sort());
});

test('arrows run centre to centre between the shapes they join', () => {
  const [a, b, arrow] = link([
    { type: 'rectangle', id: 'a', x: 0, y: 0, width: 100, height: 40 },
    { type: 'rectangle', id: 'b', x: 300, y: 100, width: 100, height: 40 },
    { __link: ['a', 'b'], label: 'next' },
  ]);
  assert.equal(arrow.type, 'arrow');
  assert.deepEqual([arrow.x, arrow.y], [50, 20]);
  assert.deepEqual([arrow.width, arrow.height], [300, 100]);
  assert.equal(arrow.label.text, 'next');
  assert.deepEqual([arrow.start.id, arrow.end.id], ['a', 'b']);
  assert.equal(a.id, 'a'); assert.equal(b.id, 'b');
});

test('an arrow to a shape that does not exist is a build error, not a silent half-board', () => {
  assert.throws(() => link([{ type: 'rectangle', id: 'a', x: 0, y: 0, width: 10, height: 10 }, { __link: ['a', 'ghost'] }]), /missing shape/);
});

test('frames list exactly the shapes that sit fully inside them', () => {
  const items = inFrames([
    { type: 'frame', id: 'f', x: 0, y: 0, width: 200, height: 200, children: [] },
    { type: 'rectangle', id: 'in', x: 20, y: 20, width: 50, height: 50 },
    { type: 'rectangle', id: 'half', x: 180, y: 20, width: 50, height: 50 },
    { type: 'rectangle', id: 'out', x: 400, y: 0, width: 50, height: 50 },
  ]);
  assert.deepEqual(items[0].children, ['in']);
});

test('the retro board has three frames, five notes and a connector; the flowchart has a decision diamond', () => {
  const retro = kinds(BOARD_TEMPLATES.retro());
  assert.equal(retro.frame, 3); assert.equal(retro.rectangle, 5); assert.equal(retro.arrow, 1);
  const flow = kinds(BOARD_TEMPLATES.flow());
  assert.equal(flow.diamond, 1); assert.equal(flow.arrow, 6);
  const brain = kinds(BOARD_TEMPLATES.brainstorm());
  assert.equal(brain.ellipse, 1); assert.equal(brain.arrow, 6);
});
