import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSticky } from '../src/sticky-task.js';

const today = new Date(2026, 10, 16);   // 16 Nov 2026

test('title, person and date come apart, in any order', () => {
  for (const text of ['Order 4 new switches @Yudi 25 Nov', '@Yudi Order 4 new switches 25 Nov', 'Order 4 new switches 25 Nov @Yudi']) {
    const r = parseSticky(text, today);
    assert.equal(r.title, 'Order 4 new switches', text);
    assert.equal(r.who, 'Yudi', text);
    assert.equal(r.due.iso, '2026-11-25T00:00:00Z', text);
  }
});

test('"4 new" is not a date; a real month name after a number is', () => {
  assert.equal(parseSticky('Buy 4 new chairs', today).due, null);
  assert.equal(parseSticky('Buy 4 new chairs', today).title, 'Buy 4 new chairs');
  assert.equal(parseSticky('Label 3 racks before 2 Dec', today).due.iso, '2026-12-02T00:00:00Z');
  assert.equal(parseSticky('Label 3 racks before 2 Dec', today).title, 'Label 3 racks');
});

test('Indonesian month names, ISO dates and day/month', () => {
  assert.equal(parseSticky('Rapat 5 Mei', today).due.iso, '2027-05-05T00:00:00Z', 'May already passed this year: next year');
  assert.equal(parseSticky('Audit 10 Agustus', today).due.iso, '2027-08-10T00:00:00Z');
  assert.equal(parseSticky('Kirim laporan 2026-12-01', today).due.iso, '2026-12-01T00:00:00Z');
  assert.equal(parseSticky('Kirim 20/12', today).due.iso, '2026-12-20T00:00:00Z');
});

test('impossible dates, empty text and an e-mail address are left alone', () => {
  assert.equal(parseSticky('Fix 31 Feb', today).due, null);
  assert.equal(parseSticky('Fix 31 Feb', today).title, 'Fix 31 Feb');
  assert.deepEqual(parseSticky('', today), { title: '', who: null, due: null });
  assert.equal(parseSticky('Mail bagas@sja.co.id', today).who, null);
  assert.equal(parseSticky('Only the first line\nmore details', today).title, 'Only the first line');
});
