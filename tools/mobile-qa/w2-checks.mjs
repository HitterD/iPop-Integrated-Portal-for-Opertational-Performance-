// Wave 2 functional checks (plan: Wave 2 QA). Copy DB + local QA hosts only.
//   node tools/mobile-qa/w2-checks.mjs --base http://127.0.0.1:5986 --user ipopadmin --pw .. [--peer 0001 --peer-pw ..]
//        [--only chat,mail,calls,notif,keyboard,back,landscape] [--w 390 --h 844] [--dark]
// Writes out/w2-checks/checks.json + PNGs. Mutations (a chat message, a reaction) go to the copy DB only.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { browser, context, login, sleep, guardBase, out, open, audit } from './lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? a.concat([[v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]]) : a), []));
const BASE = args.base; guardBase(BASE);
const only = args.only ? String(args.only).split(',') : null;
const want = k => !only || only.includes(k);
const W = +(args.w || 390), H = +(args.h || 844), DARK = !!args.dark;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const OUT = out(args.out || path.join(HERE, 'out', 'w2-checks'));
const R = {}; const save = () => fs.writeFileSync(path.join(OUT, 'checks.json'), JSON.stringify(R, null, 2));
const tag = `${W}x${H}${DARK ? '-dark' : ''}`;
// A phone keyboard covers ~35-45 % of the screen: 300 px portrait, ~170 px in short landscape.
const KB = H > 600 ? 300 : Math.round(H * 0.44);
const ok = (k, pass, info) => { R[`${k}@${tag}`] = { pass: !!pass, ...(info || {}) }; console.log((pass ? 'PASS ' : 'FAIL ') + k + ' ' + tag + (info ? ' ' + JSON.stringify(info).slice(0, 300) : '')); save(); };

/** Long-press like a finger: CDP touch events (pointerType "touch"), held 700 ms. */
async function longPress(p, loc) {
  await loc.evaluate(e => e.scrollIntoView({ block: 'center' })).catch(() => {}); await sleep(300);
  const bb = await loc.boundingBox();
  if (!bb) return false;
  const cdp = await p.context().newCDPSession(p);
  const x = bb.x + Math.min(bb.width / 2, 60), y = bb.y + bb.height / 2;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
  await sleep(700);
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
  return true;
}

/** Simulate the iOS keyboard: shrink the visual viewport by 300 px without changing innerHeight. */
async function fakeKeyboard(p, px) {
  await p.evaluate(px => {
    const vv = window.visualViewport;
    const h = window.innerHeight - px;
    Object.defineProperty(vv, 'height', { configurable: true, get: () => h });
    vv.dispatchEvent(new Event('resize'));
  }, px);
  await sleep(400);
}

async function swipe(p, loc, dx) {
  await loc.evaluate(e => e.scrollIntoView({ block: 'center' })); await sleep(300);
  const bb = await loc.boundingBox();
  const cdp = await p.context().newCDPSession(p);
  const y = bb.y + bb.height / 2, x0 = bb.x + bb.width / 2;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: x0, y }] });
  for (let i = 1; i <= 8; i++) { await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: x0 + dx * i / 8, y }] }); await sleep(16); }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await cdp.detach();
  await sleep(400);
}

