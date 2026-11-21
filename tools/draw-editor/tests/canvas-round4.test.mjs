// node --test tests/canvas-round4.test.mjs — whiteboard round 4: UML / network / floor plan stencils, racks, data graphics,
// re-layout, report, xlsx / pptx, version diff, org chart and diagram → tasks.
import test from 'node:test';
import assert from 'node:assert/strict';
import { inflateRawSync } from 'node:zlib';
import { create, isContainer } from '../src/canvas/model.js';
import { STENCILS, STENCIL, STENCIL_CATS, stencilPath, stencilTextBox, rackSlots, rackUnits, isNetDevice } from '../src/canvas/stencils.js';
import { graphicsFor, daysOf, layoutTree, layoutRadial, layoutOrganic, layoutNetwork, findDevice, rackDrop, rackContents, stackList, reportRows, toCsv, toXlsx, toPptx, zip, crc32, diffScenes, diffSummary, orgChart, tasksFromShapes } from '../src/canvas/diagram.js';

void inflateRawSync;
const node = (type, x, y, text = '', extra = {}) => create(type, { x, y, w: STENCIL[type]?.w || 180, h: STENCIL[type]?.h || 72, text, ...extra });
const conn = (a, b, label) => create('connector', { from: { id: a.id }, to: { id: b.id }, route: 'elbow', label });

/// Reads a stored (uncompressed) zip into a Map(name -> string).
function unzip(bytes) {
  const out = new Map(), dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength), dec = new TextDecoder();
  let p = 0;
  while (dv.getUint32(p, true) === 0x04034b50) {
    const size = dv.getUint32(p + 18, true), nl = dv.getUint16(p + 26, true), xl = dv.getUint16(p + 28, true);
    const name = dec.decode(bytes.subarray(p + 30, p + 30 + nl)), body = bytes.subarray(p + 30 + nl + xl, p + 30 + nl + xl + size);
    assert.equal(crc32(body), dv.getUint32(p + 14, true), `crc of ${name}`);
    out.set(name, dec.decode(body)); p += 30 + nl + xl + size;
  }
  return out;
}

test('every new stencil draws without NaN and the new categories are in the panel', () => {
  for (const [type] of STENCILS) {
    const p = stencilPath(type, 10, 20, 200, 120);
    assert.ok(p && p.d.startsWith('M'), type);
    assert.ok(!/NaN|undefined/.test(p.d + (p.extra || '') + (p.dot || '')), `${type} has no NaN`);
    const tb = stencilTextBox(type, 10, 20, 200, 120);
    assert.ok(tb.w > 0 && tb.h > 0, type);
  }
  for (const c of ['UML', 'Org chart', 'Floor plan']) assert.ok(STENCIL_CATS.includes(c));
  for (const c of STENCIL_CATS) assert.ok(STENCILS.some(s => s[2] === c), `${c} has shapes`);
  assert.ok(STENCILS.filter(s => s[2] === 'Network').length >= 25, 'network has the 15 new devices and a rack');
  assert.ok(isContainer({ type: 'rack' }) && isContainer({ type: 'listBox' }) && isContainer({ type: 'fpRoom' }) && isContainer({ type: 'umlPackage' }));
  assert.ok(isNetDevice({ type: 'netNAS' }) && !isNetDevice({ type: 'netCloud' }) && !isNetDevice({ type: 'process' }));
});

