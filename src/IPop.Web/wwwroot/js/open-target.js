// Shared by /open.html, session-lock.js and the installed app's launch queue: turns an untrusted
// "to" value (from a Notifier link, a web+ipop: URL or another tab) into a same-origin iPop path.
// Anything else — other sites, protocol-relative //host, javascript:, /open.html itself — becomes
// the dashboard, so a crafted link can never send iPop elsewhere or loop.
(function (root) {
  "use strict";
  var FALLBACK = "/dashboard";

  function ipopOpenTarget(to, origin) {
    origin = origin || (root.location && root.location.origin);
    if (typeof to !== "string" || !to || to.length > 2048) return FALLBACK;
    if (to.charAt(0) !== "/" || to.charAt(1) === "/" || to.charAt(1) === "\\") return FALLBACK;
    var u;
    try { u = new URL(to, origin); } catch (e) { return FALLBACK; }
    if (u.origin !== origin) return FALLBACK;
    if (/^\/open(\.html)?$/i.test(u.pathname)) return FALLBACK;
    return u.pathname + u.search + u.hash;
  }

  /** web+ipop://open?to=/chat/… or https://host/open.html?to=… → the target path, or null. */
  function ipopTargetFromLaunch(href, origin) {
    var u;
    try { u = new URL(href); } catch (e) { return null; }
    var to = u.searchParams.get("to");
    var h = u.searchParams.get("h");
    if (!to && h) { try { to = new URL(h).searchParams.get("to"); } catch (e) { to = null; } }
    return to ? ipopOpenTarget(to, origin) : null;
  }

  root.ipopOpenTarget = ipopOpenTarget;
  root.ipopTargetFromLaunch = ipopTargetFromLaunch;
  if (typeof module !== "undefined" && module.exports) module.exports = { ipopOpenTarget: ipopOpenTarget, ipopTargetFromLaunch: ipopTargetFromLaunch };
})(typeof window !== "undefined" ? window : globalThis);
