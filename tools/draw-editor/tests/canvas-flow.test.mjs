// node --test tests/canvas-flow.test.mjs — whiteboard round 3: flowchart stencils, check, step numbers, layout, routing, import.
import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { create } from '../src/canvas/model.js';
import { STENCILS, STENCIL, stencilPath, stencilTextBox, lanesOf, laneAt, FLOW_TYPES } from '../src/canvas/stencils.js';
import { checkFlow, stepNumbers, nextHints, placeNext, layoutFlow, elbowRoute, waypointRoute, pathWithJumps, pointAlong, fractionAt, flowOrder, graph, hasFlowchart } from '../src/canvas/flow.js';
import { fromMermaid, fromDrawioModel, drawioModel } from '../src/canvas/importers.js';

const node = (type, x, y, text = '') => create(type, { x, y, w: STENCIL[type]?.w || 180, h: STENCIL[type]?.h || 72, text });
const conn = (a, b, label) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'elbow', label });

test('every stencil draws an outline and has a text box', () => {
  for (const [type] of STENCILS) {
    const p = stencilPath(type, 10, 20, 200, 100);
    assert.ok(p && typeof p.d === 'string' && p.d.startsWith('M'), type);
    assert.ok(!/NaN|undefined/.test(p.d + (p.extra || '')), `${type} has no NaN`);
    const tb = stencilTextBox(type, 10, 20, 200, 100);
    assert.ok(tb.w > 0 && tb.h > 0, type);
  }
  assert.equal(stencilPath('rect', 0, 0, 10, 10), null, 'basic shapes are not stencils');
});

test('a complete flowchart passes the check and gets step numbers in flow order', () => {
  const s = node('terminator', 0, 0, 'Start'), a = node('process', 0, 100, 'Raise PR'), d = node('decision', 0, 200, 'Approved?');
  const yes = node('process', 0, 350, 'Make PO'), no = node('process', 300, 220, 'Return'), e = node('terminator', 0, 450, 'End');
  const els = [s, a, d, yes, no, e, conn(s, a), conn(a, d), conn(d, yes, 'Yes'), conn(d, no, 'No'), conn(yes, e), conn(no, e)];
  assert.deepEqual(checkFlow(els), []);
  const nums = stepNumbers(els);
  assert.equal(nums.get(a.id), 1);
  assert.equal(nums.get(yes.id), 2, 'the Yes branch is numbered before the No branch');
  assert.equal(nums.get(no.id), 3);
  assert.equal(nums.has(d.id), false, 'decisions are not steps');
  assert.equal(hasFlowchart(els), true);
  assert.equal(hasFlowchart([create('rect', { x: 0, y: 0, w: 1, h: 1 })]), false);
});

test('the check finds the usual mistakes', () => {
  const a = node('process', 0, 0, 'Check'), d = node('decision', 0, 100, 'OK?'), lonely = node('process', 500, 0, 'Lonely');
  const issues = checkFlow([a, d, lonely, conn(a, d)]);
  const text = issues.map(i => i.text).join('\n');
  assert.match(text, /no Start/);
  assert.match(text, /no End/);
  assert.match(text, /"OK\?" needs two ways out/);
  assert.match(text, /"Lonely" is not connected/);
  assert.equal(issues.find(i => /no Start/.test(i.text)).level, 'bad');
});

test('hints suggest the next step', () => {
  const s = node('terminator', 0, 0, 'Start'), d = node('decision', 0, 100, 'OK?'), e = node('terminator', 0, 300, 'End');
  assert.deepEqual(nextHints(s, [s]).filter(h => h.kind === 'add').map(h => h.label), ['+ Process']);
  assert.deepEqual(nextHints(d, [d]).filter(h => h.kind === 'add').map(h => h.label), ['+ Yes', '+ No']);
  const y = node('process', 0, 250, 'Do');
  assert.deepEqual(nextHints(d, [d, y, conn(d, y, 'Yes')]).filter(h => h.kind === 'add').map(h => h.label), ['+ No']);
  assert.equal(nextHints(e, [e])[0].label, 'The flow ends here.');
  assert.deepEqual(nextHints(create('rect', { x: 0, y: 0, w: 1, h: 1 }), []), [], 'no hints on plain shapes');
});

