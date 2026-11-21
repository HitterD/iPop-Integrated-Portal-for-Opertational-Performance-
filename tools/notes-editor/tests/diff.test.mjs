import test from 'node:test';
import assert from 'node:assert/strict';
import { blockTexts, diffLines, foldSame } from '../src/extras.js';

const p = t => ({ type: 'paragraph', content: t ? [{ type: 'text', text: t }] : undefined });

test('block texts: one string per block, empty blocks skipped, lists and tasks flattened, special blocks named', () => {
  const doc = { type: 'doc', content: [
    { type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Plan' }] },
    p(''), p('First'),
    { type: 'bulletList', content: [{ type: 'listItem', content: [p('one')] }, { type: 'listItem', content: [p('two')] }] },
    { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: true }, content: [p('done')] }, { type: 'taskItem', attrs: { checked: false }, content: [p('todo')] }] },
    { type: 'noteDraw', attrs: { id: 'x' } },
    { type: 'noteImage', attrs: { id: 'y', caption: 'Rack' } },
    { type: 'paragraph', content: [{ type: 'text', text: 'Ping ' }, { type: 'mention', attrs: { label: 'Rina' } }, { type: 'text', text: ' on ' }, { type: 'dateChip', attrs: { date: '2026-12-25' } }] },
  ] };
  assert.deepEqual(blockTexts(doc), ['Plan', 'First', 'one', 'two', '[x] done', '[ ] todo', '[drawing]', '[image: Rack]', 'Ping @Rina on 2026-12-25']);
  assert.deepEqual(blockTexts({ type: 'doc' }), []);
  assert.deepEqual(blockTexts(null), []);
});

test('diff: unchanged, added, removed and changed lines come out in reading order', () => {
  const d = diffLines(['a', 'b', 'c'], ['a', 'c', 'd']);
  assert.deepEqual(d.map(x => `${x.kind}:${x.text}`), ['same:a', 'del:b', 'same:c', 'add:d']);
  assert.deepEqual(diffLines([], ['x']).map(x => x.kind), ['add']);
  assert.deepEqual(diffLines(['x'], []).map(x => x.kind), ['del']);
  assert.deepEqual(diffLines([], []), []);
  const changed = diffLines(['keep', 'old line'], ['keep', 'new line']);
  assert.deepEqual(changed.map(x => `${x.kind}:${x.text}`), ['same:keep', 'del:old line', 'add:new line']);
});

test('diff: identical documents have no changes, and a moved block shows as removed + added', () => {
  assert.ok(diffLines(['a', 'b'], ['a', 'b']).every(x => x.kind === 'same'));
  const moved = diffLines(['a', 'b', 'c'], ['b', 'c', 'a']);
  assert.equal(moved.filter(x => x.kind !== 'same').length, 2);
});

test('diff: a huge page falls back to everything-changed instead of freezing', () => {
  const big = Array.from({ length: 2500 }, (_, i) => `line ${i}`);
  const d = diffLines(big, ['x']);
  assert.equal(d.length, 2501);
  assert.equal(d.filter(x => x.kind === 'del').length, 2500);
});

test('fold: long unchanged stretches collapse, one line of context stays around each change', () => {
  const lines = [...Array.from({ length: 10 }, (_, i) => ({ kind: 'same', text: `s${i}` })), { kind: 'add', text: 'NEW' }, ...Array.from({ length: 4 }, (_, i) => ({ kind: 'same', text: `t${i}` }))];
  const f = foldSame(lines);
  assert.deepEqual(f.map(x => x.kind), ['fold', 'same', 'add', 'same', 'fold']);
  assert.equal(f[0].text, '9 unchanged blocks');
  assert.equal(f.at(-1).text, '3 unchanged blocks');
  assert.deepEqual(foldSame([{ kind: 'same', text: 'only' }]).map(x => x.kind), ['fold']);
  assert.equal(foldSame([{ kind: 'same', text: 'only' }])[0].text, '1 unchanged block');
});
