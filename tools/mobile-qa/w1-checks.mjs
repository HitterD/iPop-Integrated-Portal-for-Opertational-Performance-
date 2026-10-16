// Wave 1 functional checks (plan: Wave 1 QA). Copy DB + local QA hosts only.
//   node tools/mobile-qa/w1-checks.mjs --base http://127.0.0.1:5996 --api http://127.0.0.1:5995 --user 0001 --pw .. --admin ipopadmin --admin-pw ..
//        [--only tour,tabs,more,notif,safe,install,sw,app,newbuild] [--swap "<cmd that swaps the web build>"]
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { chromium } from 'playwright';
const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
import { fileURLToPath } from 'node:url';
import { browser, context, login, settle, sleep, guardBase, out, open, dismissTour, ANDROID_UA, IPHONE_UA } from './lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? a.concat([[v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]]) : a), []));
const BASE = args.base, API = args.api; guardBase(BASE); guardBase(API);
const only = args.only ? String(args.only).split(',') : null;
const want = k => !only || only.includes(k);
const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = out(args.out || path.join(HERE, 'out', 'w1-checks'));
const R = {}; const save = () => fs.writeFileSync(path.join(OUT, 'checks.json'), JSON.stringify(R, null, 2));
const shot = (p, n) => p.screenshot({ path: path.join(OUT, n + '.png') });
const ok = (k, pass, info) => { R[k] = { pass: !!pass, ...(info || {}) }; console.log((pass ? 'PASS ' : 'FAIL ') + k + (info ? ' ' + JSON.stringify(info).slice(0, 300) : '')); save(); };

async function token(nik, pw) {
  const r = await fetch(API + '/api/auth/login', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ nik, password: pw }) });
  return (await r.json()).accessToken;
}
async function api(tok, method, url, body) {
  const r = await fetch(API + url, { method, headers: { authorization: 'Bearer ' + tok, ...(body !== undefined ? { 'content-type': 'application/json' } : {}) }, body: body !== undefined ? JSON.stringify(body) : undefined });
  const t = await r.text(); let j = null; try { j = JSON.parse(t); } catch { }
  return { status: r.status, body: j ?? t };
}
const tabs = p => p.$$eval('.m-tabbar .m-tab-lbl', els => els.map(e => e.textContent.trim()));
const DEFAULT = ['/chat', '/mail', '/calls', '/calendar'];

