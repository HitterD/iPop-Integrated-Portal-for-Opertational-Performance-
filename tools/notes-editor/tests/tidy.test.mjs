import test from 'node:test';
import assert from 'node:assert/strict';
import { analyze, apply } from '../src/tidy.js';

const t = text => ({ type: 'text', text });
const p = text => ({ type: 'paragraph', ...(text ? { content: [t(text)] } : {}) });
const h = (level, text) => ({ type: 'heading', attrs: { level }, content: [t(text)] });
const li = text => ({ type: 'listItem', content: [p(text)] });
const ul = (...items) => ({ type: 'bulletList', content: items.map(li) });
const ol = (...items) => ({ type: 'orderedList', attrs: { start: 1 }, content: items.map(li) });
const doc = (...content) => ({ type: 'doc', content });
const ids = (d, o) => analyze(d, o).map(s => s.id);

test('a clean page has nothing to suggest', () => {
  const d = doc(h(1, 'Plan'), p('Intro'), h(2, 'Steps'), ul('a', 'b'), p('end'));
  assert.deepEqual(analyze(d, { title: 'Plan' }), []);
});

test('untitled page: title comes from the first heading, else the first line, and is trimmed', () => {
  assert.equal(analyze(doc(p(''), h(2, 'Weekly report'), p('x')), { title: '' })[0].value, 'Weekly report');
  assert.equal(analyze(doc(p('Just a line')), { title: '  ' })[0].value, 'Just a line');
  const long = 'x'.repeat(200);
  assert.ok(analyze(doc(p(long)), { title: '' })[0].value.length <= 80);
  assert.equal(ids(doc(h(1, 'A')), { title: 'Already named' }).includes('title'), false);
  assert.equal(ids(doc(p('')), { title: '' }).includes('title'), false);   // nothing to take a title from
  assert.equal(apply(doc(h(1, 'Hello')), 'title').title, 'Hello');
});

test('empty lines: one is normal, a run of three leaves one, the count is the number removed', () => {
  assert.equal(ids(doc(p('a'), p(''), p('b'))).includes('blank'), false);
  const d = doc(p('a'), p(''), p(''), p(''), p('b'));
  const s = analyze(d).find(x => x.id === 'blank');
  assert.match(s.label, /Remove 2 extra empty lines/);
  const r = apply(d, 'blank').doc.content;
  assert.deepEqual(r.map(n => n.type), ['paragraph', 'paragraph', 'paragraph']);
  assert.equal(r[1].content, undefined);          // the kept one is empty
  assert.equal(r[2].content[0].text, 'b');
  assert.equal(analyze(doc(p('a'), p(''), p(''))).find(x => x.id === 'blank').label, 'Remove 1 extra empty line');
});

test('headings that skip a level are pulled down; a page that starts at H3 is left alone', () => {
  const d = doc(h(1, 'Top'), h(3, 'Deep'), h(4, 'Deeper'), h(2, 'Back'));
  assert.ok(ids(d).includes('heading'));
  const levels = apply(d, 'heading').doc.content.map(n => n.attrs.level);
  assert.deepEqual(levels, [1, 2, 3, 2]);
  assert.equal(ids(doc(h(3, 'Starts deep'), h(4, 'ok'))).includes('heading'), false);
  assert.equal(ids(doc(h(1, 'a'), h(2, 'b'), h(1, 'c'), h(3, 'skip again'))).includes('heading'), true);
});

test('split lists of the same kind join; different kinds and text between them do not', () => {
  const d = doc(ul('a'), p(''), ul('b'), p(''), p(''), ul('c'));
  assert.ok(ids(d).includes('list'));
  const out = apply(d, 'list').doc.content;
  assert.equal(out.length, 1);
  assert.deepEqual(out[0].content.map(i => i.content[0].content[0].text), ['a', 'b', 'c']);
  assert.equal(ids(doc(ul('a'), p(''), ol('b'))).includes('list'), false);          // different kind
  assert.equal(ids(doc(ul('a'), p('text between'), ul('b'))).includes('list'), false);
  assert.equal(ids(doc(ul('a'), ul('b'))).includes('list'), false);                 // already adjacent: not our job
});

test('a paragraph that is only a link becomes a bookmark; links inside a sentence are left alone', () => {
  const d = doc(p('See below'), p('https://intra.sja.co.id/it/wiki/win7-list'), p('Visit https://example.com today'));
  assert.equal(analyze(d).find(x => x.id === 'link').label, 'Turn a bare link into a bookmark');
  const out = apply(d, 'link').doc.content;
  assert.equal(out[1].type, 'noteBookmark');
  assert.equal(out[1].attrs.url, 'https://intra.sja.co.id/it/wiki/win7-list');
  assert.equal(out[1].attrs.site, 'intra.sja.co.id');
  assert.equal(out[2].type, 'paragraph');
  assert.equal(ids(doc(p('javascript:alert(1)'))).includes('link'), false);        // only http(s)
});

test('table of contents is offered from 3 headings and goes before the first heading, once', () => {
  assert.equal(ids(doc(h(1, 'a'), h(2, 'b'))).includes('toc'), false);
  const d = doc(p('intro'), h(1, 'a'), h(2, 'b'), h(2, 'c'));
  assert.ok(ids(d).includes('toc'));
  const out = apply(d, 'toc').doc.content;
  assert.deepEqual(out.map(n => n.type), ['paragraph', 'noteToc', 'heading', 'heading', 'heading']);
  assert.equal(ids(apply(d, 'toc').doc).includes('toc'), false);                  // not offered again
});

test('applying never mutates the input document', () => {
  const d = doc(p('a'), p(''), p(''), h(1, 'x'), h(3, 'y'));
  const before = JSON.stringify(d);
  for (const r of ['blank', 'heading', 'list', 'link', 'toc', 'title']) apply(d, r);
  assert.equal(JSON.stringify(d), before);
});

test('applying every suggestion leaves a page with no suggestions', () => {
  let d = doc(p(''), p(''), p(''), h(1, 'Report'), h(3, 'Skips'), ul('a'), p(''), ul('b'), p('https://example.com/x'), h(2, 'More'));
  let title = '';
  for (let guard = 0; guard < 10; guard++) {
    const s = analyze(d, { title });
    if (!s.length) break;
    const r = apply(d, s[0].rule, { title });
    d = r.doc; if (r.title) title = r.title;
  }
  assert.deepEqual(analyze(d, { title }), []);
  assert.equal(title, 'Report');
});