const shot = (p, n) => p.screenshot({ path: path.join(OUT, `${n}-${tag}.png`) });
const b = await browser();
try {
  const ctx = await context(b, { w: W, h: H, dark: DARK });
  const p = await login(ctx, BASE, args.user, args.pw);
  const npc = p.locator('.ph-npc .ph-btn').last(); if (await npc.count()) { await npc.click(); await sleep(400); }

  if (want('chat')) {
    await open(p, BASE + '/chat', 2500);
    const a0 = await audit(p);
    ok('chat.list', await p.locator('.m-chat-lt').isVisible() && await p.locator('.ph-fab').isVisible() && a0.overN === 0,
      { small: a0.small.filter(s => !/apple-notif/.test(s)), title: await p.locator('.m-chat-lt').textContent() });
    await shot(p, 'chat-list');
    // Topbar title is hidden on the list (it was cut at 360 px)
    ok('chat.topbarTitleHidden', await p.evaluate(() => getComputedStyle(document.querySelector('.v6-topbar .tb-title h1')).visibility === 'hidden'));
    await p.locator('.room-item').first().click(); await sleep(2000);
    ok('chat.conv', await p.locator('.chat-area.m-detail .composer').isVisible()
      && await p.evaluate(() => getComputedStyle(document.querySelector('.m-tabbar')).display === 'none'));
    await shot(p, 'chat-conv');
    // send text + emoji (copy DB)
    const txt = 'QA w2 ' + Date.now() + ' 👍';
    await p.locator('.composer textarea').fill(txt);
    await p.locator('.v6-send.on').click(); await sleep(1500);
    ok('chat.send', (await p.locator('.messages .msg.sent').last().textContent()).includes(txt.slice(0, 10)));
    // attach sheet
    await p.locator('.v6-plus').click(); await sleep(600);
    const tiles = await p.locator('.m-chat-agrid > *').allTextContents();
    ok('chat.attachSheet', tiles.map(t => t.trim()).join(',').startsWith('Gallery,Camera,Document,Sticker,Event'), { tiles });
    await shot(p, 'chat-attach');
    await p.goBack(); await sleep(600);
    ok('chat.attachBack', !(await p.locator('.m-chat-attach').count()) && /\/chat\/./.test(new URL(p.url()).pathname));
    // long-press actions
    await longPress(p, p.locator('.messages > .msg.sent .bubble').last()); await sleep(700);
    const acts = await p.locator('.m-chat-amenu button').allTextContents();
    ok('chat.longPress', acts.length >= 5 && acts.map(a => a.trim()).includes('Reply'), { acts });
    await shot(p, 'chat-longpress');
    await p.locator('.m-chat-amenu button', { hasText: 'Reply' }).click(); await sleep(600);
    ok('chat.replyFromSheet', await p.locator('.reply-bar').isVisible() && !(await p.locator('.m-chat-acts').count()));
    await p.locator('.reply-bar .close-reply').click().catch(() => {});
    // info (CH1) + back
    await p.locator('.conv-header .header-info').click(); await sleep(1200);
    const info = await p.locator('.m-chat-info').isVisible();
    const infoText = info ? await p.locator('.m-chat-info').innerText() : '';
    ok('chat.info', info && /Call\s*Meeting\s*Search\s*(Mute|Unmute)/.test(infoText.replace(/\n/g, ' ')) && !/\b0\d{4,}\b/.test(infoText), { snippet: infoText.slice(0, 120) });
    await shot(p, 'chat-info');
    await p.goBack(); await sleep(800);
    ok('chat.infoBack', !(await p.locator('.chat-detail-panel.on').count()) && /\/chat\/./.test(new URL(p.url()).pathname));
    // keyboard: composer above the visual viewport bottom
    await p.locator('.composer textarea').focus();
    await fakeKeyboard(p, KB);
    const kb = await p.evaluate(() => { const c = document.querySelector('.composer').getBoundingClientRect(); return { bottom: Math.round(c.bottom), vv: Math.round(visualViewport.height), kb: getComputedStyle(document.documentElement).getPropertyValue('--ph-kb') }; });
    ok('chat.keyboard', kb.bottom <= kb.vv + 1, kb);
    await shot(p, 'chat-keyboard');
    await p.reload(); await sleep(2500);
    await p.goBack(); await sleep(1200);
    ok('chat.backToList', new URL(p.url()).pathname === '/chat' && await p.locator('.m-chat-lt').isVisible());
  }

  if (want('mail')) {
    await open(p, BASE + '/mail', 3000);
    const a = await audit(p);
    ok('mail.inbox', a.overN === 0 && (await p.locator('.mlx-count').textContent()).includes('unread'), { small: a.small.filter(s => !/apple-notif/.test(s)) });
    await shot(p, 'mail-inbox');
    await p.locator('.mlx-fbtn').click(); await sleep(700);
    const boxes = await p.locator('.m-mail-box .ph-row-title').allTextContents();
    ok('mail.mailboxes', boxes.includes('Inbox') && boxes.includes('Trash') && await p.locator('.m-mail-more').isVisible(), { boxes });
    await shot(p, 'mail-mailboxes');
    await p.goBack(); await sleep(700);
    ok('mail.mailboxesBack', !(await p.locator('.m-mail-mbx').count()) && new URL(p.url()).pathname === '/mail');
    await p.locator('.mlr').first().click(); await sleep(2200);
    const rd = await p.evaluate(() => ({ pact: !!document.querySelector('.mlx-pact'), receipts: !!document.querySelector('.mrr, .thr-rl'), qr: !!document.querySelector('.mqr-in') }));
    ok('mail.reading', rd.pact && rd.qr, rd);
    await shot(p, 'mail-read');
    await p.locator('.mqr-in').first().click(); await sleep(800);
    const ta = p.locator('.mqr-e textarea, .mqr textarea').first();
    if (await ta.count()) {
      await ta.focus(); await fakeKeyboard(p, KB);
      const k = await p.evaluate(() => { const t = document.querySelector('.mqr-e textarea, .mqr textarea').getBoundingClientRect(); return { bottom: Math.round(t.bottom), vv: Math.round(visualViewport.height) }; });
      ok('mail.quickReplyKeyboard', k.bottom <= k.vv + 1, k);
      await shot(p, 'mail-quickreply');
      await p.reload(); await sleep(2500);
    } else ok('mail.quickReplyKeyboard', false, { reason: 'no quick reply textarea' });
    await open(p, BASE + '/mail', 2500);
    await p.locator('.mlx-fab').click(); await sleep(1500);
    const comp = await p.evaluate(() => ({ sheet: !!document.querySelector('.mc-sheet'), book: !!document.querySelector('.mc-book') }));
    ok('mail.compose', comp.sheet && comp.book, comp);
    await p.locator('.mc-book').first().click(); await sleep(1200);
    ok('mail.addressBook', await p.locator('[role=dialog]:has-text("Address book"), .ab-dialog, .addr-book').count() > 0);
    await shot(p, 'mail-addressbook');
    await p.keyboard.press('Escape'); await sleep(400);
    await p.goBack(); await sleep(800);
  }

  if (want('calls')) {
    await open(p, BASE + '/calls', 2500);
    const a = await audit(p);
    ok('calls.recents', a.overN === 0, { small: a.small.filter(s => !/apple-notif/.test(s)) });
    await shot(p, 'calls-recents');
    await p.getByRole('tab', { name: /Scheduled/ }).click(); await sleep(1200); await shot(p, 'calls-scheduled');
    await p.getByRole('tab', { name: /Rooms/ }).click(); await sleep(1800);
    ok('calls.rooms', await p.locator('.mrt-room').count() >= 5);
    await shot(p, 'calls-rooms');
    await p.getByRole('tab', { name: /Recents/ }).click(); await sleep(800);
    await p.locator('.cl-pfab').click(); await sleep(500);
    await p.getByRole('menuitem', { name: /room/i }).first().click(); await sleep(1500);
    const bs = await p.evaluate(() => { const s = document.querySelector('.sheet2, .sheet2-scrim'); if (!s) return null; const r = s.getBoundingClientRect(); return { top: Math.round(r.top), bottom: Math.round(r.bottom), vh: innerHeight }; });
    ok('calls.bookingSheet', !!bs && bs.bottom <= bs.vh + 1, bs);
    await shot(p, 'calls-booking');
    await p.goBack(); await sleep(800);
  }

  if (want('notif')) {
    await open(p, BASE + '/notifications', 2500);
    const a = await audit(p);
    ok('notif.list', await p.locator('.m-nf-screen').isVisible() && a.overN === 0, { small: a.small.filter(s => !/apple-notif/.test(s)) });
    await shot(p, 'notif-list');
    await p.locator('.m-nf-chip', { hasText: 'Unread' }).click(); await sleep(500);
    ok('notif.chip', (await p.locator('.m-nf-chip.on').textContent()).includes('Unread'));
    await p.locator('.m-nf-chip', { hasText: 'All' }).first().click(); await sleep(500);
    const row = p.locator('.m-nf-sw').first();
    if (await row.count()) {
      await swipe(p, row.locator('.sw-fg'), -150);
      ok('notif.swipeLeft', await row.evaluate(e => e.classList.contains('sw-left')));
      await shot(p, 'notif-swipe');
      await row.locator('.sw-snz').click(); await sleep(700);
      ok('notif.snoozeSheet', await p.locator('.m-nf-snzsheet').isVisible());
      await shot(p, 'notif-snooze');
      await p.goBack(); await sleep(600);
    }
    await p.locator('.ph-ib[aria-label="Preferences"]').click(); await sleep(900);
    const sw = await p.locator('.m-nf-prefs [role=switch]').count();
    ok('notif.prefs', sw >= 7 && !(await p.locator('.m-nf-prefs :text("Save")').count()), { switches: sw });
    await shot(p, 'notif-prefs');
    await p.goBack(); await sleep(800);
    ok('notif.prefsBack', await p.locator('.m-nf-screen').isVisible());
    // bell → /notifications
    await open(p, BASE + '/chat', 1500);
    await p.locator('.bell-btn').click(); await sleep(1500);
    ok('notif.bellOpensList', new URL(p.url()).pathname === '/notifications');
  }

  await ctx.close();
} finally { save(); await b.close(); }
