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
test('toolbar only shows for an editor focus and a keyboard taller than 120px', () => {
  assert.equal(typeof kb.shouldShow, 'function');
  assert.equal(kb.shouldShow(true, 121), true);
  assert.equal(kb.shouldShow(true, 120), false);
  assert.equal(kb.shouldShow(false, 300), false);
});
