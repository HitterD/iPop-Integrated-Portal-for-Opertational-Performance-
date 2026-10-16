// node --test tests/tour-invite-phone.test.cjs — on phone the tutorial invite card sits above the tab bar
// (it used to sit at bottom:12px and covered the More button). Desktop placement stays untouched.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot');
const M = require(path.join(WEB, 'js', 'mobile.js'));
const css = fs.readFileSync(path.join(WEB, 'css', 'v6', 'tutorial.css'), 'utf8');

/** Body of the first @media block whose query is exactly `query` (brace-matched). */
function mediaBody(src, query) {
  for (const m of src.matchAll(/@media([^{]*)\{/g)) {
    if (m[1].trim().replace(/\s+/g, ' ') !== query) continue;
    let depth = 1, i = m.index + m[0].length;
    for (; i < src.length && depth; i++) depth += src[i] === '{' ? 1 : src[i] === '}' ? -1 : 0;
    return src.slice(m.index + m[0].length, i - 1);
  }
  return null;
}
const decl = (body, selector) => {
  const esc = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const m = body.match(new RegExp('(^|[};]\\s*|^\\s*)' + esc + '\\s*\\{([^}]*)\\}', 'm'));
  return m ? m[2] : null;
};

test('desktop invite placement is unchanged (bottom-right card)', () => {
  const base = decl(css.slice(0, css.indexOf('@media')), '.tut-invite');
  assert.match(base, /right:\s*22px/);
  assert.match(base, /bottom:\s*22px/);
});

test('phone invite clears the tab bar and the bottom safe area', () => {
  const body = mediaBody(css, M.PHONE_QUERY);
  assert.ok(body, 'phone media block present');
  const d = decl(body, '.tut-invite');
  assert.ok(d, '.tut-invite rule in phone block');
  const bottom = d.match(/bottom:\s*([^;]+);/)[1];
  assert.match(bottom, /var\(--m-tabbar-h/, 'adds the tab bar height');
  assert.match(bottom, /var\(--m-safe-b/, 'adds the bottom safe area');
});

test('phone invite falls back to the screen edge when the tab bar is hidden', () => {
  const d = decl(mediaBody(css, M.PHONE_QUERY), 'body.m-hide-tabbar .tut-invite');
  assert.ok(d, 'rule for body.m-hide-tabbar');
  assert.doesNotMatch(d.match(/bottom:\s*([^;]+);/)[1], /--m-tabbar-h/);
});
