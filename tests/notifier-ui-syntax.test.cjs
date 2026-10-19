// Run with: node tests/notifier-ui-syntax.test.cjs
// A syntax error in one Notifier page script leaves every window of that kind blank white (seen 2026-10-19 with
// popup.js), so each script must at least parse.
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");

const dir = path.join(__dirname, "../src/IPop.Notifier/Ui");
const files = fs.readdirSync(dir).filter(f => f.endsWith(".js") && !f.endsWith(".min.js") && !f.endsWith(".umd.js"));
const failed = [];
for (const f of files) {
  try { execFileSync(process.execPath, ["--check", path.join(dir, f)], { stdio: "pipe" }); }
  catch (e) { failed.push(`${f}: ${String(e.stderr).split("\n").slice(0, 4).join(" | ")}`); }
}
if (failed.length) { console.error(failed.join("\n")); process.exit(1); }
console.log(`notifier UI scripts parse: ${files.length} ok`);
