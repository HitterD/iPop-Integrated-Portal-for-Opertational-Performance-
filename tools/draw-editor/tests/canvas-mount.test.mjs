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

test('a shape someone else is typing in is locked; Present works without frames', async () => {
  const { win, el } = setup();
  const doc = new Y.Doc(), ymap = doc.getMap('wb:board');
  const aw = new Awareness(doc);
  aw.setLocalStateField('user', { name: 'Me', nik: 'me', color: '#34A853' });
  const toasts = [];
  try {
    const inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', awareness: aw, voteMap: doc.getMap('wbvote:board'), onFullscreen: () => true, toast: m => toasts.push(m) });
    const shape = { id: 'po', type: 'process', version: 1, versionNonce: 1, isDeleted: false, z: 'a1', x: 0, y: 0, w: 180, h: 72, text: 'Make PO' };
    doc.transact(() => ymap.set('po', shape));
    await tick(40);
    // Rina is typing in it
    aw.states.set(999, { user: { name: 'Rina Second', nik: 'rina', color: '#5856D6' }, wb: { board: 'board', typing: { id: 'po', at: Date.now() } } });
    aw.emit('change', [{ added: [999], updated: [], removed: [] }, 'remote']);
    await tick(40);
    const pill = [...el.querySelectorAll('.wb-typing text')].map(t => t.textContent);
    assert.deepEqual(pill, ['Rina is typing…'], 'others see who is typing');
    inst.api?.editTextById?.('po');
    // double-click on it: the editor must not open
    const wb = el.querySelector('.wb');
    wb.dispatchEvent(new window.MouseEvent('dblclick', { clientX: 120, clientY: 70, bubbles: true }));
    await tick(30);
    assert.equal(el.querySelector('textarea.wb-edit'), null, 'no editor while Rina types');
    assert.ok(toasts.some(t => /Rina is typing here/.test(t)), 'a short note says why');
    // a lock with no keystroke for 30 s is let go
    aw.states.set(999, { user: { name: 'Rina Second', nik: 'rina', color: '#5856D6' }, wb: { board: 'board', typing: { id: 'po', at: Date.now() - 31000 } } });
    aw.emit('change', [{ added: [], updated: [999], removed: [] }, 'remote']);
    await tick(30);
    assert.equal(el.querySelectorAll('.wb-typing').length, 0, 'an old lock is gone');
    // Present without frames shows the whole board and the tip
    el.querySelector('.wb-tr [data-c="present"]').click();
    await tick(40);
    assert.ok(el.querySelector('.wb').classList.contains('wb-presenting'), 'presenting');
    assert.equal(el.querySelector('.wb-preshint').hidden, false, 'the "add frames" tip shows');
    assert.match(el.querySelector('.wb-presbar, .wb-pres')?.textContent || '', /Whole board/);
    inst.destroy(); aw.destroy(); doc.destroy();
  } finally { win.happyDOM.abort(); }
});
