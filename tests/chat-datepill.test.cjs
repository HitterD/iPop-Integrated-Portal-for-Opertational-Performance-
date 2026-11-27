// node --test tests/chat-datepill.test.cjs — which day the floating date pill shows (wwwroot/js/chat-datepill.js).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const D = require(path.join(__dirname, '..', 'src', 'IPop.Web', 'wwwroot', 'js', 'chat-datepill.js'));

const seps = [{ y: 0, label: 'Mon' }, { y: 800, label: 'Yesterday' }, { y: 1600, label: 'Today' }];

test('shows the day whose separator is at or above the top of the viewport', () => {
  assert.equal(D.currentLabel(seps, 10), 'Mon');
  assert.equal(D.currentLabel(seps, 799), 'Mon');
  assert.equal(D.currentLabel(seps, 800), 'Yesterday');
  assert.equal(D.currentLabel(seps, 5000), 'Today');
});

test('no separator above the top: nothing to show', () => {
  assert.equal(D.currentLabel([{ y: 50, label: 'Today' }], 10), null);
  assert.equal(D.currentLabel([], 100), null);
});
