// Wave 1: existing pages still open fine inside the new phone shell (chat room, mail, calendar, note).
//   node tools/mobile-qa/w1-pages.mjs --base http://127.0.0.1:5996 --user 0001 --pw ..
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { browser, context, login, sleep, guardBase, out, open, audit } from './lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? a.concat([[v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]]) : a), []));
const BASE = args.base; guardBase(BASE);
const OUT = out(args.out || path.join(path.dirname(fileURLToPath(import.meta.url)), 'out', 'w1-pages'));
const R = [];
const b = await browser();
try {
  for (const dark of [false, true]) {
    const ctx = await context(b, { w: 390, h: 844, dark });
    const p = await login(ctx, BASE, args.user, args.pw);
    const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }
    const step = async (name, fn) => {
      let err = null;
      try { await fn(); } catch (e) { err = e.message.split('\n')[0]; }
      await sleep(800);
      const st = await p.evaluate(() => ({ url: location.pathname, tabbar: getComputedStyle(document.querySelector('.m-tabbar') || document.body).display, boot: !!document.querySelector('#app > .boot'), errUi: !!document.querySelector('#blazor-error-ui[style*="block"]'), invite: !!document.querySelector('.tut-invite') }));
      await p.screenshot({ path: path.join(OUT, `${name}${dark ? '-dark' : ''}.png`) });
      const a = await audit(p);
      const r = { name, dark, err, ...st, overflow: a.overflow || a.overN > 0, over: a.over, errors: p.errors.splice(0) };
      R.push(r); console.log((err || st.errUi || r.errors.length || r.overflow ? 'CHECK ' : 'OK    ') + JSON.stringify(r).slice(0, 400));
    };
    await step('chat-room', async () => { await open(p, BASE + '/chat'); await p.locator('.room-item, .room-row, [class*="room-list"] [role=button], [class*="room-list"] a').first().click({ timeout: 8000 }); await sleep(1500); });
    await step('chat-back', async () => { await p.goBack(); await sleep(1200); });
    await step('mail-open', async () => { await open(p, BASE + '/mail'); await p.getByText('Mailboxes', { exact: true }).first().click({ timeout: 5000 }); await sleep(900); await p.getByText('Sent', { exact: true }).first().click({ timeout: 5000 }); await sleep(1500); });
    await step('calendar', async () => { await open(p, BASE + '/calendar'); });
    await step('notes', async () => { await open(p, BASE + '/notes'); await p.getByText('Mulai di sini: Panduan Notes').first().click({ timeout: 8000 }); await sleep(2500); });
    await step('dashboard', async () => { await open(p, BASE + '/dashboard'); });
    await ctx.close();
  }
} finally { fs.writeFileSync(path.join(OUT, 'pages.json'), JSON.stringify(R, null, 2)); await b.close(); }
