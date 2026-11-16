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
      try { inst = mountCanvas(el, { ymap, doc, Y, mode: 'board', theme: 'light', awareness: aw, voteMap: doc.getMap('wbvote:board'), onFullscreen: () => true, toast: () => {} }); }
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
