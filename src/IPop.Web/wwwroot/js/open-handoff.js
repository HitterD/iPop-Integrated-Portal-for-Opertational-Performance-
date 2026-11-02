// /open.html — the landing page for links from the iPop Notifier (and the web+ipop: protocol of the
// installed app). One message must never mean one more iPop tab: if an iPop tab already owns the
// session, hand it the target over the session channel (js/session-lock.js) and close this tab.
// No iPop tab, or this page runs inside the installed app window: just load the target here.
(function () {
  "use strict";
  var HANDOFF_WAIT_MS = 450;

  var to = window.ipopTargetFromLaunch(location.href) || "/dashboard";
  var standalone = matchMedia("(display-mode: standalone)").matches || matchMedia("(display-mode: window-controls-overlay)").matches;

  function loadHere() { location.replace(to); }

  if (standalone || !("BroadcastChannel" in window)) { loadHere(); return; }

  var id = (crypto.randomUUID && crypto.randomUUID()) || String(Math.random()).slice(2);
  var ch = new BroadcastChannel("ipop-session");
  var settled = false;
  ch.onmessage = function (e) {
    var m = e.data || {};
    if (settled || m.type !== "handoff-ok" || m.to !== id) return;
    settled = true;
    ch.close();
    document.body.classList.add("handed");
    // A tab the OS opened has one history entry, so browsers let the script close it.
    window.close();
  };
  ch.postMessage({ type: "handoff", from: id, url: to });
  setTimeout(function () {
    if (settled) return;
    settled = true;
    ch.close();
    loadHere();
  }, HANDOFF_WAIT_MS);
})();
