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

// all read → tier of the highest update; an opened "assigned to me" no longer needs action (1.5.1)
const allRead = m.group([row({ eventType: "ticket.message_created", targetUrl: `/tickets/${TIX}`, isRead: true }),
  row({ eventType: "ticket.assigned", targetUrl: `/tickets/${TIX}`, isRead: true })], ctx);
assert.equal(allRead[0].tier, "msg");
assert.equal(allRead[0].unread, false);
const apprRead = m.group([row({ eventType: "approval.requested", isRead: true })], ctx);
assert.equal(apprRead[0].tier, "act", "the read rule is only for tickets assigned to me");

// counts + filters
const c = m.counts(th);
assert.deepEqual([c.all, c.act, c.msg, c.info, c.lead, c.appr, c.tix], [5, 2, 1, 2, 1, 1, 0]);
assert.equal(c.mod.tickets, 1);
assert.equal(c.mod.approval, 1);
assert.equal(th.filter(t => m.inView(t, "act")).length, 2);
assert.equal(th.filter(t => m.inView(t, "lead")).length, 1);
assert.equal(th.filter(t => m.inView(t, "chat")).length, 1);
assert.equal(th.filter(t => m.inView(t, "all")).length, 5);
assert.ok(m.matches(chat, "FIRST"), "search looks inside older updates too");
assert.ok(!m.matches(news, "zzz"));

// Needs action = act AND unread (H1): a read approval leaves the section, the card and the Act chip, and stays in
// All as a read row of its day; the summary card and the section agree.
const na = m.group([
  row({ eventType: "approval.requested", targetUrl: "/spp/9" }),
  row({ eventType: "approval.requested", targetUrl: "/spp/8", isRead: true }),
  row({ eventType: "chat.message", type: "chat", targetUrl: `/chat/${ROOM}`, actorId: BOSS, isRead: true }),
  row({ eventType: "chat.message", type: "chat", title: "Ana", targetUrl: "/chat/x" }),
], ctx);
const [apprOpen, apprDone, bossRead, plain] = na;
assert.equal(apprDone.tier, "act", "the tier itself does not change when read");
assert.equal(m.needsAction(apprOpen), true);
assert.equal(m.needsAction(apprDone), false, "read → not Needs action any more");
assert.equal(m.needsAction(bossRead), false);
assert.deepEqual(na.filter(t => m.inView(t, "act")).map(t => t.id), [apprOpen.id], "Act chip / maximized Needs action: unread only");
assert.equal(na.filter(t => m.inView(t, "all")).length, 4, "read ones stay in All");
assert.equal(na.filter(m.needsAction).length, m.counts(na).act, "summary card = section");
assert.deepEqual(na.map(m.groupOf), ["act", "msg", "msg", "msg"], "a read act thread is drawn with Messages");
assert.equal(m.groupOf(news), "info");
// Marking the open one read empties Needs action.
apprOpen.items.forEach(x => (x.isRead = true)); apprOpen.unread = false;
assert.equal(na.filter(m.needsAction).length, 0);
assert.equal(m.counts(na).act, 0);

// settings move a kind
const moved = m.group([row({ eventType: "news.published", type: "news" })], { tiers: { news: "msg" } });
assert.equal(moved[0].tier, "msg");

// days: group headers + row times (1.5.1)
assert.equal(m.dayLabel("2026-11-21T09:00:00", "2026-11-21T18:00:00"), "Today");
assert.equal(m.dayLabel("2026-11-20T23:00:00", "2026-11-21T08:00:00"), "Yesterday");
assert.equal(m.dayLabel("2026-11-17T09:00:00", "2026-11-21T08:00:00"), "Tue 17 Nov");
assert.equal(m.relTime("2026-11-21T16:38:00", "2026-11-21T18:00:00", "en-GB"), "16:38");
assert.equal(m.relTime("2026-11-21T16:38:00", "2026-11-21T18:00:00", "id-ID"), "16.38");
assert.equal(m.relTime("2026-11-21T04:05:00", "2026-11-21T18:00:00", "en-US"), "04:05", "24 h even in en-US");
assert.equal(m.relTime("2026-11-20T16:38:00", "2026-11-21T08:00:00"), "Yesterday");
assert.equal(m.relTime("2026-11-16T10:00:00", "2026-11-21T08:00:00"), "Mon");
assert.equal(m.relTime("2026-11-15T10:00:00", "2026-11-21T08:00:00"), "Sun", "6 days ago is still a weekday");
assert.equal(m.relTime("2026-11-14T10:00:00", "2026-11-21T08:00:00"), "14 Nov");

