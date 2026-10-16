// Shared helpers for the mobile QA harness (plan: shared QA protocol). Never point at prod (5103/7103/5083).
import { chromium } from 'playwright';
import fs from 'node:fs';

export const sleep = ms => new Promise(r => setTimeout(r, ms));
export const ANDROID_UA = 'Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36';
export const IPHONE_UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1';

export function guardBase(base) {
  if (/:(5103|7103|5083)\b/.test(base) || /ipop\.santos\.co\.id/.test(base)) throw new Error('refusing to QA against prod: ' + base);
}

export async function browser() {
  const exe = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
  return chromium.launch({ executablePath: fs.existsSync(exe) ? exe : undefined, headless: true });
}

/** Phone contexts are coarse/touch; desktop/tablet-mouse are fine. */
export async function context(b, { w, h, dark = false, mobile = true, ua, pushPrompt = false } = {}) {
  const ctx = await b.newContext({
    viewport: { width: w, height: h }, isMobile: mobile, hasTouch: mobile, deviceScaleFactor: mobile ? 2 : 1,
    userAgent: ua || (mobile ? ANDROID_UA : undefined), colorScheme: dark ? 'dark' : 'light',
    reducedMotion: 'reduce',
  });
  await ctx.addInitScript(d => { try { localStorage.setItem('ipop-theme', d ? 'dark' : 'light'); } catch { } }, dark);
  if (pushPrompt) {
    // VAPID is off in the copy; answer like prod so the notification card can appear.
    await ctx.route('**/api/push/config', r => r.fulfill({ json: { enabled: true, publicKey: 'BEl62iUYgUivxIkv69yViEuiBIa-Ib9-SkvMeAtA3LFgDzkrxZJjSgSnfckjBJuBkr3qBUYIHBQFLXYp5Nksh8U' } }));
  }
  return ctx;
}

export async function login(ctx, base, nik, pw) {
  const p = await ctx.newPage();
  p.errors = [];
  p.on('pageerror', e => p.errors.push('pageerror: ' + e.message.slice(0, 200)));
  p.on('console', m => { if (m.type() === 'error' && !/Failed to load resource|ERR_|favicon/.test(m.text())) p.errors.push('console: ' + m.text().slice(0, 200)); });
  await p.goto(base + '/login');
  await p.getByLabel('Employee ID', { exact: true }).fill(nik);
  await p.getByLabel('Password', { exact: true }).fill(pw);
  await p.getByRole('button', { name: 'Sign in', exact: true }).click();
  await p.waitForURL(u => !u.pathname.startsWith('/login'), { timeout: 60000 });
  await settle(p);
  await dismissTour(p);
  return p;
}

/** Close the first-visit tutorial invite (one per module) so it never sits over what a check looks at. */
export async function dismissTour(p) {
  for (let i = 0; i < 3; i++) {
    const x = p.locator('.tut-invite .tut-x');
    if (!(await x.count())) return;
    try { await x.first().click({ timeout: 3000 }); } catch { return; }
    await sleep(400);
  }
}

/** goto + settle + dismiss the tour invite. */
export async function open(p, url, ms) {
  await p.goto(url); await settle(p, ms); await dismissTour(p);
}

export async function settle(p, ms = 1800) {
  try { await p.waitForLoadState('networkidle', { timeout: 15000 }); } catch { }
  await sleep(ms);
}

/** Overflow + tap-target audit (plan checklist). */
export async function audit(p) {
  return p.evaluate(() => {
    const vw = innerWidth, vh = innerHeight;
    const vis = e => { const s = getComputedStyle(e); if (s.display === 'none' || s.visibility === 'hidden' || +s.opacity === 0) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
    const name = e => { let s = e.tagName.toLowerCase(); const c = [...e.classList].slice(0, 2).join('.'); if (c) s += '.' + c; const t = (e.getAttribute('aria-label') || e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 24); return t ? s + ' "' + t + '"' : s; };
    const clipped = e => { for (let a = e.parentElement; a && a !== document.body; a = a.parentElement) { const s = getComputedStyle(a); if (/(auto|scroll|hidden|clip)/.test(s.overflowX)) { const ar = a.getBoundingClientRect(), r = e.getBoundingClientRect(); if (r.right > ar.right + 1 || r.left < ar.left - 1) return true; } } return false; };
    const all = [...document.querySelectorAll('body *')].filter(vis);
    const over = [];
    for (const e of all) { const r = e.getBoundingClientRect(); if (r.right > vw + 1 && r.width < vw * 3 && !clipped(e)) { const s = getComputedStyle(e); if (s.position === 'fixed' && r.left >= vw) continue; over.push(name(e) + ` →${Math.round(r.right)}`); } }
    const small = [];
    for (const e of document.querySelectorAll('a[href],button,[role=button],input:not([type=hidden]),select,[role=tab]')) {
      if (!vis(e)) continue; const r = e.getBoundingClientRect();
      if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) continue;
      if (r.width < 44 || r.height < 44) small.push(name(e) + ` ${Math.round(r.width)}x${Math.round(r.height)}`);
    }
    return { vw, vh, scrollW: document.scrollingElement.scrollWidth, overflow: document.scrollingElement.scrollWidth > vw, overN: over.length, over: over.slice(0, 12), smallN: small.length, small: small.slice(0, 25) };
  });
}

export function out(dir) { fs.mkdirSync(dir, { recursive: true }); return dir; }
