// node --test tests/phone-page-titles.test.cjs — phone-only pages must set the topbar title,
// or the shell keeps the previous page's title (Settings once showed "Dashboard").
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const PAGES = path.join(__dirname, '..', 'src', 'IPop.Web', 'Pages');

test('Settings sets its own topbar title', () => {
  const src = fs.readFileSync(path.join(PAGES, 'SettingsPhone.razor'), 'utf8');
  assert.match(src, /Layout\?\.SetHeader\("Settings"/);
});
