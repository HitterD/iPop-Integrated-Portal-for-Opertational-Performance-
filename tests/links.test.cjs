// Run with: node tests/links.test.cjs
const assert = require("node:assert/strict");
const path = require("node:path");

require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/links.js"));
const L = globalThis.ipopLinks;
const O = "https://ipop.local:7103";

// classify
assert.deepEqual(L.classify("https://example.com/a?b=1", O), { kind: "external", url: "https://example.com/a?b=1" });
assert.deepEqual(L.classify("https://ipop.local:7103/chat?room=1#m", O), { kind: "internal", path: "/chat?room=1#m" });
assert.deepEqual(L.classify("https://ipop.santos.co.id/news/abc", O), { kind: "internal", path: "/news/abc" });
assert.deepEqual(L.classify("http://IPOP.santos.co.id/projects", O), { kind: "internal", path: "/projects" });
assert.equal(L.classify("https://ipop.santos.co.id/api/files/x", O).kind, "external", "API urls are not app routes");
assert.deepEqual(L.classify("mailto:ann%40x.com?subject=hi", O), { kind: "mailto", addr: "ann@x.com" });
assert.equal(L.classify("javascript:alert(1)", O), null);
assert.equal(L.classify(" JavaScript:alert(1)", O), null);
assert.equal(L.classify("data:text/html,x", O), null);
assert.equal(L.classify("file:///C:/x", O), null);
assert.equal(L.classify("", O), null);

// uncTarget
const nas = { "192.168.2.16": "spj", "10.12.2.6": "krw" };
assert.equal(L.uncTarget("\\\\192.168.2.16\\sja\\Folder A\\Report 2026.xlsx", nas),
  "/files?server=spj&path=" + encodeURIComponent("/sja/Folder A"));
assert.equal(L.uncTarget("\\\\10.12.2.6\\share\\sub\\", nas), "/files?server=krw&path=" + encodeURIComponent("/share/sub"));
assert.equal(L.uncTarget("\\\\10.12.2.6\\share", nas), "/files?server=krw&path=" + encodeURIComponent("/share"));
assert.equal(L.uncTarget("\\\\fileserver\\share\\x", nas), null, "unknown host → copy instead");
assert.equal(L.uncTarget("C:\\x", nas), null);

// mismatch (mail anti-phishing)
assert.equal(L.mismatch("www.paypal.com", "https://evil.example/login"), true);
assert.equal(L.mismatch("https://bank.co.id/login", "https://bank.co.id.evil.ru/"), true);
assert.equal(L.mismatch("paypal.com", "https://www.paypal.com/x"), false, "www. ignored");
assert.equal(L.mismatch("Click here", "https://evil.example"), false, "plain words are not a claim");
assert.equal(L.mismatch("https://example.com/a", "https://example.com/b"), false);
assert.equal(L.mismatch("report.pdf", "https://example.com"), false, "file names are not domains");
assert.equal(L.mismatch("santos.co.id", "https://example.com"), true, "domain-shaped text counts");
assert.equal(L.mismatch("", "https://x.com"), false);

// click handling (delegated, capture phase)
let docClick;
const opened = [], navigated = [], invoked = [];
globalThis.location = { origin: O };
globalThis.open = (u, t, f) => { opened.push([u, t, f]); return {}; };
globalThis.document = {
  addEventListener: (t, f) => { if (t === "click") docClick = f; },
};
const dotNet = {
  invokeMethodAsync: (m, ...a) => {
    invoked.push([m, ...a]);
    if (m === "Navigate") navigated.push(a[0]);
    return Promise.resolve(m === "ConfirmMismatch" ? true : undefined);
  },
};
L.attach(dotNet, nas);

function anchor({ cls = [], attrs = {}, text = "", mail = false }) {
  const a = {
    classList: { contains: c => cls.includes(c) },
    getAttribute: n => (n in attrs ? attrs[n] : null),
    textContent: text,
    closest: sel => (sel === "a" ? a : sel === ".mail-body-links" && mail ? {} : null),
  };
  return a;
}
function click(a, extra = {}) {
  const e = { target: a, button: 0, defaultPrevented: false, prevented: false, ...extra };
  e.preventDefault = () => { e.prevented = true; };
  e.stopPropagation = () => { };
  docClick(e);
  return e;
}

(async () => {
  let e = click(anchor({ cls: ["msg-link"], attrs: { href: "https://example.com/" } }));
  assert.ok(e.prevented);
  assert.deepEqual(opened.pop(), ["https://example.com/", "_blank", "noopener,noreferrer"]);

  e = click(anchor({ cls: ["msg-link"], attrs: { href: "https://ipop.santos.co.id/tasks/1" } }));
  assert.ok(e.prevented);
  assert.equal(navigated.pop(), "/tasks/1");

  e = click(anchor({ cls: ["msg-link"], attrs: { href: "mailto:a@b.com", "data-mailto": "a@b.com" } }));
  assert.equal(navigated.pop(), "/mail?compose=1&toEmail=a%40b.com");

  e = click(anchor({ cls: ["msg-link", "unc-link"], attrs: { href: "#", "data-unc": "\\\\192.168.2.16\\sja\\A" } }));
  assert.ok(e.prevented);
  assert.equal(navigated.pop(), "/files?server=spj&path=%2Fsja%2FA");

  e = click(anchor({ mail: true, attrs: { href: "javascript:alert(1)" } }));
  assert.ok(e.prevented, "javascript: in mail is swallowed");
  assert.equal(opened.length, 0);

  // Ctrl-click on a web link: browser default (new tab), not intercepted.
  e = click(anchor({ cls: ["msg-link"], attrs: { href: "https://example.com/" } }), { ctrlKey: true });
  assert.ok(!e.prevented);

  // Mail phishing link: asks .NET first, opens after confirm.
  e = click(anchor({ mail: true, attrs: { href: "https://evil.example/" }, text: "www.paypal.com" }));
  assert.ok(e.prevented);
  await new Promise(r => setTimeout(r, 0));
  assert.deepEqual(invoked.find(i => i[0] === "ConfirmMismatch"), ["ConfirmMismatch", "www.paypal.com", "https://evil.example/"]);
  assert.deepEqual(opened.pop(), ["https://evil.example/", "_blank", "noopener,noreferrer"]);

  // Same link in chat (not a mail body): no confirm.
  invoked.length = 0;
  click(anchor({ cls: ["msg-link"], attrs: { href: "https://evil.example/" }, text: "www.paypal.com" }));
  assert.equal(invoked.length, 0);
  assert.equal(opened.pop()[0], "https://evil.example/");

  // Anchors outside chat/mail are left alone.
  e = click(anchor({ attrs: { href: "https://example.com" } }));
  assert.ok(!e.prevented);

  console.log("links.test.cjs: all passed");
})().catch(err => { console.error(err); process.exit(1); });
