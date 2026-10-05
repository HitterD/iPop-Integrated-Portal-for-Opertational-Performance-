// Run with: node tests/user-store.test.cjs
// Per-account settings: a value saved under the old shared key goes to the first account that
// reads it, and the next account on the same browser no longer sees it.
const assert = require("node:assert/strict");
const path = require("node:path");

const m = new Map();
globalThis.window = globalThis;
globalThis.localStorage = {
  getItem: k => (m.has(k) ? m.get(k) : null),
  setItem: (k, v) => m.set(k, String(v)),
  removeItem: k => m.delete(k),
};
require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/user-store.js"));
const s = globalThis.ipopUserStore;

m.set("ipop.mail.signature", "Regards, A");
assert.equal(s.get("A", "ipop.mail.signature"), "Regards, A", "first account inherits the legacy value");
assert.equal(m.has("ipop.mail.signature"), false, "legacy key is removed once claimed");
assert.equal(s.get("B", "ipop.mail.signature"), null, "the next account does not see it");
s.set("B", "ipop.mail.signature", "Thanks, B");
assert.equal(s.get("A", "ipop.mail.signature"), "Regards, A");
assert.equal(s.get("B", "ipop.mail.signature"), "Thanks, B");

// Signed out: nothing is read or written.
assert.equal(s.get(null, "ipop.mail.signature"), null);
s.set("", "x", "y");
assert.equal(m.has("x@"), false);

// getMine/setMine follow setUser (sounds.js).
s.setUser("A");
s.setMine("notification-sounds", "{\"volume\":40}");
assert.equal(m.get("notification-sounds@A"), "{\"volume\":40}");
s.setUser(null);
assert.equal(s.getMine("notification-sounds"), null);
console.log("user-store: ok");
