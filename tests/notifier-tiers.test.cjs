// Run with: node tests/notifier-tiers.test.cjs
// Notifier 1.5.0: Ui/tiers.js must classify exactly like NotifierSettings (C#). Both run tests/notifier-tier-cases.json.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const t = require(path.join(__dirname, "../src/IPop.Notifier/Ui/tiers.js"));
const cases = JSON.parse(fs.readFileSync(path.join(__dirname, "notifier-tier-cases.json"), "utf8"));
assert.ok(cases.length > 40, "case table loaded");

// Optional ageHours / read (1.5.1): an iDesk ticket assigned to me is Needs action only while unread and < 24 h old.
const NOW = Date.parse("2026-11-21T08:00:00Z");
for (const c of cases) {
  const label = `${c.eventType}/${c.priority}/${c.leader ? "leader" : "-"}${c.targetUrl ? " " + c.targetUrl : ""}${c.ageHours != null ? " " + c.ageHours + "h" : ""}${c.read ? " read" : ""}`;
  const kind = t.kindOf(c.eventType, c.priority, c.targetUrl);
  assert.equal(kind, c.kind, "kind of " + label);
  const at = c.ageHours != null ? new Date(NOW - c.ageHours * 3600e3).toISOString() : undefined;
  assert.equal(t.tierFor(kind, c.eventType, c.priority, c.leader, {}, at, !!c.read, NOW), c.tier, "tier of " + label);
  // classify() reads the row's own at / isRead.
  if (!c.leader) assert.equal(t.classify({ eventType: c.eventType, priority: c.priority, targetUrl: c.targetUrl, at, isRead: !!c.read }, {}, NOW).tier, c.tier, "classify " + label);
}
// No clock given → the real one: assigned just now still acts, two days ago does not; a live notice without "at" acts.
assert.equal(t.classify({ eventType: "ticket.assigned", at: new Date().toISOString() }, {}).tier, "act");
assert.equal(t.classify({ eventType: "ticket.assigned", at: new Date(Date.now() - 48 * 3600e3).toISOString() }, {}).tier, "msg");
assert.equal(t.classify({ eventType: "ticket.assigned" }, {}).tier, "act");
// A chosen msg/info stays; the rule only ever lowers Needs action.
assert.equal(t.tierFor("ticketassigned", "ticket.assigned", "normal", false, { ticketassigned: "info" }, new Date(NOW - 48 * 3600e3), false, NOW), "info");

// Every kind has a default and the 15 keys match the C# list.
assert.equal(t.KINDS.length, 15);
for (const k of t.KINDS) assert.ok(["act", "msg", "info"].includes(t.defaultTier(k)), k);

// Settings override the default, but never the always-act rules; unknown values fall back.
assert.equal(t.tierFor("news", "news.published", "normal", false, { news: "act" }), "act");
assert.equal(t.tierFor("mail", "mail.new", "normal", false, { mail: "info" }), "info");
assert.equal(t.tierFor("mail", "mail.new", "high", false, { mail: "info" }), "act");
assert.equal(t.tierFor("chat", "chat.message", "normal", true, { chat: "info" }), "act");
assert.equal(t.tierFor("news", "memo.mandatory", "normal", false, { news: "info" }), "act");
assert.equal(t.tierFor("calls", "call.missed", "normal", false, { calls: "loud" }), "msg");

// classify(): leaders and VIPs from the app's context, ids compared case-insensitively.
const ctx = { tiers: { chat: "msg" }, leaders: [{ id: "AAAAAAAA-0000-0000-0000-000000000001", name: "Boss", reason: "Manager" }], vips: [{ id: "bbbbbbbb-0000-0000-0000-000000000002" }] };
assert.deepEqual(t.classify({ eventType: "chat.message", priority: "normal", actorId: "aaaaaaaa-0000-0000-0000-000000000001" }, ctx), { kind: "chat", tier: "act", leader: "Manager" });
assert.deepEqual(t.classify({ eventType: "mail.new", priority: "low", actorId: "BBBBBBBB-0000-0000-0000-000000000002" }, ctx), { kind: "mailmass", tier: "act", leader: "VIP" });
assert.deepEqual(t.classify({ type: "mail", priority: "low" }, ctx), { kind: "mail", tier: "msg", leader: null }); // old server: coarse type only
assert.deepEqual(t.classify({ eventType: "news.published", priority: "normal" }, null), { kind: "news", tier: "info", leader: null });

console.log(`notifier tiers: ${cases.length} cases ok`);
