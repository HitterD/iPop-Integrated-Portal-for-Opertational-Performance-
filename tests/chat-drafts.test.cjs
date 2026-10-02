// Run with: node tests/chat-drafts.test.cjs
const assert = require("node:assert/strict");
const path = require("node:path");

function fakeStorage() {
  const m = new Map();
  return {
    get length() { return m.size; },
    key: i => [...m.keys()][i] ?? null,
    getItem: k => (m.has(k) ? m.get(k) : null),
    setItem: (k, v) => m.set(k, String(v)),
    removeItem: k => m.delete(k),
    _m: m,
  };
}

const listeners = {};
const docListeners = {};
globalThis.localStorage = fakeStorage();
globalThis.addEventListener = (t, f) => (listeners[t] = f);
globalThis.document = { visibilityState: "visible", addEventListener: (t, f) => (docListeners[t] = f) };
require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/chat-drafts.js"));
const d = globalThis.ipopDrafts;
const ls = globalThis.localStorage;
const A = "ipop.chat.d2.100.", B = "ipop.chat.d2.200.";

// stage is debounced; get sees the stash before it is written
d.stage(A, "r1", "hi @[Budi](u:x) ");
assert.equal(ls.getItem(A + "r1"), null);
assert.equal(d.get(A, "r1"), "hi @[Budi](u:x) ");
// pagehide flushes synchronously, verbatim
listeners.pagehide();
assert.equal(ls.getItem(A + "r1"), "hi @[Budi](u:x) ");
assert.ok(JSON.parse(ls.getItem(A + "index")).r1);

// visibility hidden flushes
d.stage(A, "r1", "v2");
document.visibilityState = "hidden";
docListeners.visibilitychange();
document.visibilityState = "visible";
assert.equal(ls.getItem(A + "r1"), "v2");

// remove discards a staged write too
d.stage(A, "r1", "v3");
d.remove(A, "r1");
d.flush();
assert.equal(ls.getItem(A + "r1"), null);
assert.equal(ls.getItem(A + "index"), null);

// accounts are separate
d.set(A, "r2", "a"); d.set(B, "r2", "b");
assert.equal(d.get(A, "r2"), "a");
assert.equal(d.get(B, "r2"), "b");

// legacy purge leaves scoped drafts
ls.setItem("ipop.chat.draft.r9", "legacy"); ls.setItem("ipop.chat.drafts", "{}");
d.purgeLegacy();
assert.equal(ls.getItem("ipop.chat.draft.r9"), null);
assert.equal(ls.getItem("ipop.chat.drafts"), null);
assert.equal(d.get(A, "r2"), "a");

// clearAll wipes every account and anything staged
d.stage(A, "r3", "pending");
d.clearAll();
d.flush();
assert.equal(ls._m.size, 0);

// LRU prune per account
d.max = 2;
d.set(A, "x1", "1"); d.set(A, "x2", "2"); d.set(A, "x3", "3");
const left = ["x1", "x2", "x3"].filter(r => ls.getItem(A + r) !== null);
assert.equal(left.length, 2);
d.max = 50;

// quota errors don't throw
const orig = ls.setItem;
ls.setItem = () => { throw new Error("QuotaExceededError"); };
d.set(A, "q", "x");
ls.setItem = orig;

console.log("chat-drafts: ok");
