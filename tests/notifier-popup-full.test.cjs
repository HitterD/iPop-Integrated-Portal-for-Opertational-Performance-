// Run with: node tests/notifier-popup-full.test.cjs
// Notifier 1.5.1 maximized popup = full layout per source (docs/mockups/notifier/priority.html screen 7,
// src/IPop.Notifier/Ui/popup-full.js). Checks the pure rules: which layout, "Key: value" facts, source chip, the
// primary button's label, iDesk steps / SLA, and that every layout renders from real-shaped data without inventing.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const ui = path.join(__dirname, "../src/IPop.Notifier/Ui");
const win = {};
vm.runInNewContext(fs.readFileSync(path.join(ui, "popup-full.js"), "utf8"), { window: win, URL, IC: n => `<i data-lucide="${n}"></i>`, initials: n => String(n || "?").slice(0, 2).toUpperCase(), avatarColor: () => "#555" });
const F = win.popupFull;
const plain = x => JSON.parse(JSON.stringify(x));

// "Key: value" facts only with ≥ 2 lines; a chat quote is not a fact.
assert.deepEqual(plain(F.keyValues("Total: Rp 18.450.000\nDue: Today 17:00\nThe quotation is attached.")),
  { pairs: [{ k: "Total", v: "Rp 18.450.000" }, { k: "Due", v: "Today 17:00" }], rest: "The quotation is attached." });
assert.equal(F.keyValues("Note: one sentence only").pairs.length, 0);
assert.equal(F.keyValues("Rizky: “halo”\nBudi: “ok”").pairs.length, 0);
assert.equal(F.keyValues("275 new messages from EPop were added to Mail.").pairs.length, 0, "no invented stat grid");

// Source chip + primary button label.
assert.equal(F.sourceLabel("system.epop_import"), "From iPop · EPop import");
assert.equal(F.sourceLabel("hris.department_changed"), "From HRIS · Department changed");
assert.equal(F.sourceLabel("approval.requested"), "Approval · Requested");
assert.equal(F.openLabel("/mail"), "Open in Mail");
assert.equal(F.openLabel("/calendar?event=x"), "Open in Calendar");
assert.equal(F.openLabel("https://spp.santos.co.id/r/1"), "Open spp.santos.co.id");
assert.equal(F.openLabel("/something-new"), "Open in iPop");
assert.equal(F.openLabel(null), "Open in iPop");

// iDesk steps: Open → Assigned → In progress → Resolved → Closed.
assert.equal(F.ticketStep("TODO", false), 0);
assert.equal(F.ticketStep("TODO", true), 1);
assert.equal(F.ticketStep("IN_PROGRESS", true), 2);
assert.equal(F.ticketStep("WAITING_VENDOR", true), 2);
assert.equal(F.ticketStep("RESOLVED", true), 3);
assert.equal(F.ticketStep("CLOSED", true), 4);
assert.equal(F.ticketStep("FAILED", false), null);
assert.equal(F.ticketStepOfEvent("ticket.assigned", "Assigned to you."), 1);
assert.equal(F.ticketStepOfEvent("ticket.message_created", "Reopened by Budi"), 2);
assert.equal(F.ticketStepOfEvent("ticket.message_created", "Ursula: “x”"), null);
const now = new Date("2026-11-21T10:00:00Z");
assert.equal(F.slaText("2026-11-21T12:41:00Z", now), "SLA 2 h 41 m left");
assert.equal(F.slaText("2026-11-21T09:30:00Z", now), "SLA 30 m overdue");
assert.equal(F.slaText(null, now), null);

// Meeting helpers.
assert.deepEqual(plain(F.agendaItems("1. Budget\n2. Network")), ["Budget", "Network"]);
assert.equal(F.agendaItems("Bring your laptop."), null);
assert.equal(F.duration("2026-11-21T15:00:00Z", "2026-11-21T16:00:00Z"), "1 hour");
assert.equal(F.duration("2026-11-21T15:00:00Z", "2026-11-21T16:30:00Z"), "1 h 30 min");

// Layout choice: approvals by kind; otherwise the loader's source; nothing loaded → the info card.
assert.equal(F.kindOf({ kind: "approval", eventType: "approval.requested" }, { src: null }), "approval");
assert.equal(F.kindOf({ kind: "system" }, { src: null }), "system");
assert.equal(F.kindOf({ kind: "mail" }, { src: { kind: "mail" } }), "mail");
assert.equal(F.kindOf({ kind: "mail" }, { src: { kind: "nope" } }), "system");

