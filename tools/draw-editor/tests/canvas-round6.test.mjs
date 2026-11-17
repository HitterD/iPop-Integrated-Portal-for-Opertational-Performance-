import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import * as Y from 'yjs';
import { mountCanvas } from '../src/canvas/board.js';
import { CANVAS_TEMPLATES, TEMPLATE_META, buildTemplate } from '../src/canvas/templates.js';
import { strokeOutline, nibOutline } from '../src/canvas/render.js';
import { isElement } from '../src/canvas/model.js';

// Round 6: dock + contextual bar, nine pens, Template Center with previews, 57 templates.

test('every template has its what / when / steps, builds in a frame, and every line joins shapes that exist', () => {
  const keys = TEMPLATE_META.map(t => t.key);
  assert.equal(keys.length, 57);
  assert.equal(new Set(keys).size, keys.length, 'no key twice');
  assert.deepEqual(Object.keys(CANVAS_TEMPLATES).sort(), [...keys].sort(), 'meta and builders cover the same templates');
  for (const t of TEMPLATE_META) {
    assert.ok(t.name && t.desc && t.when && t.steps?.length >= 2, `${t.key}: text`);
    assert.ok(['Beginner', 'Intermediate', 'Advanced'].includes(t.level), `${t.key}: level`);
    const els = buildTemplate(t.key, { x: 100, y: 50 });
    assert.ok(els.length > 2, t.key);
    assert.ok(els.every(isElement), `${t.key}: only elements the board can draw`);
    assert.equal(els[0].type, 'frame', `${t.key}: the frame comes first so it is drawn underneath`);
    assert.equal(els[0].x, 100, `${t.key}: placed where asked`);
    assert.equal(els.filter(e => e.howTo).length, 1, `${t.key}: one How-to note`);
    const ids = new Set(els.map(e => e.id));
    for (const c of els.filter(e => e.type === 'connector')) {
      if (c.from?.id) assert.ok(ids.has(c.from.id), `${t.key}: line start`);
      if (c.to?.id) assert.ok(ids.has(c.to.id), `${t.key}: line end`);
    }
  }
  assert.equal(TEMPLATE_META.filter(t => t.featured).length, 6, 'six featured for the empty board');
});

test('a template can leave out its How-to note and its example text', () => {
  const plain = buildTemplate('eisenhower', { howTo: false, examples: false });
  assert.equal(plain.filter(e => e.howTo).length, 0);
  assert.ok(plain.filter(e => e.example).every(e => !e.text), 'example stickies are empty');
  const full = buildTemplate('eisenhower');
  assert.ok(full.some(e => e.example && /Karawang/.test(e.text)), 'with examples: work words');
  // an older template is wrapped the same way
  const old = buildTemplate('kanban', { howTo: true });
  assert.equal(old[0].type, 'frame'); assert.equal(old[0].name, 'Kanban');
  assert.ok(old.some(e => e.howTo));
});

test('Indonesian work words find the right templates', () => {
  const find = q => TEMPLATE_META.filter(t => q.split(/\s+/).every(w => `${t.name} ${t.desc} ${t.cat} ${t.aliases || ''}`.toLowerCase().includes(w))).map(t => t.key);
  assert.ok(find('notulen rapat').includes('meetingnotes'));
  assert.ok(find('akar masalah').includes('fishbone'));
  assert.ok(find('persetujuan').includes('approval'));
  assert.ok(find('5r').includes('5s'));
});

test('the new pens draw: a brush tapers, a fineliner keeps its width, calligraphy is a flat nib', () => {
  const pts = Array.from({ length: 20 }, (_, i) => [i * 10, Math.sin(i / 3) * 10, 0.5]);
  for (const kind of ['fine', 'brush', 'crayon', 'neon']) assert.ok(strokeOutline(pts, 6, { kind }).startsWith('M'), kind);
  const d = nibOutline(pts, 8);
  assert.equal((d.match(/M/g) || []).length, pts.length - 1, 'one quad per segment');
});

