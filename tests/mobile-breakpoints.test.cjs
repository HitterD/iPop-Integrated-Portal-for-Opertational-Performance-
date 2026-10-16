// node --test tests/mobile-breakpoints.test.cjs — one phone breakpoint for the whole app (mobile Stage 1 hotfix).
// Phone layout = narrower than 768 px, or a short touch landscape phone (≤ 500 px tall, ≤ 932 px wide, coarse pointer).
// 768–1024 is a compact desktop (sidebar visible). Every stylesheet and script must agree with ipop.mobile.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot');
const M = require(path.join(WEB, 'js', 'mobile.js'));

// A tiny evaluator for the media-query subset we use: comma = OR, "and" = AND, (max|min-width|height: Npx), (pointer: x).
function matches(query, env) {
  return query.split(',').some(part => part.trim().split(/\s+and\s+/).every(f => {
    const m = f.trim().match(/^\((min|max)-(width|height):\s*(\d+)px\)$/);
    if (m) { const v = env[m[2]]; return m[1] === 'min' ? v >= +m[3] : v <= +m[3]; }
    const p = f.trim().match(/^\(pointer:\s*(\w+)\)$/);
    if (p) return env.pointer === p[1];
    throw new Error('unsupported feature ' + f);
  }));
}
const env = (width, height, pointer) => ({ width, height, pointer });

test('phone query: portrait phones and short touch landscape phones', () => {
  for (const [w, h] of [[360, 780], [390, 844], [430, 932], [767, 1024]]) assert.equal(matches(M.PHONE_QUERY, env(w, h, 'coarse')), true, `${w}x${h}`);
  for (const [w, h] of [[844, 390], [932, 430], [740, 360]]) assert.equal(matches(M.PHONE_QUERY, env(w, h, 'coarse')), true, `${w}x${h} landscape`);
});

test('tablets and desktops are not phones (even a short desktop window with a mouse)', () => {
  for (const [w, h, p] of [[768, 1024, 'coarse'], [1024, 768, 'coarse'], [1440, 900, 'fine'], [844, 390, 'fine'], [1280, 480, 'fine'], [1024, 480, 'coarse']])
    assert.equal(matches(M.PHONE_QUERY, env(w, h, p)), false, `${w}x${h} ${p}`);
});

test('desktop query is the exact complement of the phone query', () => {
  for (const w of [320, 600, 767, 768, 800, 932, 933, 1024, 1440]) for (const h of [360, 480, 500, 501, 768, 1000]) for (const p of ['coarse', 'fine', 'none'])
    assert.notEqual(matches(M.PHONE_QUERY, env(w, h, p)), matches(M.DESKTOP_QUERY, env(w, h, p)), `${w}x${h} ${p}`);
});

function walk(dir, ext, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'lib') walk(f, ext, out); }
    else if (f.endsWith(ext) && !f.endsWith('.min.js')) out.push(f);
  }
  return out;
}

test('no stylesheet keeps a stray 767/768/769 px phone breakpoint', () => {
  const bad = [];
  for (const f of walk(path.join(WEB, 'css'), '.css')) {
    const css = fs.readFileSync(f, 'utf8');
    for (const m of css.matchAll(/@media([^{]*)\{/g)) {
      const q = m[1].trim().replace(/\s+/g, ' ');
      if (!/\b76[789]px/.test(q)) continue;
      if (q === M.PHONE_QUERY || q === M.DESKTOP_QUERY) continue;
      bad.push(path.relative(WEB, f) + ': ' + q);
    }
  }
  assert.deepEqual(bad, []);
});

test('scripts ask ipop.mobile (or use the same query) instead of their own 767/768 px check', () => {
  const bad = [];
  for (const f of walk(path.join(WEB, 'js'), '.js')) {
    if (f.endsWith(path.join('js', 'mobile.js'))) continue;
    const js = fs.readFileSync(f, 'utf8');
    for (const m of js.matchAll(/matchMedia\(\s*(['"])([^'"]*76[789]px[^'"]*)\1/g)) bad.push(path.relative(WEB, f) + ': ' + m[2]);
  }
  assert.deepEqual(bad, []);
});

// Wave 1 guard: outside the phone query, css/mobile/*.css may only hide phone-only chrome.
// Anything else there would move desktop/tablet pixels.
function topLevelRules(css) {
  const out = [];
  css = css.replace(/\/\*[\s\S]*?\*\//g, '');
  let i = 0;
  while (i < css.length) {
    const open = css.indexOf('{', i);
    if (open < 0) break;
    const head = css.slice(i, open).trim();
    let depth = 1, j = open + 1;
    while (j < css.length && depth) { if (css[j] === '{') depth++; else if (css[j] === '}') depth--; j++; }
    out.push({ head, body: css.slice(open + 1, j - 1).trim() });
    i = j;
  }
  return out;
}

test('mobile stylesheets only add display:none guards outside the phone query', () => {
  const bad = [];
  for (const f of walk(path.join(WEB, 'css', 'mobile'), '.css')) {
    for (const r of topLevelRules(fs.readFileSync(f, 'utf8'))) {
      if (r.head.startsWith('@media')) {
        const q = r.head.slice(6).trim().replace(/\s+/g, ' ');
        if (q !== M.PHONE_QUERY) bad.push(path.basename(f) + ': @media ' + q);
        continue;
      }
      const decls = r.body.split(';').map(d => d.trim()).filter(Boolean);
      if (!(decls.length === 1 && /^display:\s*none$/.test(decls[0]))) bad.push(path.basename(f) + ': ' + r.head + ' { ' + r.body + ' }');
    }
  }
  assert.deepEqual(bad, []);
});

test('phone-kit.css is linked after the stage-1 mobile layer', () => {
  const html = fs.readFileSync(path.join(WEB, 'index.html'), 'utf8');
  const kit = html.indexOf('css/mobile/phone-kit.css');
  assert.ok(kit > html.indexOf('css/mobile/login.css'), 'phone-kit.css linked after the mobile block');
});
