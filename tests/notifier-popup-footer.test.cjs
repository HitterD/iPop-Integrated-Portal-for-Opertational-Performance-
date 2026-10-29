// Run with: node tests/notifier-popup-footer.test.cjs
// Notifier 1.4.0 popup footer (docs/mockups/notifier/aligned.html §1): row 1 = the main action full width, row 2 =
// up to 3 equal buttons, more than 4 actions → extra under "More". Every module's button list must fit that and
// always say "Mark as read" in full. Runs the real popup.js against a tiny fake DOM/bridge.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const ui = path.join(__dirname, "../src/IPop.Notifier/Ui");
const src = fs.readFileSync(path.join(ui, "popup.js"), "utf8");
const css = fs.readFileSync(path.join(ui, "popup.css"), "utf8");
const app = fs.readFileSync(path.join(ui, "app.css"), "utf8");
const win = { };
const noop = () => { };
vm.runInNewContext(src, {
  window: Object.assign(win, { app: { api: noop, image: noop, esc: s => String(s), post: noop, snack: noop, on: noop, ready: noop }, addEventListener: noop }),
  app: { on: noop, ready: noop }, document: { documentElement: { classList: { contains: () => false } } },
  requestAnimationFrame: noop, getComputedStyle: () => ({}), IC: n => n, ICON: {}, MOD: {},
});
const vmPlan = win.__notifierFooterPlan;
const plan = tools => JSON.parse(JSON.stringify(vmPlan(tools))); // plain objects (the script runs in another realm)
assert.equal(typeof vmPlan, "function", "popup.js exposes its footer plan");

const t = (label, extra) => Object.assign({ label }, extra);
// Primary = the tool marked 'p'; the rest fill row 2.
assert.deepEqual(plan([t("Open in iPop", { cls: "p" }), t("Mark as read")]), { primary: 0, row2: [1], menu: [] });
assert.deepEqual(plan([t("Snooze"), t("Accept", { cls: "p" }), t("Open"), t("Decline", { cls: "r" })]), { primary: 1, row2: [0, 2, 3], menu: [] });
// No 'p': the first one leads.
assert.deepEqual(plan([t("A"), t("B")]).primary, 0);
// 5 actions: 2 stay in row 2 (+ More), `more` ones go to the menu first.
const mail = [t("Reply", { cls: "p", reply: true }), t("Reply all"), t("Forward", { more: true }), t("Open", { more: true }), t("Mark as read")];
assert.deepEqual(plan(mail), { primary: 0, row2: [1, 4], menu: [2, 3] });
assert.deepEqual(plan([]), { primary: -1, row2: [], menu: [] });

// Every module's list (mail without Reply all, calendar invite, news ack, projects, notes, calls, default) fits:
// main + ≤3 in row 2, so nothing needs "More".
for (const n of [3, 4]) assert.equal(plan(Array.from({ length: n }, (_, i) => t("x" + i))).menu.length, 0);

// Wording: "Mark as read" in full, never shortened; no old soft/right-aligned split; Read full message maximises.
assert.ok(!/'Read'\s*\)|label: IC\('check'\) \+ 'Read'/.test(src), "never shorten Mark as read");
assert.ok(!/Show full message|Read in iPop/.test(src), "old 'Show full message' links are gone");
assert.match(src, /Read full message/);
assert.match(src, /type: 'readFull'/);

// Layout: one size, header/footer fixed, 40 px buttons, maximised reading column.
assert.match(css, /\.body\{flex:1;overflow:auto;padding:20px;min-height:0\}/);
assert.match(css, /\.col>\*\+\*\{margin-top:16px\}/);
assert.match(css, /\.tools \.r2\{display:grid;grid-auto-flow:column;grid-auto-columns:1fr/);
assert.match(css, /\.tools \.r2\.tight svg\.lucide\{display:none\}/);
assert.match(css, /html\.max \.col\{max-width:720px/);
assert.match(css, /html\.max \.ptitle\{font-size:30px/);
assert.match(css, /html\.max \.tb2\{height:48px/);
assert.match(app, /--btn-h:40px/);
// v6 tokens, old names still resolve.
for (const v of ["--bg:#F5F5F7", "--surface:#FFFFFF", "--ink:#1D1D1F", "--accent:#2E9E5B", "--accent-soft:#E4F4EA", "--danger:#FF3B30", "--accent:#30D158", "--danger:#FF453A", "--green:var(--accent)", "--glass:var(--surface)"])
  assert.ok(app.includes(v), v);
console.log("notifier popup footer: ok");
