// node --test tests/canvas-round2.test.mjs — whiteboard round 2: partial eraser, arranging, groups, neat lines, find, rich text.
import test from 'node:test';
import assert from 'node:assert/strict';
import { cutStroke, strokePieces, alignBoxes, distributeBoxes, tidyRow, expandGroups, recogniseLine, findOnBoard, searchText, create } from '../src/canvas/model.js';
import { parse, plain, hasFormatting, wrapRuns, toggleMark, toggleBullets, makeLink } from '../src/canvas/richtext.js';

const stroke = (pts, x = 0, y = 0, width = 2) => ({ ...create('path', { x, y, points: pts, width, stroke: '#FF3B30', tool: 'pen' }) });
const line = n => Array.from({ length: n }, (_, i) => [i * 10, 0, 0.5]);

test('partial eraser: rubbing the middle of a line leaves two pieces that keep the look', () => {
  const s = stroke(line(21), 100, 50);
  const runs = cutStroke(s, 200, 50, 12);
  assert.equal(runs.length, 2);
  const pieces = strokePieces(s, runs);
  assert.equal(pieces.length, 2);
  for (const p of pieces) { assert.equal(p.stroke, '#FF3B30'); assert.equal(p.tool, 'pen'); assert.notEqual(p.id, s.id); assert.deepEqual(p.points[0].slice(0, 2), [0, 0]); }
  // the pieces sit where the line was
  assert.equal(pieces[0].x, 100);
  assert.ok(pieces[1].x > 200);
});

test('partial eraser: nothing touched returns the stroke unchanged; all touched returns nothing', () => {
  const s = stroke(line(5));
  const untouched = cutStroke(s, 500, 500, 10);
  assert.equal(untouched.length, 1); assert.equal(untouched[0], s.points);
  assert.deepEqual(cutStroke(s, 20, 0, 100), []);
});

test('align and space evenly', () => {
  const items = [{ id: 'a', b: { x: 0, y: 10, w: 100, h: 50 } }, { id: 'b', b: { x: 150, y: 40, w: 100, h: 80 } }, { id: 'c', b: { x: 500, y: 0, w: 100, h: 50 } }];
  assert.deepEqual(alignBoxes(items, 'top').map(m => [m.id, m.dy]), [['a', -10], ['b', -40]]);
  assert.deepEqual(alignBoxes(items, 'left').map(m => [m.id, m.dx]), [['b', -150], ['c', -500]]);
  const d = distributeBoxes(items, 'x');
  // outer two stay; the middle one moves so both gaps are equal ((600 - 300) / 2 = 150)
  assert.deepEqual(d, [{ id: 'b', dx: 100, dy: 0 }]);
  assert.deepEqual(distributeBoxes(items.slice(0, 2), 'x'), [], 'needs three');
  const row = tidyRow(items);
  assert.ok(row.every(m => m.dy === items.find(i => i.id === m.id).b.y * -1 || m.dy === -items.find(i => i.id === m.id).b.y + 0));
});

test('groups: picking one member picks the whole group, deleted members do not come back', () => {
  const els = [{ id: 'a', groupId: 'g' }, { id: 'b', groupId: 'g' }, { id: 'c', groupId: 'g', isDeleted: true }, { id: 'd' }];
  assert.deepEqual([...expandGroups(new Set(['a']), els)].sort(), ['a', 'b']);
  assert.deepEqual([...expandGroups(new Set(['d']), els)], ['d']);
});

test('hold to neat: a straight-ish stroke becomes a line, a curve does not', () => {
  assert.ok(recogniseLine(Array.from({ length: 30 }, (_, i) => [i * 8, i * 2 + (i % 2)])));
  assert.equal(recogniseLine(Array.from({ length: 30 }, (_, i) => [i * 8, Math.sin(i / 3) * 30])), null);
  assert.equal(recogniseLine([[0, 0], [5, 0], [10, 0], [12, 0]]), null, 'too short');
});

test('find on board: text, names and titles, without formatting marks, top to bottom', () => {
  const els = [
    { id: '1', type: 'sticky', x: 0, y: 300, w: 100, h: 100, text: 'Order **switch**' },
    { id: '2', type: 'frame', x: 0, y: 0, w: 100, h: 100, name: 'Switch room' },
    { id: '3', type: 'link', x: 0, y: 600, w: 100, h: 100, url: 'https://x.test', title: 'No match' },
    { id: '4', type: 'sticky', x: 0, y: 900, w: 100, h: 100, text: 'gone switch', isDeleted: true },
  ];
  assert.deepEqual(findOnBoard(els, 'switch').map(e => e.id), ['2', '1']);
  assert.equal(searchText(els[0]), 'Order switch');
  assert.deepEqual(findOnBoard(els, '  '), []);
});

test('rich text: markers, bullets and links', () => {
  const p = parse('Plan **now** and *later*\n- [call](https://example.com)\n~~old~~');
  assert.deepEqual(p[0].runs.map(r => [r.text, !!r.b, !!r.i]), [['Plan ', false, false], ['now', true, false], [' and ', false, false], ['later', false, true]]);
  assert.equal(p[1].bullet, true);
  assert.deepEqual(p[1].runs, [{ text: 'call', b: false, i: false, s: false, href: 'https://example.com' }]);
  assert.equal(p[2].runs[0].s, true);
  assert.equal(plain('a **b**\n- c'), 'a b\n• c');
  assert.equal(hasFormatting('plain text'), false);
  assert.equal(hasFormatting('2 * 3 = 6'), false, 'a lone star is not italic');
  assert.equal(hasFormatting('**x**'), true);
  // an unclosed marker stays as typed
  assert.equal(plain('5 ** 2'), '5 ** 2');
  // javascript: links are not links
  assert.equal(parse('[x](javascript:alert(1))')[0].runs[0].href, undefined);
});

test('rich text editing helpers', () => {
  assert.deepEqual(toggleMark('hello world', 6, 11, '**'), { value: 'hello **world**', start: 8, end: 13 });
  assert.equal(toggleMark('hello **world**', 8, 13, '**').value, 'hello world');
  assert.equal(toggleBullets('a\nb', 0, 3).value, '- a\n- b');
  assert.equal(toggleBullets('- a\n- b', 0, 7).value, 'a\nb');
  const one = toggleBullets('x\n', 2, 2);
  assert.equal(one.value, 'x\n- '); assert.equal(one.start, 4);
  assert.equal(makeLink('see it', 4, 6, 'https://a.b').value, 'see [it](https://a.b)');
});

test('wrapping keeps run styles and never starts a line with a space', () => {
  const w = wrapRuns(parse('aaa **bbb** ccc'), 7, t => t.length);
  assert.deepEqual(w.map(l => l.runs.map(r => r.text).join('')), ['aaa bbb', 'ccc']);
  assert.ok(w[0].runs.some(r => r.b && r.text === 'bbb'));
});
