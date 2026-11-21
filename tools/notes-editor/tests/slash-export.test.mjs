import test from 'node:test';
import assert from 'node:assert/strict';
import { filterSlash, SLASH_ALIASES, TURN_INTO } from '../src/menus.js';
import { docToMarkdown } from '../src/transfer.js';

const cmds = ['Text', 'Heading 1', 'To-do list', 'Toggle', 'Toggle heading 1', 'Callout', 'Divider', '2 columns', 'Table of contents', 'Date', 'Code']
  .map(name => ({ name, group: 'g', desc: '', box: '', run() {} }));
const names = q => filterSlash(cmds, q).map(c => c.name);

test('slash search: names first, then words in a name, then aliases', () => {
  assert.deepEqual(names('tog').slice(0, 2), ['Toggle', 'Toggle heading 1']);
  assert.ok(names('col').includes('2 columns') && names('col').includes('Callout') === false);
  assert.equal(names('')[0], 'Text');
  assert.equal(names('zzzz').length, 0);
});

test('slash search: aliases find commands that are not in the name', () => {
  assert.ok(names('checkbox').includes('To-do list'));
  assert.ok(names('hr').includes('Divider'));
  assert.ok(names('warning').includes('Callout'));
  assert.ok(names('toc').includes('Table of contents'));
  assert.ok(names('h1').includes('Heading 1'));
  assert.ok(names('calendar').includes('Date'));
  assert.ok(names('snippet').includes('Code'));
  assert.ok(Object.keys(SLASH_ALIASES).length >= 15);
});

test('every Turn into entry has a key, a name and a runnable action; callout and toggle are there', () => {
  assert.ok(TURN_INTO.find(t => t.key === 'callout'));
  assert.ok(TURN_INTO.find(t => t.key === 'toggle'));
  assert.equal(new Set(TURN_INTO.map(t => t.key)).size, TURN_INTO.length);
  for (const t of TURN_INTO) assert.equal(typeof t.run, 'function');
});

const ctx = { link: () => null, file: () => null, draw: () => null, dir: '', title: () => '' };
const para = (...content) => ({ type: 'paragraph', content });
const text = t => ({ type: 'text', text: t });

test('markdown export: a date chip is its date, columns become sections, a toggle heading keeps its level', () => {
  const md = docToMarkdown({ type: 'doc', content: [
    para(text('Due '), { type: 'dateChip', attrs: { date: '2026-12-25' } }),
    { type: 'columns', content: [
      { type: 'column', content: [para(text('Left'))] },
      { type: 'column', content: [para(text('Right'))] },
    ] },
    { type: 'details', content: [
      { type: 'detailsSummary', attrs: { level: 2 }, content: [text('Open me')] },
      { type: 'detailsContent', content: [para(text('Inside'))] },
    ] },
  ] }, ctx);
  assert.match(md, /Due 2026-12-25/);
  assert.match(md, /Left[\s\S]*---[\s\S]*Right/);
  assert.match(md, /<summary><h2>Open me<\/h2><\/summary>/);
  assert.match(md, /Inside/);
});
