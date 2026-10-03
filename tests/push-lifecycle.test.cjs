// Run with: node tests/push-lifecycle.test.cjs
const assert = require("node:assert/strict");
const path = require("node:path");

function fakeStorage() {
  const m = new Map();
  return { getItem: k => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: k => m.delete(k), _m: m };
}

// ── a fake browser: push manager, service worker with MessageChannel replies, BroadcastChannel ──
const posted = [];
let swReply = null; // (msg) => reply object | undefined
let subscribeImpl = null;
let currentSub = null;
const swListeners = {};
function makeSub(endpoint, keyBytes) {
  return {
    endpoint,
    options: { applicationServerKey: keyBytes ? keyBytes.buffer : null },
    toJSON: () => ({ endpoint, keys: { p256dh: "P", auth: "A" } }),
    unsubscribe: async () => { if (currentSub && currentSub.endpoint === endpoint) currentSub = null; return true; },
  };
}
const reg = {
  active: {
    postMessage(msg, ports) {
      posted.push(msg);
      if (ports && ports[0] && swReply) {
        const r = swReply(msg);
        if (r !== undefined) setTimeout(() => ports[0]._peer.onmessage({ data: r }), 0);
      }
    },
  },
  pushManager: {
    getSubscription: async () => currentSub,
    subscribe: async opts => { const s = await subscribeImpl(opts); currentSub = s; return s; },
  },
};
const bc = [];
globalThis.BroadcastChannel = class { constructor(n) { this.n = n; bc.push(this); } postMessage(d) { for (const o of bc) if (o !== this && o.onmessage) o.onmessage({ data: d }); } close() { } };
globalThis.MessageChannel = class { constructor() { this.port1 = {}; this.port2 = { _peer: this.port1 }; } };
globalThis.localStorage = fakeStorage();
globalThis.isSecureContext = true;
globalThis.PushManager = function () { };
globalThis.Notification = { permission: "granted" };
globalThis.addEventListener = () => { };
globalThis.removeEventListener = () => { };
globalThis.document = { baseURI: "https://ipop.test/", visibilityState: "visible", addEventListener: () => { }, removeEventListener: () => { } };
Object.defineProperty(globalThis, "navigator", {
  value: { serviceWorker: { register: async () => reg, ready: Promise.resolve(reg), controller: null, addEventListener: (t, f) => (swListeners[t] = f), removeEventListener: () => { } } },
  configurable: true,
});

const P = require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/push-lifecycle.js"));
const KEY = "BAAB"; // base64url of bytes [4,0,1]
const keyBytes = Uint8Array.from([4, 0, 1]);

