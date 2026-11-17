import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import * as Y from 'yjs';
import { Awareness } from 'y-protocols/awareness';
import { mountCanvas } from '../src/canvas/board.js';

// The editor must mount and paint without touching anything before it exists, whatever order the browser fires events in.
function setup() {
  const win = new Window({ width: 1200, height: 800 });
  for (const k of ['window', 'document', 'Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'XMLSerializer', 'Image', 'DOMParser', 'navigator'])
    try { if (win[k] !== undefined) globalThis[k] = k === 'window' ? win : win[k]; } catch { /* read-only in this Node */ }
  globalThis.requestAnimationFrame = cb => setTimeout(() => cb(Date.now()), 0);
  globalThis.cancelAnimationFrame = id => clearTimeout(id);
  globalThis.ResizeObserver = class { constructor(cb) { this.cb = cb; } observe() { setTimeout(() => this.cb([]), 0); } disconnect() {} };
  const el = win.document.createElement('div');
  win.document.body.appendChild(el);
  return { win, el };
}
const tick = (ms = 20) => new Promise(r => setTimeout(r, ms));

test('mounts, paints and destroys cleanly, with and without awareness, even when awareness fires during mount', async () => {
  for (const withAw of [false, true]) {
    const { win, el } = setup();
    const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
    const aw = withAw ? new Awareness(doc) : null;
    if (aw) {
      // another person already on the board, so the first change event arrives while the editor is still being built
      const other = new Awareness(new Y.Doc());
      aw.setLocalStateField('user', { name: 'Me', color: '#34A853' });
      aw.on('change', () => {});
      aw.states.set(999, { user: { name: 'Rina Second', color: '#5856D6' }, wb: { board: 'board', x: 10, y: 20, sel: [], laser: [] } });
      other.destroy();
    }
    const errors = [];
    const onErr = e => errors.push(e?.message || String(e));
    process.on('uncaughtException', onErr);
    try {
      let inst = null;
      // every host service a real Notes page passes, including comments that already exist (delivered during mount)
      const comments = { canComment: () => true, start() {}, open() {}, onFocus: () => () => {},
        subscribe: f => { f([{ id: 't1', anchor: JSON.stringify({ wb: 'board', x: 1, y: 1 }), text: 'Who signs?', author: 'Rina', count: 2 }]); return () => {}; } };
      try { inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', awareness: aw, voteMap: doc.getMap('wbvote:board'), onFullscreen: () => true, toast: () => {},
        comments, library: { list: async () => [] }, pages: { all: () => [], info: () => null }, title: () => 'QA', seenKey: 'p:board',
        tasks: { get: async () => null, create: async () => [], createMany: async () => [] }, people: { search: async () => [], team: async () => [] },
        history: { list: async () => [], scene: async () => [] }, shareToChat: () => {} }); }
      catch (err) { aw?.destroy(); doc.destroy(); throw err; }
      aw?.emit('change', [{ added: [999], updated: [], removed: [] }, 'remote']);
      await tick(60);
      assert.ok(el.querySelector('.wb-tools'), 'tool bar is there');
      if (withAw) {
        assert.equal(el.querySelectorAll('.wb-face').length, 1, 'the other person shows up');
        assert.ok([...el.querySelectorAll('.wb-cursor text')].some(t => t.textContent === 'Rina'), 'their cursor shows their first name');
      }
      inst.destroy();
      aw?.destroy(); doc.destroy();
      await tick(20);
    } finally { process.off('uncaughtException', onErr); win.happyDOM.abort(); }
    assert.deepEqual(errors, [], `no errors (awareness: ${withAw})`);
  }
});

test('the board menu keeps its rows while others move their cursors (no flicker, no scroll jump)', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  const aw = new Awareness(doc);
  aw.setLocalStateField('user', { name: 'Me', color: '#34A853' });
  try {
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', awareness: aw, voteMap: doc.getMap('wbvote:board'), onFullscreen: () => true, toast: () => {} });
    await tick(40);
    el.querySelector('.wb-tr [data-c="menu"]').click();
    await tick(20);
    const list = el.querySelector('.wb-boardmenu .wb-mlist');
    assert.ok(list && list.querySelectorAll('.wb-mitem').length > 5, 'menu is open with its tools');
    const row = list.querySelector('[data-c="find"]');
    list.scrollTop = 40;
    // another person moves their cursor many times: every event repaints the corners
    for (let i = 0; i < 25; i++) {
      aw.states.set(999, { user: { name: 'Rina Second', color: '#5856D6' }, wb: { board: 'board', x: i * 10, y: 20, sel: [], laser: [] } });
      aw.emit('change', [{ added: i ? [] : [999], updated: i ? [999] : [], removed: [] }, 'remote']);
    }
    await tick(40);
    assert.equal(list.querySelector('[data-c="find"]'), row, 'the same row element is still there (not rebuilt)');
    assert.equal(el.querySelector('.wb-boardmenu .wb-mlist'), list, 'the list itself was not replaced');
    inst.destroy(); aw.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});
