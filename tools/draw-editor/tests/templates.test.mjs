import test from 'node:test';
import assert from 'node:assert/strict';
import { BOARD_TEMPLATES, TEMPLATE_NAMES, link, inFrames, edgePoint } from '../src/templates.js';

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

test('arrows start and end on the outline of the shapes they join, never inside them', () => {
  const A = { type: 'rectangle', id: 'a', x: 0, y: 0, width: 100, height: 40 };
  const B = { type: 'rectangle', id: 'b', x: 300, y: 0, width: 100, height: 40 };
  const [, , arrow] = link([A, B, { __link: ['a', 'b'], label: 'next' }]);
  assert.equal(arrow.type, 'arrow');
  assert.ok(arrow.x > 100 && arrow.x < 120, `starts just right of A: ${arrow.x}`);
  assert.ok(arrow.x + arrow.width < 300 && arrow.x + arrow.width > 280, `ends just left of B: ${arrow.x + arrow.width}`);
  assert.equal(arrow.label.text, 'next');
  assert.deepEqual([arrow.start.id, arrow.end.id], ['a', 'b']);
});

test('the outline point is right for rectangles, ellipses and diamonds, in every direction', () => {
  const near = (v, w, msg) => assert.ok(Math.abs(v - w) < 0.01, `${msg}: ${v} vs ${w}`);
  const rect = { type: 'rectangle', x: 0, y: 0, width: 100, height: 60 };
  near(edgePoint(rect, 500, 30, 0).x, 100, 'rect right');
  near(edgePoint(rect, -500, 30, 0).x, 0, 'rect left');
  near(edgePoint(rect, 50, 500, 0).y, 60, 'rect bottom');
  near(edgePoint(rect, 50, -500, 0).y, 0, 'rect top');
  const ell = { type: 'ellipse', x: 0, y: 0, width: 200, height: 100 };
  near(edgePoint(ell, 900, 50, 0).x, 200, 'ellipse right');
  near(edgePoint(ell, 100, 900, 0).y, 100, 'ellipse bottom');
  const dia = { type: 'diamond', x: 0, y: 0, width: 200, height: 100 };
  near(edgePoint(dia, 900, 50, 0).x, 200, 'diamond right tip');
  const diag = edgePoint(dia, 100 + 100, 50 + 50, 0);                 // along the 1:2 diagonal the edge is at |dx|/100 + |dy|/50 = 1
  near(Math.abs(diag.x - 100) / 100 + Math.abs(diag.y - 50) / 50, 1, 'diamond edge');
  // gap pushes the point outwards along the same line
  near(edgePoint(rect, 500, 30, 10).x, 110, 'rect right with gap');
  // a shape is never asked for a point towards its own centre (zero vector): it must not produce NaN
  const self = edgePoint(rect, 50, 30, 0);
  assert.ok(Number.isFinite(self.x) && Number.isFinite(self.y));
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