function setup(width = 1300) {
  const win = new Window({ width, height: 860 });
  for (const k of ['window', 'document', 'Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'XMLSerializer', 'Image', 'DOMParser', 'navigator', 'MouseEvent', 'KeyboardEvent', 'CustomEvent', 'WheelEvent', 'Event'])
    try { if (win[k] !== undefined) globalThis[k] = k === 'window' ? win : win[k]; } catch { /* read-only */ }
  globalThis.requestAnimationFrame = cb => setTimeout(() => cb(Date.now()), 0);
  globalThis.cancelAnimationFrame = id => clearTimeout(id);
  globalThis.ResizeObserver = class { constructor(cb) { this.cb = cb; } observe() { setTimeout(() => this.cb([]), 0); } disconnect() {} };
  globalThis.confirm = () => true;
  const el = win.document.createElement('div');
  win.document.body.appendChild(el);
  // happy-dom has no layout: give the board a size so the dock (≥ 940 px) is chosen
  Object.defineProperty(win.HTMLElement.prototype, 'clientWidth', { configurable: true, get() { return width; } });
  Object.defineProperty(win.HTMLElement.prototype, 'clientHeight', { configurable: true, get() { return 800; } });
  return { win, el };
}
const tick = (ms = 20) => new Promise(r => setTimeout(r, ms));

test('the Whiteboard page gets the dock; the bar follows the tool; pens switch and remember their colour', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  try {
    const saved = {};
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', toast: () => {}, onFullscreen: () => true,
      prefs: { get: async k => saved[k] ?? null, set: async (k, v) => { saved[k] = v; } }, pages: { all: () => [], info: () => null } });
    await tick(40);
    assert.equal(el.querySelector('.wb-dock')?.hidden, false, 'dock shows');
    assert.equal(el.querySelector('.wb-tools').hidden, true, 'small bar hides');
    el.querySelector('[data-tile="pen"]').click(); await tick();
    const bar = el.querySelector('.wb-ctx');
    assert.equal(bar.hidden, false, 'pen bar');
    assert.equal(bar.querySelectorAll('[data-pen]').length, 10, 'nine pens + eraser');
    bar.querySelector('[data-pen="brush"]').click(); await tick();
    assert.equal(el.querySelector('.wb-ctx [data-pen="brush"]').classList.contains('on'), true);
    el.querySelector('.wb-ctx [data-pcol="#0A84FF"]').click(); await tick();
    assert.match(el.querySelector('[data-tile="pen"]').innerHTML, /#0A84FF/i, 'the tile shows the pen in its colour');
    // My pens: save the current pen, it reaches the server prefs
    el.querySelector('.wb-ctx [data-cp="mypens"]').click(); await tick();
    el.querySelector('.wb-ctxpop [data-mypenname]').value = 'Blue brush';
    el.querySelector('.wb-ctxpop [data-mypensave]').click(); await tick();
    assert.deepEqual(saved['wb-pens'], [{ name: 'Blue brush', type: 'brush', color: '#0A84FF', size: 1 }]);
    // shapes: the bar shows the shape row; connector: routes
    el.querySelector('[data-tile="shape"]').click(); await tick();
    assert.ok(el.querySelector('.wb-ctx [data-shape="diamond"]'), 'shape row');
    el.querySelector('[data-tile="connector"]').click(); await tick();
    assert.ok(el.querySelector('.wb-ctx [data-route="elbow"]'), 'connector routes');
    inst.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});

test('selecting a flowchart shape shows Next step / Check / Tidy in the bar; the floating bar stays hidden', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  try {
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', toast: () => {} });
    doc.transact(() => ymap.set('d', { id: 'd', type: 'decision', version: 1, versionNonce: 1, isDeleted: false, z: 'a1', x: 0, y: 0, w: 180, h: 110, text: 'Budget OK?' }));
    await tick(30);
    // select through the keyboard: Ctrl A
    el.querySelector('.wb').dispatchEvent(new window.KeyboardEvent('keydown', { key: 'a', ctrlKey: true, bubbles: true }));
    await tick(30);
    const bar = el.querySelector('.wb-ctx');
    assert.equal(bar.hidden, false);
    assert.ok(bar.querySelector('[data-act2="next"]'), 'Next step');
    assert.ok(bar.querySelector('[data-run="check"]') && bar.querySelector('[data-run="tidy"]'), 'Check and Tidy');
    assert.equal(el.querySelector('.wb-props').hidden, true, 'no floating selection bar on the dock');
    inst.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});

test('the Template Center: previews, search, big preview, Use adds the template in its frame and remembers it', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  try {
    const saved = {};
    const mine = [{ id: 't1', name: 'Weekly ICT review', description: null, json: JSON.stringify([{ id: 'f', type: 'frame', version: 1, versionNonce: 1, isDeleted: false, z: 'a0', x: 0, y: 0, w: 400, h: 300, name: 'Weekly' }]), count: 1, shared: false, mine: true, ownerName: 'Me' }];
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', toast: () => {},
      prefs: { get: async k => saved[k] ?? null, set: async (k, v) => { saved[k] = v; } },
      templates: { list: async () => mine, save: async () => ({}), remove: async () => {}, isAdmin: () => false } });
    await tick(40);
    // empty board: six featured with previews
    const empty = el.querySelector('.wb-emptygal');
    assert.equal(empty.hidden, false);
    assert.equal(empty.querySelectorAll('.wb-tcard').length, 6);
    assert.ok(empty.querySelector('.wb-tpv svg'), 'a real preview, drawn from the template');
    empty.querySelector('[data-tbrowse]').click(); await tick(40);
    const tc = el.querySelector('.wb-tc');
    assert.equal(el.querySelector('.wb-tcov').hidden, false, 'Template Center open');
    assert.ok(tc.querySelectorAll('.wb-tcard').length >= 6 + 6 * 3, 'featured + three per category');
    // search in Indonesian
    const q = tc.querySelector('[data-tq]'); q.value = 'notulen'; q.dispatchEvent(new window.Event('input', { bubbles: true })); await tick();
    assert.deepEqual([...tc.querySelectorAll('.wb-tcard')].map(c => c.dataset.tk), ['meetingnotes']);
    // My templates come from the server
    q.value = ''; q.dispatchEvent(new window.Event('input', { bubbles: true })); await tick();
    tc.querySelector('[data-tview="mine"]').click(); await tick();
    assert.deepEqual([...tc.querySelectorAll('.wb-tcard')].map(c => c.dataset.tk), ['u:t1']);
    tc.querySelector('[data-tview="featured"]').click(); await tick();
    // preview, next, use
    tc.querySelector('[data-tprev="eisenhower"]').click(); await tick();
    assert.equal(tc.querySelector('.wb-bp').hidden, false);
    assert.match(tc.querySelector('.wb-bpi h3').textContent, /Eisenhower/);
    tc.querySelector('[data-tstep="1"]').click(); await tick();
    assert.doesNotMatch(tc.querySelector('.wb-bpi h3').textContent, /Eisenhower/, 'next template');
    tc.querySelector('[data-tstep="-1"]').click(); await tick();
    tc.querySelector('.wb-use').click(); await tick(40);
    assert.equal(el.querySelector('.wb-tcov').hidden, true, 'closed after use');
    const els = []; ymap.forEach(v => els.push(v));
    assert.ok(els.some(e => e.type === 'frame' && e.name === 'Eisenhower Matrix'), 'the template is on the board in its frame');
    assert.ok(els.some(e => e.howTo), 'with its How-to note');
    assert.deepEqual(saved['wb-tpl-recent'], ['eisenhower'], 'remembered as recently used');
    inst.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});

