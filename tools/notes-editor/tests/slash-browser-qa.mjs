// Isolated real-Chrome layout smoke test; no API, account or production writes.
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { build } from 'esbuild';
const require = createRequire('C:/Users/IT12/orca/workspaces/iPop dotnet 10/hind/.scratch/ceo-deck/seed/x.js');
const { chromium } = require('playwright');
const bundle = await build({ stdin: { contents: "import { slashPopup } from './src/slash-popup.js'; window.makePopup = slashPopup;", resolveDir: process.cwd() }, bundle: true, write: false, format: 'iife' });
const browser = await chromium.launch({ executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1100, height: 850 } });
  const css = readFileSync('../../src/IPop.Web/wwwroot/css/v6/notes.css', 'utf8');
  await page.setContent(`<style>:root{--surface:#fff;--fill:#f3f6f4;--ink:#202a25;--ink-2:#45564c;--ink-3:#68786f;--line:#dce3df}body{margin:0;height:1600px}${css}</style>`);
  await page.addScriptTag({ content: bundle.outputFiles[0].text });
  await page.evaluate(() => {
    const items = ['Text', 'Heading 2', 'To-do list', 'Image', 'Whiteboard'].map((name, i) => ({ name, group: ['Text', 'Text', 'Lists', 'Media', 'Insert'][i], desc: 'Preview purpose', box: 'T' }));
    window.popup = window.makePopup(pop => { pop.classList.add('show'); pop.style.top = '40px'; pop.style.left = '8px'; });
    window.props = { items, query: '', command: item => window.chosen = item.name, clientRect: () => ({}) };
    window.popup.onStart(window.props);
  });
  await page.locator('[data-category="Lists"]').dispatchEvent('mousedown');
  if (await page.locator('.it').count() !== 1) throw new Error('Category did not filter');
  if (!/To-do list/.test(await page.locator('.nt-slash-preview').innerText())) throw new Error('No preview');
  await page.evaluate(() => { window.popup.onUpdate({ ...window.props, loading: true, items: [] }); window.popup.onUpdate(window.props); });
  if (await page.evaluate(() => scrollY) !== 0) throw new Error('Document scrolled');
  await page.evaluate(() => window.popup.onKeyDown({ event: { key: 'Enter' } }));
  if (await page.evaluate(() => window.chosen) !== 'To-do list') throw new Error('Wrong keyboard command');
  await page.setViewportSize({ width: 390, height: 844 });
  const dimensions = await page.locator('.nt-slash').boundingBox();
  if (dimensions.x + dimensions.width > 390) throw new Error('Phone overflow');
  if (await page.evaluate(() => document.documentElement.scrollWidth > innerWidth)) throw new Error('Page horizontal overflow');
  console.log('PASS: categories, preview, collaborative repaint, keyboard selection, no document scroll, 390px phone layout');
} finally { await browser.close(); }