// Rendering from real-shaped data: no NIK, no position, the system card shows only what the notice says.
const files = { fmtSize: b => b + " B", grid: l => (l && l.length ? `<div class="fgrid">${l.length}</div>` : "") };
const N = o => Object.assign({ id: "n", title: "T", body: "", at: "2026-11-21T03:00:00Z", eventType: "system.x", module: "system", kind: "system", tier: "info" }, o);
const sys = F.render({ loaded: true, N: N({ title: "Your EPop history is in iPop Mail", body: "275 new messages were added.", eventType: "system.epop_import", targetUrl: "/mail" }), view: {}, files });
assert.match(sys, /From iPop · EPop import/);
assert.match(sys, /Open in Mail/);
assert.doesNotMatch(sys, /statgrid/, "no stat grid without Key: value lines");
const mail = F.render({ loaded: true, nik: "0001", files, N: N({ module: "mail", kind: "mail" }), view: { files: [], src: { kind: "mail", canAll: true, dept: "Finance", d: {
  subject: "Q4", senderName: "Lina Santoso", senderNik: "0099", sentAt: "2026-11-21T03:00:00Z", attachments: [], recipientTotal: 5,
  recipients: [{ name: "Rudi", nik: "0001", recipientType: "to" }, { name: "A", nik: "1", recipientType: "to" }, { name: "B", nik: "2", recipientType: "to" }, { name: "C", nik: "3", recipientType: "to" }, { name: "D", nik: "4", recipientType: "cc" }] } } } });
assert.match(mail, />You</, "me first, as You");
assert.match(mail, /\+1 more/);
assert.match(mail, /Finance/);
assert.doesNotMatch(mail, /0099|0001/, "never NIK");
assert.match(mail, /iframe data-mail sandbox="allow-same-origin"/, "mail HTML only in the sandboxed frame");
const tk = F.render({ loaded: true, me: "Rudi", files, N: N({ module: "tickets", eventType: "ticket.message_created", at: "2026-11-21T03:00:00Z" }), view: { reply: {}, src: { kind: "ticket", t: null,
  x: { title: "PRINTER", who: { name: "Ursula", sub: "Finance" }, sentence: "Ursula: “halo”", number: "#1" }, c: { from: "Ursula", text: "halo" } } } });
assert.match(tk, /Reply to Ursula/);
assert.match(tk, /halo/);
assert.doesNotMatch(tk, /class="steps"/, "a chat message on the technician side has no known step");
assert.equal(F.render({ loaded: false, N: N({}), view: {}, files }).includes("skel"), true, "skeleton while loading");
// Meeting: the place is Location; MeetingDto.RoomName is the LiveKit room id ("meeting-<guid>") and never shown.
const mt = { id: "m1", title: "Budget", startAt: "2026-11-21T15:58:00Z", endAt: "2026-11-21T16:58:00Z", location: "Ruang Rapat Lt. 2",
  roomName: "meeting-53f09019d2234a129be676f2195add35", organizerName: "Adrio", organizerNik: "0002", myResponse: "pending", attendees: [] };
const meet = F.render({ loaded: true, nik: "0001", files, N: N({ module: "calendar", kind: "calendar", eventType: "meeting.invited" }), view: { src: { kind: "meeting", mt, invited: true } } });
assert.match(meet, /Ruang Rapat Lt\. 2/);
assert.doesNotMatch(meet, /meeting-53f0/, "no LiveKit room id as the place");
const popupJs = fs.readFileSync(path.join(ui, "popup.js"), "utf8");
// Projects: task stages are todo | doing | review | done (the API rejects "in_progress"); "doing" reads In progress.
const task = F.render({ loaded: true, nik: "0001", files, N: N({ module: "projects", kind: "projects", eventType: "project.task_assigned" }), view: { src: { kind: "project",
  t: { id: "t1", title: "Switch", status: "doing", priority: "high", assignees: [] }, subs: [], comments: [] } } });
assert.match(task, /data-stpill>In progress</);
assert.doesNotMatch(popupJs, /'in_progress' : 'done'|next = done \? 'in_progress'/, "reopening a task moves it to doing");
assert.doesNotMatch(popupJs, /src\.roomName/, "the small card doesn't show the LiveKit room id either");

// Wiring: popup.html loads popup-full.js before popup.js; ApiBridge allows the extra reads only.
const html = fs.readFileSync(path.join(ui, "popup.html"), "utf8");
assert.ok(html.indexOf("popup-full.js") > 0 && html.indexOf("popup-full.js") < html.indexOf("popup.js\""), "popup-full.js before popup.js");
const bridge = fs.readFileSync(path.join(__dirname, "../src/IPop.Notifier/Core/ApiBridge.cs"), "utf8");
for (const r of ["^api/projects/tasks/{G}/comments$", "^api/projects/{G}/tasks\\\\?parentId={G}$", "^api/chat/rooms/{G}$"])
  assert.ok(bridge.includes(`("GET", R($"${r}"))`), r);
console.log("notifier popup full: ok");
