const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
test('automatic theme resolves OS preference and persists automatic rather than resolved dark', () => {
  const attrs = {}; const store = new Map(); let listener;
  const mq = { matches: true, addEventListener: (_, cb) => listener = cb };
  const document = { documentElement: { setAttribute: (k,v) => attrs[k] = v, getAttribute: k => attrs[k] }, querySelectorAll: () => [], addEventListener() {} };
  const window = { matchMedia: () => mq, addEventListener() {} }; window.window = window;
  const localStorage = { setItem: (k,v) => store.set(k,v), getItem: k => store.get(k) };
  const sandbox = { window, document, localStorage, navigator: {}, console, setTimeout, clearTimeout, matchMedia: window.matchMedia };
  Object.defineProperty(sandbox, 'ipop', { get: () => window.ipop });
  vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname, '../src/IPop.Web/wwwroot/js/ipop.js'), 'utf8'), sandbox);
  window.ipop.setTheme('auto'); assert.equal(attrs['data-theme'], 'dark'); assert.equal(store.get('ipop-theme'), 'auto');
  assert.equal(window.ipop.themePreference(), 'auto');
  mq.matches = false; listener(); assert.equal(attrs['data-theme'], 'light');
});
