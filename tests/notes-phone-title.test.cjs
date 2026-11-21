// The phone "inputs are 16px !important" rule (stops iOS zooming) must not shrink the Notes page title:
// the notes rule has to out-specify it, or the title drops to body size on phones (happened twice).
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const css = f => fs.readFileSync(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'css', 'mobile', f), 'utf8');
// specificity (ids, classes/attributes/pseudo-classes, elements) of one selector, enough for these rules
function spec(sel) {
  let a = 0, b = 0, c = 0;
  const notArgs = [];
  sel = sel.replace(/:not\(([^)]*)\)/g, (_, inner) => { notArgs.push(inner); return ''; });
  for (const inner of notArgs) { const s = spec(inner); a += s[0]; b += s[1]; c += s[2]; }
  a += (sel.match(/#[\w-]+/g) || []).length;
  b += (sel.match(/\.[\w-]+|\[[^\]]+\]|:(?!:)[\w-]+/g) || []).length;
  c += (sel.replace(/\.[\w-]+|\[[^\]]+\]|#[\w-]+|:[\w-]+/g, ' ').match(/(^|[\s>+~])[a-z][\w-]*/gi) || []).length;
  return [a, b, c];
}
const higher = (x, y) => x[0] !== y[0] ? x[0] > y[0] : x[1] !== y[1] ? x[1] > y[1] : x[2] > y[2];

test('the phone page title rule beats the phone-wide 16px input rule', () => {
  const comp = css('components.css'), notes = css('notes.css');
  const inputRule = /(input:not\(\[type="checkbox"\]\)[^,{]*)/.exec(comp);
  assert.ok(inputRule, 'found the phone-wide input rule');
  const titleRule = /([^{}\n]*nt-page-title[^{}\n]*)\{[^}]*font-size:\s*30px\s*!important/.exec(notes);
  assert.ok(titleRule, 'the notes phone title rule is 30px !important');
  const s1 = spec(inputRule[1].trim()), s2 = spec(titleRule[1].trim());
  assert.ok(higher(s2, s1), `title rule ${s2} must out-specify the input rule ${s1}`);
});
