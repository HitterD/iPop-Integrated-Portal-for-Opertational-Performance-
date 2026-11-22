import test from 'node:test';
import assert from 'node:assert/strict';
import { Window } from 'happy-dom';
import { getSchema } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TaskList, TaskItem } from '@tiptap/extension-list';
import { sliceText, cleanOfficeHtml, preferFiles } from '../src/paste.js';

const schema = getSchema([StarterKit, TaskList, TaskItem]);
const j = json => schema.nodeFromJSON(json);
const p = t => ({ type: 'paragraph', content: t ? [{ type: 'text', text: t }] : undefined });
const li = t => ({ type: 'listItem', content: [p(t)] });

test('copied text: one line per paragraph, list markers kept, no doubled blank lines', () => {
  const doc = j({ type: 'doc', content: [
    p('Saran Notes iPop:'),
    { type: 'orderedList', attrs: { start: 1 }, content: [li('Symbol sederhana'), { type: 'listItem', content: [p('Ctrl S'), { type: 'bulletList', content: [li('nested')] }] }] },
    { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: true }, content: [p('done')] }] },
    p('end'),
  ] });
  assert.equal(sliceText(doc.content), 'Saran Notes iPop:\n1. Symbol sederhana\n2. Ctrl S\n  - nested\n[x] done\nend');
});

test('an empty paragraph stays one empty line', () => {
  assert.equal(sliceText(j({ type: 'doc', content: [p('a'), p(), p('b')] }).content), 'a\n\nb');
});

const win = new Window();
const parser = new win.DOMParser();

test('Word bullet and numbered paragraphs become real lists, Office junk is gone', () => {
  const html = `<html xmlns:o="urn:schemas-microsoft-com:office:office"><body><!--[if gte mso 9]><xml>x</xml><![endif]-->
    <p class=MsoNormal>Intro<o:p></o:p></p>
    <p class=MsoListParagraphCxSpFirst style='mso-list:l0 level1 lfo1'><span style='mso-list:Ignore'>·<span>&nbsp;&nbsp;</span></span>One<o:p></o:p></p>
    <p class=MsoListParagraphCxSpLast style='mso-list:l0 level1 lfo1'><span style='mso-list:Ignore'>·<span>&nbsp;</span></span>Two</p>
    <p class=MsoNormal>&nbsp;</p>
    <p class=MsoListParagraph style='mso-list:l1 level1 lfo2'><span style='mso-list:Ignore'>1.<span>&nbsp;</span></span>First</p>
    </body></html>`;
  const out = cleanOfficeHtml(html, parser);
  assert.match(out, /<ul><li><p>One<\/p><\/li><li><p>Two<\/p><\/li><\/ul>/);
  assert.match(out, /<ol><li><p>First<\/p><\/li><\/ol>/);
  assert.ok(!/Mso|o:p|mso-list|·/.test(out), out);
});

test('non-Office HTML is untouched', () => {
  assert.equal(cleanOfficeHtml('<p>Hello <b>x</b></p>', parser), '<p>Hello <b>x</b></p>');
});

test('Excel/Word copy (table + a picture of it) pastes the table, a real picture copy pastes the file', () => {
  const cd = html => ({ files: [{}], getData: t => (t === 'text/html' ? html : '') });
  assert.equal(preferFiles(cd('<table><tr><td>Qty</td></tr></table>')), false);
  assert.equal(preferFiles(cd('<img src="https://x/y.png">')), true);
  assert.equal(preferFiles(cd('')), true);
  assert.equal(preferFiles({ files: [] }), false);
});