test('a /draw block and a narrow board keep the small bar', async () => {
  for (const [mode, width] of [['block', 1300], ['board', 700]]) {
    const { win, el } = setup(width);
    const doc = new Y.Doc(), ymap = doc.getMap('wb:x');
    try {
      const inst = mountCanvas(el, { ymap, doc, Y, mode, theme: 'light', toast: () => {} });
      await tick(40);
      assert.equal(el.querySelector('.wb-dock').hidden, true, `${mode} ${width}: no dock`);
      assert.equal(el.querySelector('.wb-tools').hidden, false, `${mode} ${width}: small bar`);
      inst.destroy(); doc.destroy();
    } finally { win.happyDOM.abort(); }
  }
});

test('round 7: a wheel over a panel never moves the board; the Template Center shows every template', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  try {
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', toast: () => {} });
    await tick(40);
    const world = () => el.querySelector('.wb-world').getAttribute('transform');
    el.querySelector('[data-tile="tpl"]').click(); await tick(40);
    const before = world();
    const body = el.querySelector('.wb-tcbody');
    const ev = new window.WheelEvent('wheel', { deltaY: 200, bubbles: true, cancelable: true });
    body.dispatchEvent(ev);
    await tick(30);
    assert.equal(world(), before, 'the board did not pan');
    el.querySelector('[data-tview="all"]').click(); await tick();
    assert.doesNotMatch(el.querySelector('.wb-tc').textContent, /See \d+ more/);
    assert.equal(el.querySelectorAll('.wb-tc .wb-tcard').length, 57, 'all 57 iPop templates');
    assert.equal(el.querySelectorAll('.wb-tc [data-tjump]').length, 6, 'jump to each category');
    // a wheel on the empty board still pans
    const wb = el.querySelector('.wb');
    wb.dispatchEvent(new window.WheelEvent('wheel', { deltaY: 120, bubbles: true, cancelable: true }));
    await tick(30);
    inst.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});
