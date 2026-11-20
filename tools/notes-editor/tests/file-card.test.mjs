import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { NoteFile, fileKind, fileSubLine, pageFiles } from '../src/nodes.js';

const PDF_ID = '11111111-1111-1111-1111-111111111111';
const DOC_ID = '22222222-2222-2222-2222-222222222222';
const PPT_ID = '33333333-3333-3333-3333-333333333333';
const file = (id, name, size, extra = {}) => ({ type: 'noteFile', attrs: { id, name, size, ...extra } });
const p = text => ({ type: 'paragraph', content: [{ type: 'text', text }] });

function mount(content, hostExtra = {}) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser', 'Image']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const calls = { preview: [], info: [], toast: [] };
  const infos = {
    [PDF_ID]: { kind: 'pdf', pages: 18, unit: 'pages', thumbnailId: null, pdfId: null, previewable: true },
    [DOC_ID]: { kind: 'word', pages: 4, unit: 'pages', thumbnailId: null, pdfId: '44444444-4444-4444-4444-444444444444', previewable: true },
    [PPT_ID]: { kind: 'none', pages: null, unit: null, thumbnailId: null, pdfId: null, previewable: false },
  };
  const host = {
    filePreview: id => { calls.info.push(id); return Promise.resolve(infos[id] ?? null); },
    previewFile: (id, files) => calls.preview.push({ id, files }),
    toast: m => calls.toast.push(m),
    fetch: () => Promise.reject(new Error('offline')),
    ...hostExtra,
  };
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content: { type: 'doc', content }, extensions: [StarterKit, NoteFile.configure({ host: () => host })] });
  return { editor, win, calls, element };
}
const done = ({ editor, win }) => { editor.destroy(); win.happyDOM.abort(); };
const tick = () => new Promise(r => setTimeout(r, 0));

test('the file type comes from the name; legacy .ppt and unknown files are not previewable kinds', () => {
  assert.equal(fileKind('Deck.PPTX'), 'slides');
  assert.equal(fileKind('a.docx'), 'word');
  assert.equal(fileKind('a.rtf'), 'word');
  assert.equal(fileKind('Budget.xlsx'), 'sheet');
  assert.equal(fileKind('data.csv'), 'sheet');
  assert.equal(fileKind('x.pdf'), 'pdf');
  assert.equal(fileKind('old.ppt'), 'none');
  assert.equal(fileKind('setup.zip'), 'none');
});

test('the sub line reads "PDF · 3.9 MB · 18 pages", Excel counts sheets, an unknown count is left out', () => {
  assert.equal(fileSubLine('x.pdf', 3.9 * 1048576, { kind: 'pdf', pages: 18, unit: 'pages' }), 'PDF · 3.9 MB · 18 pages');
  assert.equal(fileSubLine('b.xlsx', 88 * 1024, { kind: 'sheet', pages: 3, unit: 'sheets' }), 'Excel · 88 KB · 3 sheets');
  assert.equal(fileSubLine('n.docx', 214 * 1024, { kind: 'word', pages: 1, unit: 'pages' }), 'Word · 214 KB · 1 page');
  assert.equal(fileSubLine('n.docx', 2048, null), 'Word · 2 KB');
  assert.equal(fileSubLine('s.zip', 2048, { kind: 'none', pages: null }), 'ZIP · 2 KB');
});

test('the card shows the name, size and page count with Preview, Download and ⋯', async () => {
  const m = mount([file(PDF_ID, 'iPop_IDEA_LAB_2026_v18.pdf', 3.9 * 1048576)]);
  try {
    await tick(); await tick();
    const card = m.element.querySelector('.nt-fcard');
    assert.ok(card);
    assert.equal(card.querySelector('.nm').textContent, 'iPop_IDEA_LAB_2026_v18.pdf');
    assert.equal(card.querySelector('.sub').textContent, 'PDF · 3.9 MB · 18 pages');
    assert.deepEqual([...card.querySelectorAll('[data-act]')].map(b => b.dataset.act), ['open', 'dl', 'menu']);
    assert.deepEqual(m.calls.info, [PDF_ID]);
  } finally { done(m); }
});