const b = await browser();
const userTok = await token(args.user, args.pw);
try {
  // ── Tour invite sits above the tab bar; More stays tappable (also with a 34 px home-indicator inset) ──
  if (want('tour')) {
    for (const [w, h, safe] of [[390, 844, 0], [390, 844, 34], [360, 780, 0], [844, 390, 0]]) {
      const ctx = await context(b, { w, h });
      if (safe) await ctx.addInitScript(s => document.addEventListener('DOMContentLoaded', () => document.documentElement.style.setProperty('--m-safe-b', s + 'px')), safe);
      // The invite only shows for modules this account has not seen: clear them through the admin reset endpoint.
      const at = await token(args.admin, args['admin-pw']);
      await api(at, 'POST', `/api/admin/users/${args.user}/reset-tutorials`);
      const p = await ctx.newPage();
      await p.goto(BASE + '/login');
      await p.getByLabel('Employee ID', { exact: true }).fill(args.user);
      await p.getByLabel('Password', { exact: true }).fill(args.pw);
      await p.getByRole('button', { name: 'Sign in', exact: true }).click();
      await p.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 60000 });
      await p.goto(BASE + '/chat'); await settle(p, 2500);
      const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }
      await p.waitForSelector('.tut-invite', { timeout: 15000 }).catch(() => null);
      const geo = await p.evaluate(() => {
        const inv = document.querySelector('.tut-invite'), bar = document.querySelector('.m-tabbar');
        if (!inv || !bar) return { invite: !!inv, bar: !!bar };
        const more = [...bar.querySelectorAll('button.m-tab')].pop(), mr = more.getBoundingClientRect();
        const hit = document.elementFromPoint(mr.left + mr.width / 2, mr.top + mr.height / 2);
        return { invite: true, inviteBottom: Math.round(inv.getBoundingClientRect().bottom), barTop: Math.round(bar.getBoundingClientRect().top), moreHit: more.contains(hit), inviteTop: Math.round(inv.getBoundingClientRect().top) };
      });
      await shot(p, `tour-${w}x${h}-safe${safe}`);
      ok(`tour.${w}x${h}.safe${safe}`, geo.invite && geo.inviteBottom <= geo.barTop && geo.moreHit && geo.inviteTop >= 0, geo);
      await dismissTour(p);
      await ctx.close();
    }
  }

  // ── Edit tabs: persists across reload, across a second browser context, and on the API ──
  if (want('tabs')) {
    await api(userTok, 'PUT', '/api/me/ui-prefs/tabs', DEFAULT);
    const c1 = await context(b, { w: 390, h: 844 });
    const p = await login(c1, BASE, args.user, args.pw);
    const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }
    await open(p, BASE + '/chat');
    const before = await tabs(p);
    await p.locator('.m-tabbar button.m-tab').click(); await sleep(600);
    await p.locator('.ph-more-edit').click(); await sleep(700);
    await shot(p, 'tabs-edit-open');
    await p.getByRole('button', { name: 'Unpin Calls & Meetings' }).click().catch(async () => p.locator('.ph-rm').nth(2).click());
    await p.getByRole('button', { name: 'Pin Notes' }).click();
    await p.getByRole('button', { name: 'Move Notes up' }).click();
    await shot(p, 'tabs-edit-changed');
    await p.locator('.ph-done').click(); await sleep(1200);
    const after = await tabs(p);
    await p.reload(); await settle(p); await dismissTour(p);
    const reloaded = await tabs(p);
    const apiTabs = (await api(userTok, 'GET', '/api/me/ui-prefs/tabs')).body;
    const c2 = await context(b, { w: 360, h: 780, dark: true });
    const p2 = await login(c2, BASE, args.user, args.pw);
    const other = await tabs(p2);
    await shot(p2, 'tabs-second-context');
    // Back closes Edit tabs first, then the More sheet, and stays in the app.
    await p.locator('.m-tabbar button.m-tab').click(); await sleep(600);
    await p.locator('.ph-more-edit').click(); await sleep(700);
    const u0 = p.url();
    await p.goBack(); await p.waitForFunction(() => !document.querySelector('.ph-edit-tabs'), null, { timeout: 5000 }).catch(() => null); await sleep(300);
    const afterBack1 = await p.evaluate(() => ({ edit: !!document.querySelector('.ph-edit-tabs.ph-sheet, .ph-edit-tabs .ph-sheet, .ph-sheet.ph-edit-tabs'), more: !!document.querySelector('.ph-more') }));
    await p.goBack(); await p.waitForFunction(() => !document.querySelector('.ph-more'), null, { timeout: 5000 }).catch(() => null); await sleep(300);
    const afterBack2 = await p.evaluate(() => ({ edit: !!document.querySelector('.ph-edit-tabs'), more: !!document.querySelector('.ph-more'), url: location.href }));
    ok('tabs.persist', before.join() === 'Chat,Mail,Calls,Calendar,More' && after.join() === reloaded.join() && reloaded.join() === other.join() && !reloaded.includes('Calls') && reloaded.includes('Notes'),
      { before, after, reloaded, other, apiTabs });
    ok('tabs.back', !afterBack1.edit && afterBack1.more && !afterBack2.more && afterBack2.url.startsWith(BASE), { u0, afterBack1, afterBack2 });
    // Reset to default through the sheet.
    if (await p.locator('.ph-more').count()) { await p.goBack(); await sleep(800); }
    await p.locator('.m-tabbar button.m-tab').click(); await sleep(600);
    await p.locator('.ph-more-edit').click(); await sleep(700);
    await p.locator('.ph-reset').click(); await p.locator('.ph-done').click(); await sleep(1000);
    ok('tabs.reset', (await tabs(p)).join() === 'Chat,Mail,Calls,Calendar,More', { tabs: await tabs(p), api: (await api(userTok, 'GET', '/api/me/ui-prefs/tabs')).body });
    // API validation
    const bad = await api(userTok, 'PUT', '/api/me/ui-prefs/tabs', ['/chat', '/chat', '/mail', '/calls']);
    const adminTab = await api(userTok, 'PUT', '/api/me/ui-prefs/tabs', ['/chat', '/mail', '/calls', '/admin']);
    ok('tabs.api-validation', bad.status === 400 && adminTab.status === 400, { dup: bad.status, adminForUser: adminTab.status });
    await c1.close(); await c2.close();
  }

  // ── More sheet: every unpinned module, Admin only for admins, never the NIK ──
  if (want('more')) {
    for (const [who, nik, pw] of [['user', args.user, args.pw], ['admin', args.admin, args['admin-pw']]]) for (const dark of [false, true]) {
      const ctx = await context(b, { w: 390, h: 844, dark });
      const p = await login(ctx, BASE, nik, pw);
      const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }
      await open(p, BASE + '/mail');
      await p.locator('.m-tabbar button.m-tab').click(); await sleep(700);
      const info = await p.evaluate(nik => {
        const s = document.querySelector('.ph-more'); const t = s ? s.innerText : '';
        return { open: !!s, tiles: [...(s?.querySelectorAll('.ph-mgrid a, .ph-mgrid button') || [])].map(e => e.innerText.trim()), admin: /Admin/.test(t), nik: nik.length >= 4 && /\d/.test(nik) && t.includes(nik), rows: [...(s?.querySelectorAll('.ph-row') || [])].map(e => e.innerText.trim()) };
      }, nik);
      await shot(p, `more-${who}${dark ? '-dark' : ''}`);
      ok(`more.${who}${dark ? '.dark' : ''}`, info.open && !info.nik && info.admin === (who === 'admin') && info.tiles.includes('Dashboard') && !info.tiles.includes('Chat'), info);
      await ctx.close();
    }
  }

  // ── Notification card: shown once per account (prompt-able browser, server push on) ──
  if (want('notif')) {
    await api(userTok, 'PUT', '/api/me/ui-prefs/notif-card', '');
    const del = await api(userTok, 'GET', '/api/me/ui-prefs/notif-card');
    const ctx = await context(b, { w: 390, h: 844, pushPrompt: true });
    const p = await login(ctx, BASE, args.user, args.pw);
    await sleep(1500);
    const first = await p.evaluate(() => ({ card: !!document.querySelector('.ph-npc'), banner: !!document.querySelector('.push-banner'), perm: typeof Notification !== 'undefined' ? Notification.permission : 'n/a', secure: isSecureContext }));
    await shot(p, 'notif-card');
    if (first.card) { await p.getByRole('button', { name: 'Not now' }).click(); await sleep(800); }
    await p.reload(); await settle(p);
    const again = await p.evaluate(() => !!document.querySelector('.ph-npc'));
    const c2 = await context(b, { w: 430, h: 932, pushPrompt: true });
    const p2 = await login(c2, BASE, args.user, args.pw); await sleep(1500);
    const other = await p2.evaluate(() => !!document.querySelector('.ph-npc'));
    const desk = await context(b, { w: 1440, h: 900, mobile: false, pushPrompt: true });
    const pd = await login(desk, BASE, args.user, args.pw); await sleep(1500);
    const deskCard = await pd.evaluate(() => !!document.querySelector('.ph-npc'));
    ok('notif.once', first.card && !first.banner && !again && !other && !deskCard, { first, again, other, deskCard, pref: (await api(userTok, 'GET', '/api/me/ui-prefs/notif-card')).body, cleared: del.body });
    await ctx.close(); await c2.close(); await desk.close();
  }

  // ── Top safe area: nothing interactive under a 47 px notch ──
  if (want('safe')) {
    for (const standalone of [false, true]) {
      const ctx = await context(b, { w: 390, h: 844 });
      const p = await login(ctx, BASE, args.user, args.pw);
      if (standalone) { const cdp = await ctx.newCDPSession(p); await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'display-mode', value: 'standalone' }] }); }
      const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }
      await p.evaluate(() => document.documentElement.style.setProperty('--m-safe-t', '47px'));
      await open(p, BASE + '/chat'); await p.evaluate(() => document.documentElement.style.setProperty('--m-safe-t', '47px')); await sleep(400);
      const under = async () => p.evaluate(() => [...document.querySelectorAll('a[href],button,[role=button],input,h1,.topbar-title,.page-title')].filter(e => { const r = e.getBoundingClientRect(), s = getComputedStyle(e); return r.width && r.height && s.visibility !== 'hidden' && s.display !== 'none' && r.top < 47 && r.bottom > 0; }).map(e => e.tagName + '.' + [...e.classList].join('.')).slice(0, 10));
      const top = await under();
      await shot(p, `safe-top${standalone ? '-standalone' : ''}`);
      await p.locator('.m-tabbar button.m-tab').click(); await sleep(700);
      const sheetTop = await p.evaluate(() => { const s = document.querySelector('.ph-sheet'); return s ? Math.round(s.getBoundingClientRect().top) : null; });
      ok(`safe.top${standalone ? '.standalone' : ''}`, top.length === 0 && (sheetTop == null || sheetTop >= 47), { under: top, sheetTop });
      await ctx.close();
    }
  }

  // ── Manifest + installability (CDP) ──
  if (want('install')) {
    for (const [w, h, mobile] of [[390, 844, true], [1440, 900, false]]) {
      // Ephemeral Playwright contexts report "in-incognito"; installability needs a real profile.
      const dir = fs.mkdtempSync(path.join(OUT, 'profile-'));
      const ctx = await chromium.launchPersistentContext(dir, { executablePath: fs.existsSync(CHROME) ? CHROME : undefined, headless: true, viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, userAgent: mobile ? ANDROID_UA : undefined });
      const p = ctx.pages()[0] || await ctx.newPage(); await p.goto(BASE + '/login'); await settle(p, 4000);
      const cdp = await ctx.newCDPSession(p);
      const errs = (await cdp.send('Page.getInstallabilityErrors')).installabilityErrors;
      const man = await cdp.send('Page.getAppManifest');
      const m = JSON.parse(man.data || '{}');
      const theme = await p.$$eval('meta[name=theme-color]', els => els.map(e => [e.content, e.media]));
      ok(`install.${w}`, errs.length === 0 && m.display === 'standalone' && m.id === '/' && (man.errors || []).length === 0, { errs, manifestErrors: man.errors, url: man.url, display: m.display, start: m.start_url, theme });
      await ctx.close(); fs.rmSync(dir, { recursive: true, force: true });
    }
  }

  // ── Service worker: registered, offline reload of the shell, offline page for uncached routes ──
  if (want('sw')) {
    const ctx = await context(b, { w: 390, h: 844 });
    const p = await login(ctx, BASE, args.user, args.pw);
    await open(p, BASE + '/chat');
    const reg = await p.evaluate(async () => { const r = await navigator.serviceWorker.ready; return { scope: r.scope, script: r.active?.scriptURL }; });
    await p.reload(); await settle(p, 2500);
    const ctl = await p.evaluate(async () => ({ controller: navigator.serviceWorker.controller?.scriptURL || null, caches: await caches.keys(), regs: (await navigator.serviceWorker.getRegistrations()).length }));
    const fwCount = await p.evaluate(async () => { const k = (await caches.keys()).find(k => k.startsWith('ipop-fw-')); return k ? (await (await caches.open(k)).keys()).length : 0; });
    ok('sw.registered', ctl.controller && ctl.regs === 1 && ctl.caches.some(k => k.startsWith('ipop-shell-')) && fwCount > 10, { reg, ...ctl, fwCount });
    await ctx.setOffline(true);
    await p.reload({ waitUntil: 'domcontentloaded' }).catch(e => null); await sleep(9000);
    const off = await p.evaluate(() => ({ title: document.title, booted: !document.querySelector('#app > .boot') && !!document.querySelector('#app *'), text: document.body.innerText.slice(0, 160), offlinePage: /offline/i.test(document.body.innerText) }));
    await shot(p, 'sw-offline-reload');
    ok('sw.offline-shell', off.booted || off.offlinePage, off);
    const p3 = await ctx.newPage();
    await p3.goto(BASE + '/files/never-visited?x=' + Date.now()).catch(() => null); await sleep(6000);
    const off2 = await p3.evaluate(() => ({ text: document.body.innerText.slice(0, 200) }));
    await shot(p3, 'sw-offline-new-tab');
    ok('sw.offline-nav', !/ERR_INTERNET_DISCONNECTED|This site can.t be reached/i.test(off2.text), off2);
    await ctx.setOffline(false);
    await ctx.close();
  }

  // ── /app: Android / iPhone / desktop QR, logged out and logged in ──
  if (want('app')) {
    for (const [name, opt] of [['android', { w: 390, h: 844, ua: ANDROID_UA }], ['iphone', { w: 390, h: 844, ua: IPHONE_UA }], ['desktop', { w: 1440, h: 900, mobile: false }]]) for (const signedIn of [false, true]) {
      const ctx = await context(b, opt);
      const p = signedIn ? await login(ctx, BASE, args.user, args.pw) : await ctx.newPage();
      await p.goto(BASE + '/app'); await settle(p, 2500);
      const info = await p.evaluate(() => ({ url: location.pathname, cls: document.querySelector('.ph-app')?.className, qr: !!document.querySelector('.ph-qr svg'), apk: !!document.querySelector('.ph-dl'), soon: !!document.querySelector('.ph-soon'), steps: document.querySelectorAll('.ph-step').length, seg: [...document.querySelectorAll('[role=tab][aria-selected="true"]')].map(e => e.textContent.trim()), text: document.body.innerText.slice(0, 120), shell: !!document.querySelector('.shell .sidebar') }));
      await shot(p, `app-${name}-${signedIn ? 'in' : 'out'}`);
      const pass = info.url === '/app' && (name === 'desktop' ? info.qr : name === 'android' ? (info.soon || info.apk) && info.seg.includes('Android') : info.seg.includes('iPhone') && info.steps > 0);
      ok(`app.${name}.${signedIn ? 'in' : 'out'}`, pass, info);
      await ctx.close();
    }
  }

  // ── A new web build is picked up (no stale app behind the service worker) ──
  if (want('newbuild') && args.swap) {
    const ctx = await context(b, { w: 390, h: 844 });
    const p = await login(ctx, BASE, args.user, args.pw);
    await open(p, BASE + '/chat'); await p.reload(); await settle(p, 2500);
    const wasm = () => p.evaluate(() => performance.getEntriesByType('resource').map(e => e.name).filter(n => /IPop\.Web\.[a-z0-9]+\.wasm/.test(n)).map(n => n.match(/IPop\.Web\.([a-z0-9]+)\.wasm/)[1]));
    const before = await wasm();
    // The swap starts a detached web host; ignore stdio so its inherited handles do not keep us waiting.
    await new Promise((res, rej) => spawn(args.swap, { shell: true, stdio: 'ignore', windowsHide: true }).on('exit', c => c === 0 ? res() : rej(new Error('swap exit ' + c))));
    await sleep(6000);
    // version-check.js runs on focus / visibility; trigger it the way a user returning to the tab would.
    await p.evaluate(() => { dispatchEvent(new Event('focus')); }); await sleep(3000);
    await p.evaluate(() => { dispatchEvent(new Event('focus')); }); await sleep(3000);
    const bar = await p.evaluate(() => !!document.querySelector('.ipop-update-bar'));
    if (bar) await p.locator('.ipop-update-bar button').click(); else await p.reload();
    await settle(p, 4000);
    const after = await wasm();
    const p2 = await ctx.newPage(); await p2.goto(BASE + '/mail'); await settle(p2, 4000);
    const fresh = await p2.evaluate(() => performance.getEntriesByType('resource').map(e => e.name).filter(n => /IPop\.Web\.[a-z0-9]+\.wasm/.test(n)).map(n => n.match(/IPop\.Web\.([a-z0-9]+)\.wasm/)[1]));
    ok('sw.new-build', before.length && after.length && after[0] !== before[0] && fresh[0] === after[0] && after[0] === args.expect, { before, after, fresh, updateBar: bar, expect: args.expect });
    await ctx.close();
  }
} finally { save(); await b.close(); }
const fails = Object.entries(R).filter(([, v]) => !v.pass).map(([k]) => k);
console.log(JSON.stringify({ total: Object.keys(R).length, fails }));
