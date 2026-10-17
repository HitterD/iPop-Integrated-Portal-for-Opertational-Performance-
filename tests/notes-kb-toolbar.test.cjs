const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const file = path.join(__dirname, '../src/IPop.Web/wwwroot/js/notes-kb.js');
const kb = fs.existsSync(file) ? require(file) : {};

test('toolbar offset follows the visible viewport bottom on iOS and resized Android', () => {
  assert.equal(typeof kb.toolbarOffset, 'function');
  assert.equal(kb.toolbarOffset({ offsetTop: 34, height: 430 }, 844), -380);
  assert.equal(kb.toolbarOffset({ offsetTop: 0, height: 544 }, 544), 0);
  assert.equal(kb.toolbarOffset({ offsetTop: 0, height: 900 }, 844), 0);
});
test('unoccluded baseline resets when width changes on rotation, not keyboard resize', () => {
  assert.equal(typeof kb.baselineHeight, 'function');
  const portrait = kb.baselineHeight(null, 390, 844);
  assert.equal(kb.baselineHeight(portrait, 390, 544).height, 844);
  assert.equal(kb.baselineHeight(portrait, 844, 390).height, 390);
  const landscape = kb.baselineHeight(portrait, 844, 390);
  assert.equal(kb.shouldShow(true, landscape.height - 390), false);
  assert.equal(kb.shouldShow(true, landscape.height - 200), true);
  assert.equal(kb.shouldShow(true, landscape.height - 390), false);
});
test('a toolbar attached after Android keyboard resize uses the pre-focus viewport baseline', () => {
  const vm = require('node:vm');
  const listeners = new Map();
  const classes = new Set();
  const editor = { closest: () => null };
  const doc = { activeElement: { closest: () => editor }, addEventListener() {}, removeEventListener() {} };
  const root = {
    innerWidth: 390, innerHeight: 844,
    screen: { orientation: { type: 'portrait-primary' } },
    visualViewport: { height: 844, offsetTop: 0, addEventListener() {}, removeEventListener() {} },
    addEventListener: (event, callback) => listeners.set(event, callback), removeEventListener() {},
    getSelection: () => null, ipop: { mobile: { isMobile: () => true } }
  };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), { window: root, document: doc, WeakMap });
  root.innerHeight = root.visualViewport.height = 544;
  listeners.get('resize')?.();
  const bar = { classList: { toggle: (name, on) => on ? classes.add(name) : classes.delete(name) }, style: {}, addEventListener() {}, removeEventListener() {}, offsetHeight: 48 };
  root.ipop.notesKb.attach(bar);
  assert.ok(classes.has('is-visible'), 'late attach must retain the pre-keyboard baseline');
  // Android rotates while its keyboard is still open: layout height is only 200, not 390.
  root.screen.orientation.type = 'landscape-primary';
  root.innerWidth = 844; root.innerHeight = root.visualViewport.height = 200;
  listeners.get('resize')();
  assert.ok(classes.has('is-visible'), 'rotation with keyboard open retains the new unoccluded orientation height');
  root.innerHeight = root.visualViewport.height = 390;
  listeners.get('resize')();
  assert.equal(classes.has('is-visible'), false, 'closing the landscape keyboard hides toolbar');
  root.ipop.notesKb.detach(bar);
});
test('toolbar only shows for an editor focus and a keyboard taller than 120px', () => {
  assert.equal(typeof kb.shouldShow, 'function');
  assert.equal(kb.shouldShow(true, 121), true);
  assert.equal(kb.shouldShow(true, 120), false);
  assert.equal(kb.shouldShow(false, 300), false);
});
