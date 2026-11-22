import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

// Paket C regression: Blazor called editor.scrollToBlock (a ?b= block link on the page that is already open) but the editor
// handle never had it, so the link silently did nothing. Every method the Notes page invokes on the editor handle must exist
// in the object mount() returns.
const pages = join(import.meta.dirname, '../../../src/IPop.Web/Pages');
const cs = readdirSync(pages).filter(f => /^Notes(\..+)?\.(cs|razor)$/.test(f)).map(f => readFileSync(join(pages, f), 'utf8')).join('\n');
const called = new Set([...cs.matchAll(/\beditor!?\.Invoke(?:Void)?Async(?:<[^>]+>)?\("([A-Za-z]+)"/g)].map(m => m[1]));
const src = readFileSync(join(import.meta.dirname, process.env.IDX || '../src/index.js'), 'utf8').replace(/\r\n/g, '\n');
const handle = src.slice(src.indexOf('  return {\n    closeMobileMention'), src.indexOf('/// Page-level shortcuts'));

test('the Notes page only calls editor methods that exist', () => {
  assert.ok(called.size > 10, `found ${called.size} calls`);
  const missing = [...called].filter(m => !new RegExp(`\\n\\s+(async\\s+)?${m}\\s*\\(`).test(handle));
  assert.deepEqual(missing, []);
});
