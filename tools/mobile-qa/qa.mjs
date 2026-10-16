// Mobile QA harness (docs/superpowers/plans/2026-10-16-mobile-app.md, "Shared QA protocol").
//   node tools/mobile-qa/qa.mjs --wave 1 --base http://127.0.0.1:5996 [--baseline http://127.0.0.1:5997]
//        --user 0001 --pw ... [--admin ipopadmin --admin-pw ...] [--only phone,landscape,diff,...]
// Output: tools/mobile-qa/out/w<N>/report.json + PNGs. Never run against prod.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';
import pixelmatch from 'pixelmatch';
import { browser, context, login, audit, settle, sleep, guardBase, out, dismissTour, ANDROID_UA, IPHONE_UA } from './lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? a.concat([[v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]]) : a), []));
const wave = String(args.wave || 1);
const BASE = args.base, BASELINE = args.baseline;
guardBase(BASE); if (BASELINE) guardBase(BASELINE);
const USER = args.user, PW = args.pw, ADMIN = args.admin, ADMIN_PW = args['admin-pw'];
const only = args.only ? String(args.only).split(',') : null;
const want = k => !only || only.includes(k);
const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = out(args.out || path.join(HERE, 'out', 'w' + wave));
const ROUTES = JSON.parse(fs.readFileSync(path.join(HERE, 'routes.json'), 'utf8'))[wave];
const report = { wave, base: BASE, baseline: BASELINE || null, at: new Date().toISOString(), phone: [], landscape: [], tablet: [], diff: [], checks: {} };
const save = () => fs.writeFileSync(path.join(OUT, 'report.json'), JSON.stringify(report, null, 2));
const shot = (p, name) => p.screenshot({ path: path.join(OUT, name + '.png') });

async function answerNotifCard(p) {
  const btn = p.locator('.ph-npc .ph-btn.pl, .ph-npc .ph-btn').last();
  if (await btn.count()) { await btn.click(); await sleep(400); }
}

async function openRoute(p, r) {
  if (r === '#more') { await p.goto(BASE + '/chat'); await settle(p); await dismissTour(p); await p.locator('.m-tabbar button.m-tab').click(); await sleep(600); return; }
  if (r === '#edit-tabs') {
    await openRoute(p, '#more'); await p.locator('.ph-more-edit').click(); await sleep(700); return;
  }
  await p.goto(BASE + r); await settle(p); await dismissTour(p);
}

