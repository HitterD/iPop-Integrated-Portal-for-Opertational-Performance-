// Mail recipients line (QA 3): show as many WHOLE items (names / group chips, each capped by CSS) as fit on
// one line, then "+N". Never squeeze every chip to "SJ…".
const test = require('node:test');
const assert = require('node:assert/strict');
const { fitCount } = require('../src/IPop.Web/wwwroot/js/mail-fit.js');

test('everything fits: no +N needed', () => {
  assert.equal(fitCount({ available: 400, widths: [30, 100, 100], gap: 4, moreWidth: 30 }), 3);
});

test('keeps whole items and leaves room for +N', () => {
  // you(30) + 4 + chip(100) + 4 + chip(100) = 238; + 4 + more(30) = 272 <= 280; a third chip would not fit.
  assert.equal(fitCount({ available: 280, widths: [30, 100, 100, 100, 100], gap: 4, moreWidth: 30 }), 3);
  assert.equal(fitCount({ available: 271, widths: [30, 100, 100, 100, 100], gap: 4, moreWidth: 30 }), 2);
});

test('the last item may fit exactly without +N even when +N would not', () => {
  assert.equal(fitCount({ available: 238, widths: [30, 100, 100], gap: 4, moreWidth: 30 }), 3);
});

test('at least one item, even on a very narrow line', () => {
  assert.equal(fitCount({ available: 10, widths: [200, 100], gap: 4, moreWidth: 30 }), 1);
  assert.equal(fitCount({ available: 0, widths: [], gap: 4, moreWidth: 30 }), 0);
});

test('bad measurements fall back to one item', () => {
  assert.equal(fitCount({ available: NaN, widths: [30, 100], gap: 4, moreWidth: 30 }), 1);
});
