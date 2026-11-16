import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { Details, DetailsContent } from '@tiptap/extension-details';
import { wordStats, findMatches, FindReplace, findKey, Columns, Column, columnsJson, dateLabel, isoDay, DateChip, EmojiShortcodes, SHORTCODES, ToggleSummary, toggleJson } from '../src/extras.js';

function mount(content) {
  const win = new Window(); globalThis.window = win; globalThis.document = win.document;
  for (const k of ['Node', 'HTMLElement', 'Element', 'MutationObserver', 'getComputedStyle', 'DOMParser']) if (win[k]) globalThis[k] = win[k];
  globalThis.requestAnimationFrame ??= cb => setTimeout(cb, 0);
  const element = win.document.createElement('div'); win.document.body.appendChild(element);
  const editor = new Editor({ element, content, extensions: [StarterKit, FindReplace, Columns, Column, DateChip, EmojiShortcodes, Details, ToggleSummary, DetailsContent] });
  return { editor, win };
}
const p = t => ({ type: 'paragraph', content: t ? [{ type: 'text', text: t }] : undefined });
const doc = (...c) => ({ type: 'doc', content: c });
const done = ({ editor, win }) => { editor.destroy(); win.happyDOM.abort(); };

test('word count: words, characters and reading time; an empty page reads zero', () => {
  const m = mount(doc(p('Hello brave new world'), p('second   line  here')));
  try {
    const s = wordStats(m.editor.state.doc);
    assert.equal(s.words, 7);
    assert.equal(s.minutes, 1);
    assert.ok(s.chars >= 30);
  } finally { done(m); }
  const e = mount(doc(p('')));
  try { assert.deepEqual(wordStats(e.editor.state.doc), { words: 0, chars: 0, minutes: 0 }); } finally { done(e); }
});

test('word count: a long page rounds to 200 words a minute', () => {
  const m = mount(doc(p(Array(450).fill('word').join(' '))));
  try { assert.equal(wordStats(m.editor.state.doc).minutes, 2); } finally { done(m); }
});

test('find: case-insensitive by default, case-sensitive on request, never across blocks, empty query finds nothing', () => {
  const m = mount(doc(p('Report report REPORT'), p('re'), p('port')));
  try {
    const d = m.editor.state.doc;
    assert.equal(findMatches(d, 'report').length, 3);
    assert.equal(findMatches(d, 'report', { caseSensitive: true }).length, 1);
    assert.equal(findMatches(d, 'report ').length, 2);
    assert.equal(findMatches(d, '').length, 0);
    assert.equal(findMatches(d, 'zzz').length, 0);
  } finally { done(m); }
});

test('find and replace: replace one, replace all, stepping wraps around', () => {
  const m = mount(doc(p('cat and cat and cat')));
  try {
    const e = m.editor;
    e.commands.setFind('cat');
    assert.equal(findKey.getState(e.state).matches.length, 3);
    e.commands.stepFind(1); e.commands.stepFind(1); e.commands.stepFind(1);
    assert.equal(findKey.getState(e.state).current, 0);
    e.commands.stepFind(-1);
    assert.equal(findKey.getState(e.state).current, 2);
    e.commands.setFind('cat');
    e.commands.replaceFind('dog');
    assert.equal(e.getText(), 'dog and cat and cat');
    e.commands.replaceFind('bird', true);
    assert.equal(e.getText(), 'dog and bird and bird');
    e.commands.setFind('bird'); e.commands.replaceFind('');
    assert.equal(e.getText(), 'dog and  and bird');
  } finally { done(m); }
});

test('find follows edits: matches are recomputed when the document changes', () => {
  const m = mount(doc(p('alpha beta')));
  try {
    m.editor.commands.setFind('beta');
    assert.equal(findKey.getState(m.editor.state).matches.length, 1);
    m.editor.commands.insertContentAt(1, 'beta ');
    assert.equal(findKey.getState(m.editor.state).matches.length, 2);
  } finally { done(m); }
});

test('columns: 2 to 4 columns; one or five are rejected', () => {
  const m = mount(doc(columnsJson(3)));
  try {
    assert.equal(m.editor.state.doc.firstChild.childCount, 3);
    assert.equal(m.editor.getHTML().match(/data-column=/g).length, 3);
    assert.throws(() => m.editor.schema.nodeFromJSON(columnsJson(1)).check());
    assert.throws(() => m.editor.schema.nodeFromJSON(columnsJson(5)).check());
  } finally { done(m); }
});

test('date label: today, tomorrow, yesterday, plain date, junk', () => {
  const today = new Date(2026, 11, 10);
  assert.equal(dateLabel('2026-12-10', today), 'Today');
  assert.equal(dateLabel('2026-12-11', today), 'Tomorrow');
  assert.equal(dateLabel('2026-12-09', today), 'Yesterday');
  assert.match(dateLabel('2026-12-25', today), /25/);
  assert.match(dateLabel('2027-01-05', today), /2027/);
  assert.equal(dateLabel('', today), 'Pick a date');
  assert.equal(dateLabel('2026-02-31', today), 'Pick a date');
  assert.equal(dateLabel('tomorrow', today), 'Pick a date');
  assert.equal(isoDay(new Date(2026, 0, 5)), '2026-01-05');
});

test('date chip keeps its date through HTML and reads as the date in plain text', () => {
  const m = mount(doc({ type: 'paragraph', content: [{ type: 'dateChip', attrs: { date: '2026-12-25' } }] }));
  try {
    assert.match(m.editor.getHTML(), /data-date="2026-12-25"/);
    assert.equal(m.editor.getText(), '2026-12-25');
  } finally { done(m); }
});

test('emoji shortcodes: known names convert, unknown names and clock times are left alone', () => {
  assert.equal(SHORTCODES.tada, '🎉');
  assert.ok(Object.keys(SHORTCODES).length >= 100);
  const type = (m, text) => {
    for (const ch of text) {
      const { from, to } = m.editor.state.selection;
      const handled = m.editor.view.someProp('handleTextInput', f => f(m.editor.view, from, to, ch));
      if (!handled) m.editor.view.dispatch(m.editor.state.tr.insertText(ch, from, to));
    }
  };
  const a = mount(doc(p('')));
  try { a.editor.commands.focus('end'); type(a, 'done :tada:'); assert.equal(a.editor.getText(), 'done 🎉'); } finally { done(a); }
  const b = mount(doc(p('')));
  try { b.editor.commands.focus('end'); type(b, 'meet at 10:30:'); assert.equal(b.editor.getText(), 'meet at 10:30:'); } finally { done(b); }
  const c = mount(doc(p('')));
  try { c.editor.commands.focus('end'); type(c, 'a :nosuchthing:'); assert.equal(c.editor.getText(), 'a :nosuchthing:'); } finally { done(c); }
});

test('toggle heading: the title carries a level, a plain toggle does not', () => {
  const m = mount(doc(toggleJson(2, 'Section'), toggleJson(0, 'Plain')));
  try {
    const html = m.editor.getHTML();
    assert.match(html, /data-level="2"/);
    assert.equal((html.match(/data-level=/g) || []).length, 1);
  } finally { done(m); }
});
