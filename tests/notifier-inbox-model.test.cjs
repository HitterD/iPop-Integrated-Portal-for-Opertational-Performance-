// Run with: node tests/notifier-inbox-model.test.cjs
// Notifier 1.5.0 Inbox: one row per chat room / ticket / project task, tiers per thread, counts and filters.
const assert = require("node:assert/strict");
const path = require("node:path");

const m = require(path.join(__dirname, "../src/IPop.Notifier/Ui/inbox-model.js"));
const ROOM = "11111111-1111-1111-1111-111111111111";
const TIX = "22222222-2222-2222-2222-222222222222";
const PRJ = "33333333-3333-3333-3333-333333333333";
const TASK = "44444444-4444-4444-4444-444444444444";
const BOSS = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
const ctx = { tiers: {}, leaders: [{ id: BOSS, name: "Boss", reason: "Manager" }], vips: [] };

let n = 0;
const row = o => Object.assign({ id: "r" + ++n, type: "system", title: "t", body: "", at: "2026-11-21T08:00:00Z", isRead: false, priority: "normal" }, o);

// thread keys
assert.equal(m.threadKey({ targetUrl: `/chat/${ROOM}` }), "chat:" + ROOM);
assert.equal(m.threadKey({ targetUrl: `/tickets/${TIX}` }), "ticket:" + TIX);
assert.equal(m.threadKey({ targetUrl: `/idesk/tickets/${TIX}#update` }), "ticket:" + TIX);
assert.equal(m.threadKey({ targetUrl: `/projects/${PRJ}?task=${TASK}` }), "task:" + TASK);
assert.equal(m.threadKey({ targetUrl: `/projects/${PRJ}` }), null);
assert.equal(m.threadKey({ targetUrl: "/mail?mailId=x" }), null);

const rows = [
  row({ eventType: "ticket.message_created", targetUrl: `/tickets/${TIX}`, body: "Ursula · Finance\nUrsula: “bisa”\n#201126-FIN-0031" }),
  row({ eventType: "chat.message", type: "chat", targetUrl: `/chat/${ROOM}`, title: "Agus", body: "second" }),
  row({ eventType: "ticket.assigned", targetUrl: `/tickets/${TIX}`, isRead: true }),
  row({ eventType: "chat.message", type: "chat", targetUrl: `/chat/${ROOM}`, title: "Agus", body: "first", actorId: BOSS }),
  row({ eventType: "news.published", type: "news", targetUrl: "/news/x" }),
  row({ eventType: "approval.requested", targetUrl: "/spp/1" }),
  row({ eventType: "mail.new", type: "mail", priority: "low", targetUrl: "/mail?mailId=1" }),
];
const th = m.group(rows, ctx);
assert.equal(th.length, 5, "ticket ×2 and chat ×2 fold into one row each");
const [tix, chat, news, appr, mass] = th;
assert.equal(tix.items.length, 2);
assert.equal(tix.id, rows[0].id, "head = newest");
assert.equal(tix.tier, "msg", "unread ticket chat decides; the read 'assigned' (act) does not");
assert.equal(tix.line.tag, "#201126-FIN-0031");
assert.equal(tix.line.who, "Ursula");
assert.equal(chat.items.length, 2);
assert.equal(chat.tier, "act", "a leader's older unread message lifts the whole chat");
assert.equal(chat.leader, "Manager");
assert.equal(news.tier, "info");
assert.ok(appr.approval && appr.tier === "act");
assert.equal(mass.kind, "mailmass");
assert.equal(mass.tier, "info");

// all read → tier of the highest update
const allRead = m.group([row({ eventType: "ticket.message_created", targetUrl: `/tickets/${TIX}`, isRead: true }),
  row({ eventType: "ticket.assigned", targetUrl: `/tickets/${TIX}`, isRead: true })], ctx);
assert.equal(allRead[0].tier, "act");
assert.equal(allRead[0].unread, false);

// counts + filters
const c = m.counts(th);
assert.deepEqual([c.all, c.act, c.msg, c.info, c.lead, c.appr], [5, 2, 1, 2, 1, 1]);
assert.equal(c.mod.tickets, 1);
assert.equal(c.mod.approval, 1);
assert.equal(th.filter(t => m.inView(t, "act")).length, 2);
assert.equal(th.filter(t => m.inView(t, "lead")).length, 1);
assert.equal(th.filter(t => m.inView(t, "chat")).length, 1);
assert.equal(th.filter(t => m.inView(t, "all")).length, 5);
assert.ok(m.matches(chat, "FIRST"), "search looks inside older updates too");
assert.ok(!m.matches(news, "zzz"));

// settings move a kind
const moved = m.group([row({ eventType: "news.published", type: "news" })], { tiers: { news: "msg" } });
assert.equal(moved[0].tier, "msg");

// days
assert.equal(m.dayLabel("2026-11-21T09:00:00", "2026-11-21T18:00:00"), "Today");
assert.equal(m.dayLabel("2026-11-20T23:00:00", "2026-11-21T08:00:00"), "Yesterday");
assert.match(m.dayLabel("2026-11-17T09:00:00", "2026-11-21T08:00:00"), /^Tuesday 17 Nov$/);

// modules
assert.equal(m.moduleOf({ eventType: "approval.requested" }), "approval");
assert.equal(m.moduleOf({ type: "system", targetUrl: `/idesk/tickets/${TIX}` }), "tickets");
assert.equal(m.moduleOf({ type: "project", eventType: "project.comment" }), "projects");

console.log("notifier inbox model: ok");