(async () => {
  // 1. Typed, sanitised subscribe errors: the browser message (with a URL in it) never comes through.
  for (const [name, code] of [["AbortError", "subscribe_abort"], ["NotAllowedError", "subscribe_not_allowed"], ["InvalidStateError", "subscribe_invalid_state"], ["NotSupportedError", "subscribe_not_supported"], ["TypeError", "subscribe_failed"]]) {
    subscribeImpl = async () => { const e = new Error("Registration failed - push service https://fcm.googleapis.com/fcm/send/SECRET"); e.name = name; throw e; };
    const r = await P.subscribe(KEY);
    assert.equal(r.ok, false);
    assert.equal(r.code, code);
    assert.ok(!/https?:|fcm|SECRET/.test(r.message), "message must not leak the browser text");
  }

  // 2. Permission and context map to codes; nothing prompts.
  Notification.permission = "denied";
  assert.equal((await P.subscribe(KEY)).code, "permission_denied");
  Notification.permission = "default";
  assert.equal((await P.subscribe(KEY)).code, "permission_default");
  Notification.permission = "granted";
  assert.equal((await P.subscribe("")).code, "no_key");
  globalThis.isSecureContext = false;
  assert.equal((await P.subscribe(KEY)).code, "insecure");
  globalThis.isSecureContext = true;

  // 3. Success, reuse, and replacement on a VAPID key mismatch.
  subscribeImpl = async () => makeSub("https://push.test/new", keyBytes);
  let r = await P.subscribe(KEY);
  assert.equal(r.ok, true); assert.equal(r.created, true); assert.equal(r.endpoint, "https://push.test/new");
  r = await P.subscribe(KEY);
  assert.equal(r.created, false, "existing matching subscription is reused");
  currentSub = makeSub("https://push.test/oldkey", Uint8Array.from([9, 9, 9]));
  r = await P.subscribe(KEY);
  assert.equal(r.created, true); assert.equal(r.endpoint, "https://push.test/new");

  // 4. Generation: global, monotonic, survives a reload (a fresh read of storage), never goes backwards.
  const g1 = await P.nextGen();
  const g2 = await P.nextGen();
  assert.ok(g2 > g1);
  localStorage.setItem("ipop.push.gen", String(g2 + 1e9)); // a "future" value from a skewed clock
  const g3 = await P.nextGen();
  assert.equal(g3, g2 + 1e9 + 1, "a high stored gen still advances by one, no lockout");

  // 5. Intent: legacy off flag honoured before the first write; setIntent broadcasts; isCurrent guards.
  localStorage.removeItem("ipop.push.intent");
  localStorage.setItem("ipop-push-off", "1");
  assert.equal(P.intent().optedOut, true);
  const other = new BroadcastChannel("ipop-push");
  let heard = null; other.onmessage = e => (heard = e.data);
  let calls = [];
  P.start({ invokeMethodAsync: (m, ...a) => calls.push([m, ...a]) });
  const gA = await P.setIntent("aaaa", false);
  assert.equal(localStorage.getItem("ipop-push-off"), null, "legacy flag follows the intent");
  assert.deepEqual(heard, { type: "intent", gen: gA, acct: "aaaa", optedOut: false });
  assert.ok(P.isCurrent(gA));
  // Another tab opts out: this tab's run at gA is no longer current, and .NET hears about it.
  other.postMessage({ type: "intent", gen: gA + 1, acct: "aaaa", optedOut: true });
  localStorage.setItem("ipop.push.intent", JSON.stringify({ gen: gA + 1, acct: "aaaa", optedOut: true }));
  assert.equal(P.isCurrent(gA), false);
  assert.deepEqual(calls.at(-1), ["OnPushIntentChanged"]);

  // 6. SW repair message reaches .NET; an unversioned one is ignored.
  calls = [];
  swListeners.message({ data: { type: "ipop-push-repair", v: 1, reason: "subscriptionchange" } });
  swListeners.message({ data: { type: "ipop-push-repair" } });
  assert.deepEqual(calls, [["OnPushRepair"]]);
  P.stop();

  // 7. Hello: an old worker that never replies counts as schema 0 after the timeout.
  swReply = () => undefined;
  let h = await P.hello();
  assert.deepEqual(h, { schema: 0, repairNeeded: false, lastResult: null });
  swReply = m => m.type === "ipop-push-hello"
    ? { type: "ipop-push-hello", v: 1, schema: 1, repairNeeded: true, lastResult: { outcome: "notification-created", at: 5, kind: "chat", body: "secret" } }
    : undefined;
  h = await P.hello();
  assert.equal(h.schema, 1); assert.equal(h.repairNeeded, true);
  assert.deepEqual(h.lastResult, { outcome: "notification-created", at: 5, kind: "chat" }, "only whitelisted fields pass");

  // 8. Binding counts as acked only when the worker accepted this exact generation.
  swReply = m => m.type === "ipop-push-binding" ? { type: "ipop-push-binding", v: 1, schema: 1, gen: m.gen, accepted: true } : undefined;
  assert.equal(await P.sendBinding("aaaa", 42, false), true);
  assert.deepEqual(posted.at(-1), { type: "ipop-push-binding", v: 1, acct: "aaaa", gen: 42, optedOut: false });
  swReply = m => ({ type: "ipop-push-binding", v: 1, schema: 1, gen: m.gen, accepted: false });
  assert.equal(await P.sendBinding("aaaa", 43, false), false, "a stale gen rejected by the worker is not acked");
  swReply = m => ({ type: "ipop-push-binding", v: 1, schema: 1, gen: m.gen - 1, accepted: true });
  assert.equal(await P.sendBinding("aaaa", 44, false), false);

  // 9. Preferences: volume clamped to 0..1; no reply expected.
  await P.sendPreferences(true, 1.7);
  assert.deepEqual(posted.at(-1), { type: "ipop-push-preferences", v: 1, soundEnabled: true, volume: 1 });
  await P.sendPreferences(false, null);
  assert.deepEqual(posted.at(-1), { type: "ipop-push-preferences", v: 1, soundEnabled: false, volume: null });

  // 10. Account hash: stable, 16 hex chars, case-insensitive GUID; the raw id is never stored.
  const a1 = await P.acct("6F9619FF-8B86-D011-B42D-00C04FC964FF");
  const a2 = await P.acct("6f9619ff-8b86-d011-b42d-00c04fc964ff");
  assert.equal(a1, a2); assert.match(a1, /^[0-9a-f]{16}$/);
  P.rememberRegistration(a1, await P.endpointDigest("https://push.test/new"));
  const stored = [...localStorage._m.values()].join("|");
  assert.ok(!/6f9619ff|push\.test/i.test(stored), "storage holds hashes only");

  // 11. Background handoff flag.
  assert.equal(globalThis.ipopPushBackgroundHandoff, false);
  P.setBackgroundHandoff(true);
  assert.equal(globalThis.ipopPushBackgroundHandoff, true);

  // 12. Unsubscribe returns the endpoint and clears the subscription.
  currentSub = makeSub("https://push.test/x", keyBytes);
  assert.equal(await P.unsubscribe(), "https://push.test/x");
  assert.equal(await P.unsubscribe(), null);

  console.log("push-lifecycle: all tests passed");
})().catch(e => { console.error(e); process.exit(1); });
