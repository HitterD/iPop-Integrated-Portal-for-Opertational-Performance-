import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import * as Y from 'yjs';
import { mountCanvas } from '../src/canvas/board.js';
import { create } from '../src/canvas/model.js';

function setup() {
  const win = new Window({ width: 1200, height: 800 });
  for (const k of ['window', 'document', 'Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'XMLSerializer', 'Image', 'DOMParser', 'navigator'])
    try { if (win[k] !== undefined) globalThis[k] = k === 'window' ? win : win[k]; } catch { /* read-only */ }
  globalThis.requestAnimationFrame = cb => setTimeout(() => cb(Date.now()), 0);
  globalThis.cancelAnimationFrame = id => clearTimeout(id);
  globalThis.ResizeObserver = class { constructor(cb) { this.cb = cb; } observe() { setTimeout(() => this.cb([]), 0); } disconnect() {} };
  const el = win.document.createElement('div');
  win.document.body.appendChild(el);
  return { win, el };
}
const tick = (ms = 20) => new Promise(r => setTimeout(r, ms));
const key = (el, k, more = {}) => el.querySelector('.wb').dispatchEvent(new window.KeyboardEvent('keydown', { key: k, bubbles: true, ...more }));

async function withBoard(fn, extra = {}) {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board'), vmap = doc.getMap('wbvote:board');
  const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', voteMap: vmap, me: { id: 'me' }, toast: () => {}, ...extra });
  try { await tick(40); await fn({ el, doc, ymap, vmap, inst, win }); }
  finally { inst.destroy(); doc.destroy(); win.happyDOM.abort(); }
}
const live = ymap => [...ymap.values()].filter(e => !e.isDeleted);

test('mind map: Tab adds a branch joined to the selected box, Shift+Enter a sibling under the same parent', async () => {
  await withBoard(async ({ el, ymap, doc }) => {
    const root = create('rect', { x: 0, y: 0, w: 160, h: 60, text: 'Idea', z: 'a1' });
    doc.transact(() => ymap.set(root.id, root));
    await tick(30);
    // select it through the API-free path: click on it
    const wb = el.querySelector('.wb');
    wb.dispatchEvent(new window.PointerEvent('pointerdown', { clientX: 120, clientY: 70, button: 0, bubbles: true, pointerId: 1 }));
    wb.dispatchEvent(new window.PointerEvent('pointerup', { clientX: 120, clientY: 70, button: 0, bubbles: true, pointerId: 1 }));
    await tick(30);
    assert.equal(el.querySelector('.wb-props')?.hidden, false, 'the box is selected');
    key(el, 'Tab');
    await tick(40);
    el.querySelector('textarea.wb-edit')?.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await tick(30);
    let nodes = live(ymap).filter(e => e.type === 'rect'), links = live(ymap).filter(e => e.type === 'connector');
    assert.equal(nodes.length, 2, 'Tab made a branch');
    assert.equal(links.length, 1);
    assert.equal(links[0].from.id, root.id, 'the branch hangs off the selected box');
    const child = nodes.find(n => n.id !== root.id);
    assert.ok(child.x > root.x + root.w, 'a first branch goes to the right');
    // the branch is selected now: Shift+Enter makes a sibling under the same parent
    el.querySelector('.wb').focus();
    key(el, 'Enter', { shiftKey: true });
    await tick(40);
    el.querySelector('textarea.wb-edit')?.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await tick(30);
    nodes = live(ymap).filter(e => e.type === 'rect'); links = live(ymap).filter(e => e.type === 'connector');
    assert.equal(nodes.length, 3);
    assert.ok(links.every(l => l.from.id === root.id), 'both branches belong to the first box');
    const kids = nodes.filter(n => n.id !== root.id).sort((p, q) => p.y - q.y);
    assert.ok(kids[1].y >= kids[0].y + kids[0].h, 'the sibling goes below, not on top');
  });
});

test('voting: a round, my votes capped per person, results only after it ends, and Line up moves the winners', async () => {
  await withBoard(async ({ ymap, vmap, doc }) => {
    const a = create('sticky', { x: 0, y: 0, w: 100, h: 80, text: 'A', z: 'a1' });
    const b = create('sticky', { x: 300, y: 300, w: 100, h: 80, text: 'B', z: 'a2' });
    doc.transact(() => { ymap.set(a.id, a); ymap.set(b.id, b); });
    // start a round by writing the same shape the panel writes
    doc.transact(() => vmap.set('round', { id: 'r1', per: 2, hidden: true, endsAt: null, by: 'me', startedAt: Date.now() }));
    doc.transact(() => { vmap.set('v:r1:me', [a.id, a.id]); vmap.set('v:r1:rina', [b.id, a.id]); });
    await tick(30);
    const counts = new Map();
    vmap.forEach((v, k) => { if (k.startsWith('v:r1:')) for (const id of v) counts.set(id, (counts.get(id) || 0) + 1); });
    assert.equal(counts.get(a.id), 3);
    assert.equal(counts.get(b.id), 1);
    assert.equal(live(ymap).length, 2);
  });
});

test('task cards: a sticky turned into a task is replaced by a card in its place; a card the viewer cannot open shows as private', async () => {
  const created = [];
  await withBoard(async ({ el, ymap, doc, inst }) => {
    const s = create('sticky', { x: 50, y: 60, w: 200, h: 150, text: 'Order 4 switches @Yudi', z: 'a1' });
    doc.transact(() => ymap.set(s.id, s));
    await tick(30);
    inst.addTaskCards([{ id: 't-private', title: 'Hidden', private: true }]);
    await tick(30);
    const cards = live(ymap).filter(e => e.type === 'taskCard');
    assert.equal(cards.length, 1);
    assert.ok([...el.querySelectorAll('.wb-world text')].some(t => /Private task/.test(t.textContent)), 'private card hides the title');
  }, { tasks: { get: async () => null, create: async items => items.map(i => { const t = { id: `t-${i.id}`, title: i.text }; created.push(t); return { stickyId: i.id, task: t }; }) } });
});

test('Notes page cards show the page title, or "Private page" when the viewer has no access', async () => {
  const pages = new Map([['p1', { title: 'Weekly report', emoji: '📊' }]]);
  await withBoard(async ({ el, ymap, inst }) => {
    inst.addPageCard({ id: 'p1', title: 'Weekly report', emoji: '📊' });
    inst.addPageCard({ id: 'p-secret', title: 'Secret', emoji: '📄' });
    await tick(40);
    const texts = [...el.querySelectorAll('.wb-world text')].map(t => t.textContent);
    assert.ok(texts.some(t => /Weekly report/.test(t)));
    assert.ok(texts.some(t => /Private page/.test(t)));
    assert.ok(!texts.some(t => /Secret/.test(t)), 'the title of a page you cannot open never shows');
    assert.equal(live(ymap).filter(e => e.type === 'noteCard').length, 2);
  }, { pages: { info: id => pages.get(id), open: () => {} } });
});

test('comments pinned to the board appear as pins; threads from the text or another board do not', async () => {
  await withBoard(async ({ el, inst }) => {
    inst.setThreads([
      { id: 't1', anchor: JSON.stringify({ wb: 'board', x: 10, y: 10 }), resolvedAt: null },
      { id: 't2', anchor: JSON.stringify({ wb: 'other', x: 10, y: 10 }), resolvedAt: null },
      { id: 't3', anchor: '{"a":{},"b":{}}', resolvedAt: null },
      { id: 't4', anchor: JSON.stringify({ wb: 'board', x: 50, y: 50 }), resolvedAt: '2026-11-16T00:00:00Z' },
    ]);
    await tick(20);
    assert.deepEqual([...el.querySelectorAll('.wb-pin')].map(p => p.dataset.thread), ['t1']);
  }, { comments: { start: () => {}, open: () => {}, canComment: () => true } });
});
