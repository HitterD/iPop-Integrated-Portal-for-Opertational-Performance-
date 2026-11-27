// Run with: node tests/notifier-remote-capture.test.cjs
// Hotfix 1.8.1 (BIGIS #8, prod 2026-11-27): the remote host never shared its screen on PCs with two monitors.
// WebView2 auto-selects the capture source whose title CONTAINS --auto-select-desktop-capture-source. With one monitor
// the source is "Entire screen"; with several they are "Screen 1", "Screen 2"… so "Entire screen" matched nothing:
// getDisplayMedia hung and getUserMedia('screen') failed with "NotAllowedError: Permission denied by user".
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "../src/IPop.Notifier");
const read = f => fs.readFileSync(path.join(root, f), "utf8");

// 1. The capture source must match both namings Chromium uses.
const webHost = read("Windows/WebHost.cs");
const source = webHost.match(/CaptureSource\s*=\s*"([^"]+)"/)?.[1];
assert.ok(source, "WebHost.CaptureSource is defined");
assert.match(webHost, /--auto-select-desktop-capture-source=\\""\s*\+\s*CaptureSource/, "the main environment uses CaptureSource");
assert.match(webHost, /CaptureSourceFor\(int monitorIndex, int monitorCount\) =>\s*monitorCount > 1 \? \$"Screen \{monitorIndex \+ 1\}" : CaptureSource/, "1.8.2: one capture environment per monitor (\"Screen N\")");
for (const title of ["Entire screen", "Screen 1", "Screen 2"]) assert.ok(title.includes(source), `"${source}" matches "${title}"`);
assert.doesNotMatch(webHost, /auto-select-desktop-capture-source=\\"Entire screen\\"/, "no single-monitor-only source");

// 2. The host shares with getDisplayMedia and gives up instead of hanging.
const remote = read("Ui/remote.js").split(/\r?\n/).filter(l => !l.trim().startsWith("//")).join("\n");
assert.doesNotMatch(remote, /chromeMediaSource/, "the whole-desktop getUserMedia path (refused on two monitors) is gone");
assert.match(remote, /getDisplayMedia\(/);
assert.match(remote, /CAPTURE_TIMEOUT_MS/, "a capture that never answers fails");

// 2b. 1.8.2: hardware H.264 — WebView2 encodes 42e01f in software (OpenH264); High profile (64…) uses the GPU.
assert.match(remote, /profile-level-id=64/, "High-profile H.264 is preferred");
assert.match(remote, /setCodecPreferences/);
assert.match(remote, /smooth: \{[^}]*fps: 60/); assert.match(remote, /auto: \{[^}]*fps: 60[^}]*simulcast: true/);

// 3. Allow is answered on its own; a failed start ends the session instead of "Couldn't reach iPop to answer".
const host = read("Remote/RemoteHost.cs");
const answered = host.slice(host.indexOf("ask.Answered +="), host.indexOf("var timer = new System.Windows.Forms.Timer"));
const tryBlock = answered.slice(answered.indexOf("try"), answered.indexOf("catch"));
assert.doesNotMatch(tryBlock, /StartAsync|StartSafeAsync/, "starting the share is outside the answer's try");
assert.match(answered, /StartSafeAsync\(id, initials, unattended: false\)/);
assert.match(host, /ShareDeadline/, "the host ends a share that never starts");
assert.match(host + read("Remote/RemoteHost.Monitor.cs"), /WebViewFailed \+=/, "a hidden page WebView2 refuses ends the session");

// 4. The viewer never waits forever on "Connecting…".
const viewer = read("Ui/remote-viewer.js");
assert.match(viewer, /noScreen = setTimeout\(/);
assert.match(viewer, /clearTimeout\(noScreen\)/);

// 5. No raw .NET "Unhandled exception" dialog: the app catches and logs everything.
const crashes = read("Core/Crashes.cs");
assert.match(crashes, /SetUnhandledExceptionMode\(UnhandledExceptionMode\.CatchException\)/);
assert.match(crashes, /ThreadException/);
assert.match(crashes, /UnobservedTaskException/);
assert.match(read("Program.cs"), /Crashes\.Install\(\);\s*\n\s*ApplicationConfiguration\.Initialize\(\);/, "installed before any window");

console.log("notifier remote capture (hotfix 1.8.1): ok");

// 6. Two sessions in a row: the shared remote tray icon must never be disposed (1.8.0 failed every 2nd session with
//    ObjectDisposedException 'Icon' in UpdateTray, inside StartAsync).
const app = read("Windows/NotifierApp.cs");
assert.doesNotMatch(app, /old != AppIcon\.Icon && old != tray\.Icon\) old\.Dispose\(\)/, "old dispose rule is gone");
assert.match(app, /AppIcon\.IsDisposable\(old\)\) old\.Dispose\(\)/);
assert.match(read("Windows/HtmlWindow.cs"), /IsDisposable\(Icon i\) => !ReferenceEquals\(i, icon\) && !ReferenceEquals\(i, remote\)/);
console.log("notifier remote: second session keeps its tray icon: ok");

// 7. 1.8.2: Chromium's "… is sharing your screen · Stop sharing" bar is hidden, only for this app's own WebView2.
const bar = read("Remote/ScreenShareBar.cs");
assert.match(bar, /title\.EndsWith\(Suffix, StringComparison\.Ordinal\) && title\.Contains\("notifier\.ipop"/);
assert.match(bar, /ParentOf\(p\.Id\) == me/, "only msedgewebview2 children of the Notifier");
assert.match(webHost, /ScreenShareBar\.HideSoon\(\)/);
// Monitor 1 ↔ 2: the picker is live again, without "All".
const monitors = read("Remote/Monitors.cs");
assert.doesNotMatch(monitors, /name = "All"/);
assert.match(read("Remote/RemoteHost.Monitor.cs"), /SourceFor\(monitor\)/);
console.log("notifier remote 1.8.2 (monitor switch, HW H.264, share bar): ok");
