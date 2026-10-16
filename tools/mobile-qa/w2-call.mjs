// Wave 2: two-browser 1:1 voice call on phone viewports (C1 full screen, C1b pill on another route).
//   node tools/mobile-qa/w2-call.mjs --base http://127.0.0.1:5986 --a ipopadmin --a-pw .. --b 0001 --b-pw .. --room <dm id of a+b>
// Fake media; only /api/calls* and chat mutate the copy DB.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { context, login, sleep, guardBase, out, open } from './lib.mjs';

const args = Object.fromEntries(process.argv.slice(2).reduce((a, v, i, all) => (v.startsWith('--') ? a.concat([[v.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]]) : a), []));
const BASE = args.base; guardBase(BASE);
const OUT = out(path.join(path.dirname(fileURLToPath(import.meta.url)), 'out', 'w2-call'));
const R = {}; const ok = (k, pass, info) => { R[k] = { pass: !!pass, ...(info || {}) }; console.log((pass ? 'PASS ' : 'FAIL ') + k + (info ? ' ' + JSON.stringify(info).slice(0, 240) : '')); };
const exe = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const launch = () => chromium.launch({ executablePath: fs.existsSync(exe) ? exe : undefined, headless: true,
  args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', '--autoplay-policy=no-user-gesture-required'] });
const ba = await launch(), bb = await launch();
try {
  const ca = await context(ba, { w: 390, h: 844 }), cb = await context(bb, { w: 390, h: 844 });
  for (const c of [ca, cb]) await c.grantPermissions(['microphone'], { origin: BASE });
  const pa = await login(ca, BASE, args.a, args['a-pw']);
  const pb = await login(cb, BASE, args.b, args['b-pw']);
  for (const p of [pa, pb]) { const n = p.locator('.ph-npc .ph-btn').last(); if (await n.count()) await n.click(); }
  // B sits on /chat; A rings B from the Calls page picker.
  await open(pb, BASE + '/chat', 1500);
  // A opens the shared DM (--room) and rings from the conversation header, like a phone user.
  await open(pa, BASE + '/chat/' + args.room, 2500);
  await pa.locator('.conv-header .header-icon-btn[aria-label="Start call"]').click(); await sleep(2500);
  ok('ring.outgoingFull', await pa.locator('.callx-window.m-call-full').isVisible());
  await pa.screenshot({ path: path.join(OUT, 'a-ringing.png') });
  const ringing = await pb.locator('.callx-incoming').isVisible({ timeout: 8000 }).catch(() => false);
  ok('ring.incoming', ringing);
  await pb.screenshot({ path: path.join(OUT, 'b-incoming.png') });
  if (ringing) {
    await pb.locator('.callx-incoming .callx-accept').click(); await sleep(6000);
    const st = await pa.evaluate(() => document.querySelector('.callx-window .callx-badge')?.className);
    ok('call.connected', /\bok\b/.test(st || '') , { badge: st });
    ok('call.fullScreenB', await pb.locator('.callx-window.m-call-full').isVisible());
    await pb.screenshot({ path: path.join(OUT, 'b-in-call.png') });
    // C1b: minimise, move to Mail, pill is still there with mute + end
    await pb.locator('button[aria-label="Minimise call"]').click(); await sleep(600);
    await pb.locator('.m-tabbar a[href="/mail"], .m-tabbar [href="/mail"]').first().click().catch(() => pb.goto(BASE + '/mail')); await sleep(2500);
    const pill = pb.locator('.callx-pill.m-call-pill');
    ok('pill.onOtherRoute', await pill.isVisible() && new URL(pb.url()).pathname === '/mail');
    const pb1 = await pill.boundingBox();
    ok('pill.topAndTargets', pb1 && pb1.y < 80 && (await pill.locator('.callx-round').evaluateAll(els => els.every(e => e.getBoundingClientRect().width >= 44))), { box: pb1 });
    await pb.screenshot({ path: path.join(OUT, 'b-pill-mail.png') });
    await pill.locator('.callx-round').first().click(); await sleep(800);
    ok('pill.mute', await pill.locator('.callx-round.on').count() === 1);
    await pill.locator('.callx-pill-open').click(); await sleep(600);
    ok('pill.returns', await pb.locator('.callx-window.m-call-full').isVisible());
    await pb.locator('.callx-ctrl.hangup').click(); await sleep(2500);
    ok('call.ended', !(await pa.locator('.callx-window').count()) && !(await pb.locator('.callx-window').count()));
  } else {
    await pa.locator('.callx-ctrl.hangup').click().catch(() => {});
  }
} finally {
  fs.writeFileSync(path.join(OUT, 'call.json'), JSON.stringify(R, null, 2));
  await ba.close(); await bb.close();
}
