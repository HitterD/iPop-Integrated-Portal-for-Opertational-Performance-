// node --test tests/phone-kit-scope.test.cjs — the phone kit's .ph-* rules must not restyle other modules.
// Projects already uses .ph-btn / .ph-av (ProjectHeader) — any kit rule for a class that markup outside the kit
// also uses has to be scoped under a kit container (e.g. `.ph-npc .ph-btn`), never bare.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const SRC = path.join(__dirname, '..', 'src', 'IPop.Web');
const KIT_OWNERS = [path.join('Components', 'Phone'), path.join('Pages', 'AppInstall.razor'), path.join('Layout', 'MainLayout.razor')];

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const f = path.join(dir, e.name);
    if (e.isDirectory()) { if (!['bin', 'obj', 'wwwroot'].includes(e.name)) walk(f, out); }
    else if (f.endsWith('.razor')) out.push(f);
  }
  return out;
}

/** ph-* classes used by markup that is not part of the phone kit. */
function foreignPhClasses() {
  const used = new Map();
  for (const f of walk(SRC)) {
    const rel = path.relative(SRC, f);
    if (KIT_OWNERS.some(o => rel.startsWith(o))) continue;
    const src = fs.readFileSync(f, 'utf8');
    for (const m of src.matchAll(/class="([^"]*)"|Class="([^"]*)"/g))
      for (const c of (m[1] || m[2]).split(/\s+/)) if (/^ph-[\w-]+$/.test(c)) used.set(c, rel);
  }
  return used;
}

test('kit rules for classes shared with other modules are scoped under a kit container', () => {
  const css = fs.readFileSync(path.join(SRC, 'wwwroot', 'css', 'mobile', 'phone-kit.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const foreign = foreignPhClasses();
  const bad = [];
  for (const m of css.matchAll(/([^{}]+)\{/g)) {
    const prelude = m[1].trim();
    if (prelude.startsWith('@')) continue;
    for (const sel of prelude.split(',').map(s => s.trim())) {
      const first = sel.split(/\s+|>|\+|~/).filter(Boolean)[0] || '';
      for (const cm of first.matchAll(/\.(ph-[\w-]+)/g))
        if (foreign.has(cm[1])) bad.push(`${sel}  (also used by ${foreign.get(cm[1])})`);
    }
  }
  assert.deepEqual(bad, []);
});