test('the next shape goes beside the source and never on top of another', () => {
  const src = node('process', 0, 0), busy = node('process', 0, 136);
  const p = placeNext(src, 'b', { w: 180, h: 72 }, [src, busy]);
  assert.ok(p.y >= 72 + 64 - 1);
  assert.ok(p.x >= busy.x + busy.w || p.y >= busy.y + busy.h || p.x + p.w <= busy.x, 'moved off the busy spot');
  const r = placeNext(src, 'r', { w: 180, h: 72 }, [src]);
  assert.equal(r.x, 180 + 64); assert.equal(r.y, 0);
});

test('tidy up: layers top to bottom, loops back do not break it', () => {
  const s = node('terminator', 500, 500, 'Start'), a = node('process', 20, 900, 'A'), d = node('decision', 900, 20, 'OK?'), b = node('process', 300, 300, 'B');
  const els = [s, a, d, b, conn(s, a), conn(a, d), conn(d, b, 'Yes'), conn(d, a, 'No')];
  const pos = layoutFlow(els, { origin: { x: 0, y: 0 } });
  const at = id => pos.get(id) || els.find(e => e.id === id);
  assert.ok(at(s.id).y < at(a.id).y && at(a.id).y < at(d.id).y && at(d.id).y < at(b.id).y);
  const lr = layoutFlow(els, { dir: 'LR', origin: { x: 0, y: 0 } });
  const at2 = id => lr.get(id) || els.find(e => e.id === id);
  assert.ok(at2(s.id).x < at2(a.id).x && at2(a.id).x < at2(d.id).x);
  assert.deepEqual(flowOrder(graph(els)).map(n => n.text), ['Start', 'A', 'OK?', 'B']);
});

test('elbow routes go around a shape in the way, waypoints make corners, jumps appear at crossings', () => {
  const a = { x: 0, y: 50 }, b = { x: 400, y: 50 };
  const wall = { x: 150, y: 0, w: 100, h: 100 };
  const pts = elbowRoute(a, 'r', b, 'l', [wall]);
  const crosses = pts.slice(1).some((p, i) => { const q = pts[i]; return Math.min(p.x, q.x) < wall.x + wall.w && Math.max(p.x, q.x) > wall.x && Math.min(p.y, q.y) < wall.y + wall.h && Math.max(p.y, q.y) > wall.y; });
  assert.equal(crosses, false, 'the route avoids the shape');
  assert.ok(pts.slice(1).every((p, i) => Math.abs(p.x - pts[i].x) < 0.6 || Math.abs(p.y - pts[i].y) < 0.6), 'only straight segments');
  const w = waypointRoute({ x: 0, y: 0 }, { x: 100, y: 100 }, [{ x: 50, y: 20 }]);
  assert.ok(w.every((p, i) => i === 0 || Math.abs(p.x - w[i - 1].x) < 0.6 || Math.abs(p.y - w[i - 1].y) < 0.6));
  const d = pathWithJumps([{ x: 0, y: 50 }, { x: 200, y: 50 }], [[{ x: 100, y: 0 }, { x: 100, y: 100 }]]);
  assert.match(d, /A6 6/, 'a jump arc where the lines cross');
  const mid = pointAlong([{ x: 0, y: 0 }, { x: 100, y: 0 }, { x: 100, y: 100 }], 0.5);
  assert.deepEqual(mid, { x: 100, y: 0 });
  assert.ok(Math.abs(fractionAt([{ x: 0, y: 0 }, { x: 100, y: 0 }], { x: 25, y: 10 }) - 0.25) < 0.01);
});

test('swimlanes: lanes split the box, a point finds its lane', () => {
  const lane = create('swimlane', { x: 0, y: 0, w: 900, h: 300, lanes: [{ id: 'a', name: 'Warehouse', size: 1 }, { id: 'b', name: 'Purchasing', size: 2 }] });
  const ls = lanesOf(lane);
  assert.equal(ls[0].box.h, 100); assert.equal(ls[1].box.y, 100); assert.equal(ls[1].box.h, 200);
  assert.equal(laneAt(lane, 400, 50), 0); assert.equal(laneAt(lane, 400, 250), 1); assert.equal(laneAt(lane, 1000, 50), -1);
  const v = lanesOf({ ...lane, orient: 'vertical' });
  assert.equal(v[1].box.x, 300);
});