// summary: named parts, singular for 1, zeros left out
const sp = p => m.summaryParts(p).map(x => `${x[1]} ${x[2]}`).join(" · ");
assert.equal(sp({ appr: 2, lead: 2, tix: 1, msg: 4 }), "2 approvals · 2 leaders · 1 ticket · 4 messages");
assert.equal(sp({ appr: 1, lead: 0, tix: 7, msg: 0 }), "1 approval · 7 tickets");
assert.equal(sp({ appr: 0, lead: 1, tix: 0, msg: 1 }), "1 leader · 1 message");
assert.equal(sp({ appr: 0, lead: 0, tix: 0, msg: 0 }), "");
assert.deepEqual(m.summaryParts({ appr: 0, lead: 0, tix: 2, msg: 3 }).map(x => x[3]), ["act", "msg"], "tickets jump to Needs action, messages to Messages");

// iDesk: status chips, "Assigned to you", the 24 h rule in the Inbox
const NOW = "2026-11-21T08:00:00Z", h = n => new Date(Date.parse(NOW) - n * 3600e3).toISOString();
const T1 = "55555555-5555-5555-5555-555555555555", T2 = "66666666-6666-6666-6666-666666666666", T3 = "77777777-7777-7777-7777-777777777777";
const tixRows = [
  row({ eventType: "ticket.assigned", targetUrl: `/idesk/tickets/${T1}#update`, body: "Eklesia V · Reporter · QC\nAssigned to you. SLA target: Sat 21 Nov, 18:00.\n#211126-TRA-0029", at: h(1) }),
  row({ eventType: "ticket.assigned", targetUrl: `/idesk/tickets/${T2}#update`, body: "Budi · Reporter · Logistic\nAssigned to you.\n#201126-LOG-0007", at: h(30) }),
  row({ eventType: "ticket.resolved", targetUrl: `/tickets/${T3}`, body: "Dia · Your PIC · ICT\nMarked as resolved. Please confirm it works.\n#201126-IP-0022", at: h(2) }),
];
const [fresh, old, resolved] = m.group(tixRows, ctx, NOW);
assert.equal(fresh.tier, "act"); assert.equal(fresh.status, "new"); assert.equal(fresh.assigned, true);
assert.equal(old.tier, "msg", "assigned 30 h ago → Messages"); assert.equal(old.assigned, false);
assert.equal(resolved.status, "res"); assert.equal(resolved.tier, "msg");
assert.equal(m.counts([fresh, old, resolved]).tix, 1);
assert.equal(m.ticketStatus({ eventType: "ticket.message_created", body: "U · F\nUrsula: “bisa”" }), "rep");
assert.equal(m.ticketStatus({ eventType: "ticket.closed", body: "x\nConfirmed as fixed by Dia. The ticket is closed." }), "res");
assert.equal(m.ticketStatus({ eventType: "ticket.reopened" }), "new");
assert.equal(m.ticketStatus({ eventType: "ticket.status_changed", body: "x\nReopened by Budi." }), "new");
assert.equal(m.ticketStatus({ eventType: "ticket.status_changed", body: "x\nStatus: In progress → Resolved." }), "res");
assert.equal(m.ticketStatus({ eventType: "ticket.status_changed", body: "x\nStatus: Open → In progress." }), null);
assert.equal(m.ticketStatus({ eventType: "ticket.visit_changed", body: "x\nVisit moved to Mon." }), null);
assert.equal(m.ticketStatus({ type: "system", targetUrl: `/idesk/tickets/${T1}#update`, body: "x\nAssigned to you." }), "new", "old server: read the sentence");
assert.equal(m.assignedToMe({ eventType: "ticket.assigned", targetUrl: `/tickets/${T3}` }), false, "requester's 'picked up your ticket' is not mine");

// modules
assert.equal(m.moduleOf({ eventType: "approval.requested" }), "approval");
assert.equal(m.moduleOf({ type: "system", targetUrl: `/idesk/tickets/${TIX}` }), "tickets");
assert.equal(m.moduleOf({ type: "project", eventType: "project.comment" }), "projects");
assert.equal(m.moduleOf({ type: "system", eventType: "system.epop_import", targetUrl: "/mail" }), "system", "a system notice linking to Mail is iPop's own");
assert.equal(m.moduleOf({ type: "mail", eventType: "mail.new", targetUrl: "/mail?mailId=1" }), "mail");

console.log("notifier inbox model: ok");
