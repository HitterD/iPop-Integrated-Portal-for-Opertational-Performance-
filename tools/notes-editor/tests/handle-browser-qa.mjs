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
const editor = new Editor({ element: document.querySelector('#ed'), content: '<p>Evaluasi :</p><ol><li><p>Fokus menjawab</p></li><li><p>Security program</p></li><li><p>Story telling</p></li><li><p></p></li></ol><p>PPT - review bersama</p>',
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
  // the number glyph is drawn right-aligned against the <li> box (outside marker); measure its real left edge
  const markerLeft = await p.evaluate(() => {
    const li = document.querySelectorAll('.nt-editor ol > li')[1];
    const probe = document.createElement('span'); probe.textContent = '2.'; probe.style.cssText = 'position:absolute;visibility:hidden;font-weight:600;white-space:nowrap';
    li.appendChild(probe); const w = probe.getBoundingClientRect().width; probe.remove();
    return li.getBoundingClientRect().left - w - parseFloat(getComputedStyle(li, '::marker').marginInlineEnd || 0) - 6;
  });
  const grip = await p.evaluate(() => { const b = [...document.querySelectorAll('.nt-handle button')].pop().getBoundingClientRect(); return b.right; });
  console.log(JSON.stringify({ gripRight: grip, markerLeft }));
  if (grip > markerLeft + 1) throw new Error(`grip overlaps the number: grip right ${grip} > number left ${markerLeft}`);
  console.log('PASS handle sits left of the number marker on the hovered row');
  // Travel: from the middle of the text, move left in small steps onto the handle; it must stay visible all the way.
  const row = await p.locator('.nt-editor ol > li').last().boundingBox();
  const y = row.y + row.height / 2;
  await p.mouse.move(row.x + 300, y); await p.waitForTimeout(300);
  const hb = await p.locator('.nt-handle').boundingBox();
  const target = hb.x + hb.width - 10;   // the grip button
  let hidden = null;
  for (let x = row.x + 300; x >= target; x -= 3) {
    await p.mouse.move(x, y); await p.waitForTimeout(20);
    const vis = await p.evaluate(() => getComputedStyle(document.querySelector('.nt-handle')).visibility);
    if (vis === 'hidden') { hidden = x; break; }
  }
  if (hidden !== null) throw new Error(`handle disappeared while moving left, at x=${hidden} (handle right ${hb.x + hb.width}, list left ${r.olLeft})`);
  const under = await p.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest('.nt-handle') !== null, [target, y]);
  if (!under) throw new Error('pointer over the grip does not hit the handle');
  console.log('PASS moving left from the text onto the handle keeps it visible and reachable');
} finally { await b.close(); }