test('data graphics follow the rules: done green, late after N days red, in progress orange', () => {
  const late = { data: [{ name: 'Duration', value: '4 days' }, { name: 'Status', value: '' }] };
  const done = { data: [{ name: 'Status', value: 'Done' }] };
  const doing = { data: [{ name: 'Progress', value: '40%' }] };
  assert.equal(graphicsFor(late, { show: 'bar', lateDays: 3 }).kind, 'late');
  assert.equal(graphicsFor(late, { show: 'bar', lateDays: 5 }).kind, 'todo');
  assert.equal(graphicsFor(done, { show: 'icon', lateDays: 3 }).icon, '✓');
  const g = graphicsFor(doing, { show: 'bar', lateDays: 3 });
  assert.equal(g.kind, 'doing'); assert.equal(g.bar, 0.4); assert.equal(g.color, '#FF9500');
  assert.match(graphicsFor(done, { show: 'color' }).fill, /^#34C759/);
  assert.equal(graphicsFor(done, { show: 'off' }), null);
  assert.equal(graphicsFor({}, { show: 'bar' }), null, 'no data, no graphic');
  assert.equal(daysOf('2 h'), 0.25); assert.equal(daysOf('1 week'), 5); assert.equal(daysOf('15 min'), 15 / 480); assert.equal(daysOf('x'), null);
});

test('re-layout: tree puts parents above children, radial puts the root in the middle, organic is stable', () => {
  const g = node('process', 500, 500, 'Goal'), a = node('process', 0, 0, 'Plan'), b = node('process', 900, 900, 'People'), c = node('process', 40, 700, 'Budget');
  const els = [g, a, b, c, conn(g, a), conn(g, b), conn(a, c)];
  const t = layoutTree(els, { origin: { x: 0, y: 0 } });
  const at = id => t.get(id);
  assert.ok(at(g.id).y < at(a.id).y && at(a.id).y < at(c.id).y);
  assert.equal(at(a.id).y, at(b.id).y, 'siblings on one row');
  const gc = at(g.id).x + g.w / 2, kids = [a, b].map(k => at(k.id).x + k.w / 2);
  assert.ok(Math.abs(gc - (kids[0] + kids[1]) / 2) < 1, 'parent centred over its children');
  const r = layoutRadial(els);
  assert.equal(r.has(g.id), false, 'the root stays where it is');
  const d1 = Math.hypot(r.get(a.id).x - g.x, r.get(a.id).y - g.y), d2 = Math.hypot(r.get(c.id).x - g.x, r.get(c.id).y - g.y);
  assert.ok(d2 > d1, 'grandchildren on the outer ring');
  assert.deepEqual([...layoutOrganic(els)], [...layoutOrganic(els)], 'same input, same picture');
});

test('network tidy-up goes Internet → firewall → core → access → devices whatever way the lines were drawn', () => {
  const inet = node('netCloud', 0, 900), fw = node('netFirewall', 0, 0), core = node('netCoreSwitch', 400, 400), acc = node('netAccessSwitch', 800, 0), pc = node('netPC', 0, 400);
  const els = [inet, fw, core, acc, pc, conn(fw, inet), conn(core, fw), conn(acc, core), conn(acc, pc)];
  const p = layoutNetwork(els, { origin: { x: 0, y: 0 } });
  const y = e => (p.get(e.id) || e).y;
  assert.ok(y(inet) < y(fw) && y(fw) < y(core) && y(core) < y(acc) && y(acc) < y(pc));
});

test('find a device by IP, hostname or name', () => {
  const nas = node('netNAS', 0, 0, 'NAS-01', { data: [{ name: 'IP', value: '10.10.6.16' }, { name: 'Hostname', value: 'nas-01.sja.local' }] });
  const sw = node('netAccessSwitch', 0, 0, 'SW-OFFICE', { data: [{ name: 'IP', value: '10.10.1.2' }] });
  const els = [nas, sw, node('process', 0, 0, '10.10.6.16 in text only')];
  assert.deepEqual(findDevice(els, '10.10.6.16').map(e => e.id), [nas.id]);
  assert.deepEqual(findDevice(els, '10.10.').map(e => e.text).sort(), ['NAS-01', 'SW-OFFICE']);
  assert.deepEqual(findDevice(els, 'nas-01').map(e => e.id), [nas.id]);
  assert.deepEqual(findDevice(els, 'nothing'), []);
});

test('racks: slots are numbered from the bottom, devices snap to them, free U are counted', () => {
  const rack = create('rack', { x: 0, y: 0, w: 240, h: 420, data: [{ name: 'U', value: '20' }] });
  const s = rackSlots(rack);
  assert.equal(s.units, 20);
  assert.ok(s.slot(1).y > s.slot(20).y, 'U1 is at the bottom');
  const nas = node('netNAS', 30, s.slot(10).y + 2);
  assert.equal(rackUnits(nas), 2);
  const drop = rackDrop(nas, [rack, nas]);
  assert.equal(drop.rack.id, rack.id); assert.equal(drop.size, 2);
  assert.ok(Math.abs(drop.box.h - s.uh * 2) < 0.01);
  const placed = { ...nas, ...drop.box };
  const c = rackContents(rack, [rack, placed]);
  assert.equal(c.rows.length, 1); assert.equal(c.rows[0].size, 2); assert.equal(c.free, 18);
  assert.equal(rackDrop(node('netPC', 900, 900), [rack]), null, 'outside the rack: no snap');
  assert.equal(rackUnits({ type: 'netPC', data: [{ name: 'U', value: '4' }] }), 4);
});

test('a list stacks what is inside it, in order, and grows', () => {
  const list = create('listBox', { x: 0, y: 0, w: 240, h: 200 });
  const a = node('process', 20, 90, 'Second'), b = node('process', 20, 40, 'First');
  const { pos, height } = stackList(list, [list, a, b]);
  assert.ok(pos.get(b.id).y < pos.get(a.id).y);
  assert.equal(pos.get(b.id).x, 12); assert.equal(pos.get(b.id).w, 216);
  assert.ok(height > 200, 'the list grows to fit');
});

test('report: steps in order, every data field a column, lanes named; CSV and xlsx open', () => {
  const s = node('terminator', 0, 0, 'Start'), a = node('process', 0, 100, 'Raise PR', { data: [{ name: 'PIC', value: 'Admin' }, { name: 'Duration', value: '15 min' }] });
  const b = node('process', 0, 200, 'Make PO', { data: [{ name: 'PIC', value: 'Purchasing' }, { name: 'Status', value: 'Late' }] });
  const lane = create('swimlane', { x: -200, y: -50, w: 900, h: 400, lanes: [{ id: 'w', name: 'Warehouse', size: 1 }, { id: 'p', name: 'Purchasing', size: 1 }] });
  const r = reportRows([lane, s, a, b, conn(s, a), conn(a, b)]);
  assert.deepEqual(r.head, ['Step', 'Shape', 'Text', 'PIC', 'Duration', 'Status', 'Lane']);
  const pr = r.rows.find(x => x[2] === 'Raise PR');
  assert.deepEqual(pr, [1, 'Process', 'Raise PR', 'Admin', '15 min', '', 'Warehouse']);
  assert.equal(r.rows.find(x => x[2] === 'Make PO')[6], 'Purchasing');
  const csv = toCsv(r);
  assert.ok(csv.startsWith('﻿Step;Shape;Text'));
  const x = unzip(toXlsx(r));
  assert.ok(x.has('xl/workbook.xml') && x.has('[Content_Types].xml'));
  assert.match(x.get('xl/worksheets/sheet1.xml'), /Raise PR/);
  assert.match(x.get('xl/worksheets/sheet1.xml'), /<v>1<\/v>/, 'numbers stay numbers');
});

test('PowerPoint: one slide per frame, shapes and text stay editable, lines are connectors', () => {
  const a = node('process', 100, 100, 'Raise PR'), d = node('decision', 100, 260, 'OK?'), st = create('sticky', { x: 400, y: 100, w: 160, h: 160, text: 'Idea <1>', color: 'pink' });
  const p = toPptx([{ title: 'Overview', box: { x: 0, y: 0, w: 800, h: 450 }, els: [a, d, st, conn(a, d)] }, { title: 'Empty', box: { x: 0, y: 0, w: 100, h: 100 }, els: [], picture: { bytes: new Uint8Array([137, 80, 78, 71]), box: { x: 0, y: 0, w: 100, h: 100 } } }], { title: 'Board' });
  const f = unzip(p);
  assert.ok(f.has('ppt/slides/slide1.xml') && f.has('ppt/slides/slide2.xml') && f.has('ppt/media/image2.png'));
  const s1 = f.get('ppt/slides/slide1.xml');
  assert.match(s1, /prst="flowChartProcess"/); assert.match(s1, /prst="flowChartDecision"/);
  assert.match(s1, /<a:t>Raise PR<\/a:t>/); assert.match(s1, /Idea &lt;1&gt;/);
  assert.match(s1, /<p:cxnSp>/); assert.match(s1, /FFADD2/);
  assert.match(f.get('ppt/presentation.xml'), /<p:sldId id="257"/);
});

test('zip writer: stored entries with the right CRC', () => {
  const z = zip([['a.txt', 'hello'], ['b/c.txt', 'world']]);
  const f = unzip(z);
  assert.equal(f.get('a.txt'), 'hello'); assert.equal(f.get('b/c.txt'), 'world');
  assert.equal(crc32(new TextEncoder().encode('hello')), 0x3610a686);
});

test('version diff and the "since you last opened" sentence', () => {
  const a = node('process', 0, 0, 'Raise PR'), b = node('process', 0, 100, 'Old step'), c = node('process', 0, 200, 'Make PO');
  const after = [a, { ...c, text: 'Make PO now' }, node('decision', 0, 300, 'OK?'), node('process', 0, 400, 'Send')];
  const d = diffScenes([a, b, c], after);
  assert.equal(d.added.length, 2); assert.equal(d.removed.length, 1); assert.equal(d.changed.length, 1);
  assert.equal(diffSummary(d), 'added 2 shapes, changed 1 shape and removed 1 shape');
  assert.equal(diffSummary(diffScenes([a], [a, create('connector', { from: { id: a.id }, to: { x: 1, y: 1 } })])), 'added 1 line');
  assert.equal(diffSummary(diffScenes([a], [a])), '');
});

test('org chart: one card per person (no NIK, no position), lines from manager, laid out as a tree', () => {
  const people = [{ key: 'a', name: 'RM Bagastyo', dept: 'ICT' }, { key: 'b', name: 'Rina S', dept: 'ICT', managerKey: 'a' }, { key: 'c', name: 'Adi P', dept: 'ICT', managerKey: 'a' }];
  const out = orgChart(people, create, { x: 0, y: 0 });
  const cards = out.filter(e => e.type === 'orgCard'), lines = out.filter(e => e.type === 'connector');
  assert.equal(cards.length, 3); assert.equal(lines.length, 2);
  assert.equal(cards[0].text, 'RM Bagastyo\nICT');
  assert.ok(cards[0].y < cards[1].y && cards[1].y === cards[2].y);
  assert.ok(!JSON.stringify(out).match(/nik|position/i));
});

test('diagram → tasks: flow order, PIC and duration from shape data, due dates on working days', () => {
  const s = node('terminator', 0, 0, 'Start'), a = node('process', 0, 100, 'Raise PR', { data: [{ name: 'PIC', value: 'Admin' }, { name: 'Duration', value: '1 day' }] });
  const b = node('process', 0, 200, 'Make PO\nUse the Finance template', { data: [{ name: 'PIC', value: 'Purchasing' }, { name: 'Duration', value: '2 days' }] });
  const els = [s, a, b, conn(s, a), conn(a, b)];
  const t = tasksFromShapes(els, [b, s, a], { start: new Date(2026, 10, 20) });   // Friday 20 Nov 2026
  assert.deepEqual(t.map(x => x.title), ['Raise PR', 'Make PO'], 'Start/End are not tasks; flow order');
  assert.equal(t[0].pic, 'Admin'); assert.equal(t[0].due, '2026-11-23', 'the next working day after Friday is Monday');
  assert.equal(t[1].due, '2026-11-25'); assert.equal(t[1].description, 'Use the Finance template');
  assert.equal(tasksFromShapes(els, [a], { chain: false })[0].due, null);
});
