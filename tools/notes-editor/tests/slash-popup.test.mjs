import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { categoryFor, readRecent, rememberRecent, slashPopup } from '../src/slash-popup.js';

test('commands are grouped by function and recent storage tolerates failures', () => {
  assert.equal(categoryFor('Heading 2'), 'Text');
  assert.equal(categoryFor('Numbered list'), 'Lists');
  assert.equal(categoryFor('Image'), 'Media');
  assert.equal(categoryFor('Toggle heading 2'), 'Structure');
  assert.equal(categoryFor('Whiteboard'), 'Insert');
  assert.deepEqual(readRecent(null), []);
  assert.deepEqual(rememberRecent(null, 'Image'), ['Image']);
});

test('picker categories, search, preview, keyboard and collaborative updates', () => {
  const win = new Window({ url: 'http://localhost/' });
  globalThis.window = win; globalThis.document = win.document;
  const names = ['Text', 'Heading 2', 'To-do list', 'Image', 'Whiteboard'];
  const items = names.map(name => ({ name, group: categoryFor(name), desc: `Purpose of ${name}`, box: 'T', kbd: '' }));
  let picked = null;
  const popup = slashPopup(() => {});
  const props = { items, query: '', command: item => { picked = item.name; }, clientRect: () => ({}) };
  try {
    popup.onStart(props);
    assert.equal(document.querySelectorAll('[role="tab"]').length, 6);
    const tab = document.querySelector('[data-category="Insert"]');
    tab.dispatchEvent(new win.MouseEvent('mousedown', { bubbles: true }));
    assert.equal(document.querySelectorAll('.it').length, 1);
    assert.match(document.querySelector('.nt-slash-preview').textContent, /Whiteboard/);
    const row = document.querySelector('.it');
    popup.onUpdate({ ...props, loading: true, items: [] });
    popup.onUpdate(props);
    assert.equal(document.querySelector('.it'), row);
    popup.onKeyDown({ event: { key: 'Enter' } });
    assert.equal(picked, 'Whiteboard');
    assert.equal(readRecent(win.localStorage)[0], 'Whiteboard');
    popup.onUpdate({ ...props, query: 'image', items: [items[3]] });
    assert.match(document.querySelector('.nt-slash-preview').textContent, /Image/);
    popup.onKeyDown({ event: { key: 'Escape' } });
    popup.onUpdate(props);
    assert.equal(document.querySelector('.nt-slash'), null);
  } finally { popup.onExit(); win.happyDOM.abort(); }
});
