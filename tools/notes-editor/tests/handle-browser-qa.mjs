// Real Chrome check: the drag handle must end left of the list marker (no overlap). Standalone page, no API.
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { build } from 'esbuild';
const require = createRequire('C:/Users/IT12/orca/workspaces/iPop dotnet 10/hind/.scratch/ceo-deck/seed/x.js');
const { chromium } = require('playwright');
const entry = `
import { Editor } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import DragHandle from '@tiptap/extension-drag-handle';
import { handleClearsMarker } from './src/handle-position.js';
let type = null, pos = -1;
const handle = document.createElement('div'); handle.className = 'nt-handle'; handle.innerHTML = '<button>+</button><button>::</button>';
const editor = new Editor({ element: document.querySelector('#ed'), content: '<p>Evaluasi :</p><ol><li><p>Fokus menjawab</p></li><li><p>Security program</p></li><li><p>Story telling</p></li></ol><p>after</p>',
  editorProps: { attributes: { class: 'nt-editor' } },
  extensions: [StarterKit, DragHandle.configure({ render: () => handle,
    nested: { allowedContainers: ['bulletList','orderedList'], edgeDetection: 'left' },
    computePositionConfig: { placement: 'left-start', middleware: [handleClearsMarker(() => ({ type, el: pos >= 0 ? editor.view.nodeDOM(pos) : null }))] },
    onNodeChange: ({ node, pos: p }) => { pos = p ?? -1; type = node?.type.name ?? null; } })] });
window.ed = editor;`;
const out = await build({ stdin: { contents: entry, resolveDir: process.cwd() }, bundle: true, write: false, format: 'iife' });
const css = readFileSync('../../src/IPop.Web/wwwroot/css/v6/notes.css', 'utf8');
const b = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
try {
  const p = await b.newPage({ viewport: { width: 1200, height: 700 } });
  await p.setContent(`<style>:root{--surface:#fff;--ink:#222;--ink-3:#777;--line:#ddd;--fill:#f3f3f3}body{margin:0;padding:40px 160px;font:15px system-ui}${css}</style><div id="ed"></div>`);
  await p.addScriptTag({ content: out.outputFiles[0].text });
  await p.waitForTimeout(300);
  const li = p.locator('.nt-editor ol > li').nth(1);
  const box = await li.boundingBox();
  await p.mouse.move(box.x + 40, box.y + box.height / 2); await p.waitForTimeout(400);
  await p.mouse.move(box.x + 60, box.y + box.height / 2); await p.waitForTimeout(400);
  const r = await p.evaluate(() => {
    const h = document.querySelector('.nt-handle').getBoundingClientRect();
    const li = document.querySelectorAll('.nt-editor ol > li')[1];
    const ol = li.parentElement.getBoundingClientRect();
    return { handleRight: h.right, handleTop: h.top, liLeft: li.getBoundingClientRect().left, liTop: li.getBoundingClientRect().top, olLeft: ol.left, visible: getComputedStyle(document.querySelector('.nt-handle')).visibility };
  });
  console.log(JSON.stringify(r));
  await p.screenshot({ path: 'tests/handle-qa.png', clip: { x: 0, y: 0, width: 700, height: 220 } });
  if (r.visible === 'hidden') throw new Error('handle not shown');
  if (Math.abs(r.handleTop - r.liTop) > 6) throw new Error('handle is not on the hovered row');
  if (r.handleRight > r.olLeft + 1) throw new Error(`handle overlaps the marker: right ${r.handleRight} > marker start ${r.olLeft}`);
  console.log('PASS handle sits left of the number marker on the hovered row');
} finally { await b.close(); }
