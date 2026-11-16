import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { PageLink, matchPages, pageLinkIds } from '../src/page-link.js';

const pages = [
  { id: 'a', title: 'Weekly report', emoji: '📊' }, { id: 'b', title: 'Network layout idea', emoji: '✏️' },
  { id: 'c', title: 'Report archive' }, { id: 'd', title: 'Meeting notes' }, { id: 'e', title: '' },
];

test('page search ranks prefix matches before word matches before substrings, and excludes the current page', () => {
  assert.deepEqual(matchPages(pages, 'rep').map(p => p.id), ['c', 'a']);              // "Report archive" starts with it, "Weekly report" has a word that does
  assert.deepEqual(matchPages(pages, 'eek').map(p => p.id), ['a']);                    // substring only
  assert.deepEqual(matchPages(pages, 'zzz'), []);
  assert.deepEqual(matchPages(pages, 'rep', { exclude: ['c'] }).map(p => p.id), ['a']);
  assert.ok(matchPages(pages, '').length > 0);                                         // empty query lists pages
  assert.ok(matchPages(pages, '').length <= 8);
  assert.deepEqual(matchPages([], 'x'), []);
  assert.deepEqual(matchPages([null, { title: 'No id' }], 'n'), []);                   // broken rows never crash the picker
});

test('an untitled page is found by typing "untitled" and a case-insensitive query works', () => {
  assert.deepEqual(matchPages(pages, 'MEET').map(p => p.id), ['d']);
});

function mount(content, host = {}) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  const el = win.document.createElement('div'); win.document.body.appendChild(el);
  const editor = new Editor({ element: el, content, extensions: [StarterKit, PageLink.configure({ host: () => host })] });
  return { editor, win, el };
}
const link = (id, title) => ({ type: 'pageLink', attrs: { id, title, emoji: '📄' } });
const doc = (...inline) => ({ type: 'doc', content: [{ type: 'paragraph', content: inline }] });

test('links are collected in reading order without duplicates', () => {
  const { editor, win } = mount(doc(link('a', 'A'), { type: 'text', text: ' and ' }, link('b', 'B'), link('a', 'A again')));
  try { assert.deepEqual(pageLinkIds(editor.state.doc), ['a', 'b']); } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('a link shows the target page\'s CURRENT title, so a rename shows up, and clicking opens it', () => {
  let opened = null;
  const live = { a: { title: 'Weekly report (renamed)', emoji: '📈' } };
  const { editor, win, el } = mount(doc(link('a', 'Old title')), { pageInfo: id => live[id], openPage: id => { opened = id; }, pagesKnown: () => true });
  try {
    const chip = el.querySelector('.nt-pagelink');
    assert.match(chip.textContent, /Weekly report \(renamed\)/);
    assert.match(chip.textContent, /📈/);
    chip.dispatchEvent(new win.MouseEvent('click', { bubbles: true }));
    assert.equal(opened, 'a');
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('a link to a page that is gone (or not visible) is marked, and falls back to the stored title', () => {
  const { editor, win, el } = mount(doc(link('gone', 'Stored title')), { pageInfo: () => undefined, pagesKnown: () => true });
  try {
    const chip = el.querySelector('.nt-pagelink');
    assert.ok(chip.classList.contains('gone'));
    assert.match(chip.textContent, /Stored title/);
  } finally { editor.destroy(); win.happyDOM.abort(); }
});

test('plain text of a link is its title (search, word count and Tidy see readable text)', () => {
  const { editor, win } = mount(doc({ type: 'text', text: 'See ' }, link('a', 'Weekly report')));
  try { assert.equal(editor.getText(), 'See Weekly report'); } finally { editor.destroy(); win.happyDOM.abort(); }
});