test('clicking the card or Preview opens the viewer with every file of the page, in order', async () => {
  const m = mount([file(PDF_ID, 'a.pdf', 10), p('between'), file(DOC_ID, 'b.docx', 20)]);
  try {
    await tick(); await tick();
    const [first, second] = m.element.querySelectorAll('.nt-fcard');
    second.querySelector('[data-act="open"]').click();
    first.click();
    const files = [{ id: PDF_ID, name: 'a.pdf', size: 10 }, { id: DOC_ID, name: 'b.docx', size: 20 }];
    assert.deepEqual(m.calls.preview, [{ id: DOC_ID, files }, { id: PDF_ID, files }]);
    assert.deepEqual(pageFiles(m.editor.state.doc), files);
  } finally { done(m); }
});

test('a file with no preview keeps Download only and says so; clicking it does not open the viewer', async () => {
  const m = mount([file(PPT_ID, 'old.ppt', 10)]);
  try {
    await tick(); await tick();
    const card = m.element.querySelector('.nt-fcard');
    assert.equal(card.querySelector('[data-act="open"]'), null);
    assert.ok(card.querySelector('[data-act="dl"].keep'));
    assert.match(card.querySelector('.na').textContent, /Preview not available for this file type/);
    card.click();
    assert.equal(m.calls.preview.length, 0);
  } finally { done(m); }
});

test('⋯ → Show inline sets the inline attribute; it round-trips through JSON and HTML and renders a preview frame', async () => {
  const m = mount([file(PDF_ID, 'a.pdf', 10)]);
  try {
    await tick(); await tick();
    m.element.querySelector('[data-act="menu"]').click();
    const items = [...m.element.querySelectorAll('.nt-file-menu [data-mi]')].map(b => b.textContent);
    assert.deepEqual(items, ['Show inline', 'Open full screen', 'Download', 'Remove']);
    m.element.querySelector('.nt-file-menu [data-mi="inline"]').click();
    assert.equal(m.editor.getJSON().content[0].attrs.inline, true);
    const html = m.editor.getHTML();
    assert.match(html, /data-inline="true"/);

    const embed = m.element.querySelector('.nt-fembed');
    assert.ok(embed, 'inline frame shown');
    assert.equal(embed.querySelector('.hd .nm').textContent, 'a.pdf');
    assert.equal(embed.querySelector('.hd small').textContent, '18 pages');
    assert.ok(embed.querySelector('.bd'));

    // parsed back from HTML (paste/import) it is still inline
    m.editor.commands.setContent(html);
    assert.equal(m.editor.getJSON().content[0].attrs.inline, true);

    // ▭ back to the card
    await tick(); await tick();
    m.element.querySelector('.nt-fembed [data-act="card"]').click();
    assert.equal(m.editor.getJSON().content[0].attrs.inline, false);
    assert.doesNotMatch(m.editor.getHTML(), /data-inline/);
    assert.ok(m.element.querySelector('.nt-fcard'));
  } finally { done(m); }
});

test('a read-only reader sees the inline frame but cannot switch it back or remove the file', async () => {
  const m = mount([file(DOC_ID, 'b.docx', 20, { inline: true })]);
  try {
    m.editor.setEditable(false);
    await tick(); await tick();
    const embed = m.element.querySelector('.nt-fembed');
    assert.ok(embed);
    assert.equal(embed.querySelector('[data-act="card"]'), null);
    embed.querySelector('[data-act="menu"]').click();
    assert.deepEqual([...m.element.querySelectorAll('.nt-file-menu [data-mi]')].map(b => b.dataset.mi), ['full', 'dl']);
  } finally { done(m); }
});

test('older file blocks without the attribute load as cards', () => {
  const m = mount([]);
  try {
    m.editor.commands.setContent('<div data-file="" data-id="x" data-name="a.pdf" data-size="5"></div>');
    assert.deepEqual({ ...m.editor.getJSON().content[0].attrs }, { id: 'x', name: 'a.pdf', size: 5, inline: false });
  } finally { done(m); }
});