test('Mermaid: shapes, labels, chains and loops become a laid-out diagram', () => {
  const r = fromMermaid(`flowchart TD
    A([Start]) --> B[Receive request]
    B --> C{Complete?}
    C -->|Yes| D[[Process order]]
    C -- No --> E[/Ask for details/]
    E --> B
    D --> F[(SAP)] --> G([End])`);
  assert.equal(r.error, null);
  const shapes = r.elements.filter(e => e.type !== 'connector');
  const byText = t => shapes.find(e => e.text === t);
  assert.equal(byText('Start').type, 'terminator');
  assert.equal(byText('Complete?').type, 'decision');
  assert.equal(byText('Process order').type, 'subprocess');
  assert.equal(byText('Ask for details').type, 'data');
  assert.equal(byText('SAP').type, 'database');
  const lines = r.elements.filter(e => e.type === 'connector');
  assert.equal(lines.length, 7);
  assert.deepEqual(lines.filter(l => l.label).map(l => l.label).sort(), ['No', 'Yes']);
  assert.ok(byText('Start').y < byText('Receive request').y, 'laid out top to bottom');
  assert.deepEqual(checkFlow(r.elements), []);
  assert.match(fromMermaid('').error, /Paste/);
});

test('draw.io: plain and compressed files', async () => {
  const win = new Window(); globalThis.DOMParser = win.DOMParser; globalThis.atob = s => Buffer.from(s, 'base64').toString('binary');
  const xml = `<mxfile><diagram name="Page-1"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
    <mxCell id="s" value="Start" style="ellipse;whiteSpace=wrap;" vertex="1" parent="1"><mxGeometry x="100" y="40" width="120" height="60" as="geometry"/></mxCell>
    <mxCell id="d" value="Stock&lt;br&gt;ok?" style="rhombus;whiteSpace=wrap;fillColor=#fff2cc;" vertex="1" parent="1"><mxGeometry x="100" y="160" width="120" height="80" as="geometry"/></mxCell>
    <mxCell id="e1" value="Yes" style="edgeStyle=orthogonalEdgeStyle;" edge="1" parent="1" source="s" target="d"><mxGeometry relative="1" as="geometry"/></mxCell>
  </root></mxGraphModel></diagram></mxfile>`;
  const model = await drawioModel(xml, async () => '');
  const r = fromDrawioModel(model, { x: 1000, y: 0 });
  assert.equal(r.error, null);
  const [s, d, c] = r.elements;
  assert.equal(s.type, 'terminator'); assert.equal(s.x, 1000); assert.equal(s.y, 0);
  assert.equal(d.type, 'decision'); assert.equal(d.text, 'Stock\nok?'); assert.equal(d.fill, '#fff2cc'); assert.equal(d.y, 120);
  assert.equal(c.type, 'connector'); assert.equal(c.label, 'Yes'); assert.equal(c.route, 'elbow');
  // compressed: raw-deflate + URI-encoded + base64 (what draw.io writes)
  const { deflateRawSync, inflateRawSync } = await import('node:zlib');
  const inner = xml.match(/<mxGraphModel>[\s\S]*<\/mxGraphModel>/)[0];
  const packed = deflateRawSync(Buffer.from(encodeURIComponent(inner))).toString('base64');
  const m2 = await drawioModel(`<mxfile><diagram name="x">${packed}</diagram></mxfile>`, async bytes => inflateRawSync(Buffer.from(bytes)).toString());
  assert.equal(fromDrawioModel(m2).elements.length, 3);
  await assert.rejects(drawioModel('<nope', async () => ''), /not a draw.io file/);
});

test('flow types cover the flowchart stencils only', () => {
  assert.ok(FLOW_TYPES.has('decision') && FLOW_TYPES.has('terminator'));
  assert.ok(!FLOW_TYPES.has('netPC') && !FLOW_TYPES.has('swimlane'));
});

test('step numbers start at 1 again for each separate flowchart on the board', () => {
  const mk = (dx) => { const s = node('terminator', dx, 0, 'Start'), a = node('process', dx, 100, 'A'), b = node('process', dx, 200, 'B'); return [s, a, b, conn(s, a), conn(a, b)]; };
  const one = mk(0), two = mk(600);
  const nums = stepNumbers([...one, ...two]);
  assert.deepEqual([nums.get(one[1].id), nums.get(one[2].id), nums.get(two[1].id), nums.get(two[2].id)], [1, 2, 1, 2]);
});