const b = await browser();
try {
  // ── Phone portrait: 360 / 390 / 430, light + dark ──
  if (want('phone')) {
    for (const [w, h] of [[360, 780], [390, 844], [430, 932]]) for (const dark of [false, true]) {
      const ctx = await context(b, { w, h, dark });
      const p = await login(ctx, BASE, USER, PW);
      await answerNotifCard(p);
      for (const r of ROUTES.phone) {
        await openRoute(p, r);
        const name = `phone-${w}x${h}${dark ? '-dark' : ''}-${r.replace(/[#/]/g, '') || 'root'}`;
        await shot(p, name);
        const a = await audit(p);
        // FAB must not cover the last list row: a FAB, if any, sits above the tab bar.
        const fab = await p.evaluate(() => { const f = document.querySelector('.ph-fab'); const t = document.querySelector('.m-tabbar'); if (!f || !t) return null; return f.getBoundingClientRect().bottom <= t.getBoundingClientRect().top; });
        report.phone.push({ w, h, dark, route: r, ...a, fabAboveTabbar: fab, errors: p.errors.splice(0) });
      }
      await ctx.close(); save();
    }
  }

  // ── Short landscape phone: phone layout, no overflow, sheets fit ──
  if (want('landscape')) {
    const ctx = await context(b, { w: 844, h: 390 });
    const p = await login(ctx, BASE, USER, PW);
    await answerNotifCard(p);
    for (const r of ['/dashboard', '/chat', '#more', '#edit-tabs']) {
      await openRoute(p, r);
      const lay = await p.evaluate(() => {
        const g = s => { const e = document.querySelector(s); if (!e) return null; const c = getComputedStyle(e); const r = e.getBoundingClientRect(); return { d: c.display, top: Math.round(r.top), bottom: Math.round(r.bottom), h: Math.round(r.height) }; };
        return { tabbar: g('.m-tabbar'), sidebar: g('.shell > .sidebar'), sheet: g('.ph-sheet'), vh: innerHeight };
      });
      await shot(p, `landscape-844x390-${r.replace(/[#/]/g, '')}`);
      report.landscape.push({ route: r, ...lay, ...(await audit(p)), sheetFits: !lay.sheet || (lay.sheet.top >= 0 && lay.sheet.bottom <= lay.vh + 1) });
    }
    await ctx.close(); save();
  }

  // ── Pixel diff vs the previous prod build: 1440 (mouse) and the tablet compact desktop ──
  if (want('diff') && BASELINE) {
    const MASK = ['.tb-timer', 'time', '.apple-notif-row-time', '.nav-count', '.badge', '.m-tab-badge', '.avatar', '.av', '[class*="avatar"]', '[class*="clock"]', '.tb-live', '.dw-greet', '.dw-date', '.ipop-update-bar', '.toast', '.float-stack', '.cal-now'];
    for (const [w, h, mobile] of [[1440, 900, false], [1024, 768, true], [768, 1024, true]]) {
      const shotsFor = async base => {
        const ctx = await context(b, { w, h, mobile });
        await ctx.addInitScript(() => { const s = document.createElement('style'); s.textContent = '*,*::before,*::after{animation:none!important;transition:none!important;caret-color:transparent!important}'; document.addEventListener('DOMContentLoaded', () => document.head.appendChild(s)); });
        const p = await login(ctx, base, USER, PW);
        const res = {};
        for (const r of ROUTES.diff) {
          await p.goto(base + r);
          await p.waitForFunction(() => !document.querySelector('#app > .boot') && document.querySelector('.shell .main-panel') && document.fonts.status === 'loaded', null, { timeout: 60000 });
          await settle(p, 3500);
          await dismissTour(p);
          // Live text ("synced 13:32", relative times) changes width between the two captures and shifts
          // flex neighbours; pin it to one value so only real layout changes count.
          await p.evaluate(() => { const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT); for (let n; (n = w.nextNode());) if (/\b\d{1,2}[:.]\d{2}\b/.test(n.nodeValue)) n.nodeValue = n.nodeValue.replace(/\b\d{1,2}[:.]\d{2}\b/g, '00:00'); document.querySelectorAll('.cal-now').forEach(e => e.remove()); });
          await sleep(300);
          res[r] = await p.screenshot({ mask: MASK.map(m => p.locator(m)), maskColor: '#ff00ff' });
        }
        await ctx.close();
        return res;
      };
      const a = await shotsFor(BASELINE), c = await shotsFor(BASE);
      for (const r of ROUTES.diff) {
        const A = PNG.sync.read(a[r]), C = PNG.sync.read(c[r]);
        const tag = `diff-${w}x${h}-${r.replace(/\//g, '')}`;
        if (A.width !== C.width || A.height !== C.height) { report.diff.push({ w, h, route: r, sizeMismatch: [A.width, A.height, C.width, C.height] }); continue; }
        const D = new PNG({ width: A.width, height: A.height });
        const n = pixelmatch(A.data, C.data, D.data, A.width, A.height, { threshold: 0.1 });
        if (n) { fs.writeFileSync(path.join(OUT, tag + '-base.png'), a[r]); fs.writeFileSync(path.join(OUT, tag + '-new.png'), c[r]); fs.writeFileSync(path.join(OUT, tag + '-diff.png'), PNG.sync.write(D)); }
        report.diff.push({ w, h, route: r, changedPx: n });
      }
      save();
    }
  }
} finally {
  save();
  await b.close();
}
console.log(JSON.stringify({ phone: report.phone.length, landscape: report.landscape.length, diff: report.diff }, null, 1));
