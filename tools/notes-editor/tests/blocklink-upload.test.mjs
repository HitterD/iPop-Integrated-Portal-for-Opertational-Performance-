import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { BlockId, findBlock, ensureBlockId, newBlockId, uploadXhr, createUploadTray } from '../src/extras.js';

function mount(content) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content, extensions: [StarterKit, BlockId] });
  return { editor, win };
}
const p = t => ({ type: 'paragraph', content: [{ type: 'text', text: t }] });
const done = ({ editor, win }) => { editor.destroy(); win.happyDOM.abort(); };

test('a block gets an id the first time it is linked, and keeps it afterwards', () => {
  const m = mount({ type: 'doc', content: [p('one'), p('two')] });
  try {
    const second = m.editor.state.doc.firstChild.nodeSize;
    const id = ensureBlockId(m.editor, second);
    assert.match(id, /^[a-z0-9]{6,8}$/);
    assert.equal(ensureBlockId(m.editor, second), id);                    // second call reuses it
    assert.equal(findBlock(m.editor.state.doc, id), second);
    assert.equal(findBlock(m.editor.state.doc, 'nope'), -1);
    assert.equal(findBlock(m.editor.state.doc, ''), -1);
    assert.match(m.editor.getHTML(), new RegExp(`data-bid="${id}"`));
  } finally { done(m); }
});

test('the id follows its block when something is typed above it', () => {
  const m = mount({ type: 'doc', content: [p('one'), p('two')] });
  try {
    const id = ensureBlockId(m.editor, m.editor.state.doc.firstChild.nodeSize);
    m.editor.commands.insertContentAt(0, { type: 'paragraph', content: [{ type: 'text', text: 'new first line' }] });
    const pos = findBlock(m.editor.state.doc, id);
    assert.ok(pos > 0);
    assert.equal(m.editor.state.doc.nodeAt(pos).textContent, 'two');
  } finally { done(m); }
});

test('a node that cannot carry an id (a text position) is refused, not crashed on', () => {
  const m = mount({ type: 'doc', content: [p('one')] });
  try {
    assert.equal(ensureBlockId(m.editor, 999), null);
    assert.notEqual(newBlockId(), newBlockId());
  } finally { done(m); }
});

test('upload tray: a row per file, progress, failure state and removal', () => {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  const tray = createUploadTray();
  try {
    assert.equal(tray.dom.hidden, true);
    let cancelled = 0;
    const a = tray.add('plan.pdf', () => cancelled++);
    const b = tray.add('photo.jpg');
    assert.equal(tray.dom.hidden, false);
    assert.equal(tray.dom.querySelectorAll('.up').length, 2);
    a.progress(0.5);
    assert.equal(tray.dom.querySelector('.pc').textContent, '50%');
    a.progress(7);                                                          // clamps; 100% reads Saving
    assert.equal(tray.dom.querySelector('.pc').textContent, 'Saving…');
    a.progress(-3);
    assert.equal(tray.dom.querySelector('.pc').textContent, '0%');
    tray.dom.querySelector('button').click();
    assert.equal(cancelled, 1);
    b.fail('Too big');
    assert.ok(tray.dom.querySelectorAll('.up')[1].classList.contains('bad'));
    a.done();
    assert.equal(tray.dom.querySelectorAll('.up').length, 1);
  } finally { tray.destroy(); win.happyDOM.abort(); }
});

test('uploadXhr reports progress, resolves the JSON body, and cancels cleanly', async () => {
  const sent = [];
  class FakeXhr {
    constructor() { this.upload = {}; FakeXhr.last = this; }
    open(m, u) { this.method = m; this.url = u; }
    setRequestHeader(k, v) { this.headers = { ...(this.headers || {}), [k]: v }; }
    send() { sent.push(this); }
    abort() { this.aborted = true; }
  }
  const prev = globalThis.XMLHttpRequest; globalThis.XMLHttpRequest = FakeXhr;
  try {
    const seen = [];
    const ok = uploadXhr('/api/x', {}, 'T', { onProgress: f => seen.push(f) });
    const x = FakeXhr.last;
    assert.equal(x.headers.Authorization, 'Bearer T');
    x.upload.onprogress({ lengthComputable: true, loaded: 25, total: 100 });
    x.upload.onprogress({ lengthComputable: false, loaded: 1, total: 0 });
    x.status = 200; x.responseText = '{"id":"abc"}'; x.onload();
    assert.deepEqual(await ok, { ok: true, status: 200, json: { id: 'abc' } });
    assert.deepEqual(seen, [0.25]);

    const bad = uploadXhr('/api/x', {}, 'T');
    FakeXhr.last.status = 413; FakeXhr.last.responseText = 'not json'; FakeXhr.last.onload();
    assert.deepEqual(await bad, { ok: false, status: 413, json: null });

    const ctl = new AbortController();
    const cancelled = uploadXhr('/api/x', {}, 'T', { signal: ctl.signal });
    const y = FakeXhr.last;
    ctl.abort();
    await assert.rejects(cancelled, e => e.name === 'AbortError');
    assert.equal(y.aborted, true);

    const pre = new AbortController(); pre.abort();
    await assert.rejects(uploadXhr('/api/x', {}, 'T', { signal: pre.signal }), e => e.name === 'AbortError');
  } finally { globalThis.XMLHttpRequest = prev; }
});
