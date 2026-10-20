// iPop service worker — Web Push (Windows toasts) + app-shell caching for the installed app.
// Caching rules (Wave 1): HTML network-first (4 s) -> cached shell -> offline.html; fingerprinted
// _framework files cache-first; ?v= static files stale-while-revalidate; never /api, /hubs, auth,
// uploads, media, cross-origin or no-store requests (version-check.js keeps seeing every deploy).
// Kill switch: set wwwroot/sw-kill to {"off":true} AND bump SW_VERSION (so browsers install the changed
// worker); on activate it sees the switch, drops the ipop-shell-/ipop-fw- caches and unregisters itself.
// Payload (PushDispatcher.PushPayload): { kind, title, body, url, tag, requireInteraction, callId, actionToken, apiBase }
//   kind: "notif" | "chat" (grouped per room tag) | "call" (sticky, Answer/Decline) | "close" (dismiss tag) | "test"

const ICON = "img/brand/icon-192.png";
const PUSH_STATE = "ipop-push-state-v1";
const CHAT_ALERT_COOLDOWN = 30000;
const CALL_VIBRATE = [800, 400, 800, 400, 800, 1200, 800, 400, 800, 400, 800];

async function pushState(key, value) {
  const cache = await caches.open(PUSH_STATE);
  const url = new URL("__push_state__/" + encodeURIComponent(key), self.registration.scope).href;
  if (value === undefined) {
    const response = await cache.match(url);
    return response ? response.json() : null;
  }
  await cache.put(url, new Response(JSON.stringify(value), { headers: { "Content-Type": "application/json" } }));
  if (key.startsWith("alert:")) {
    const keys = await cache.keys();
    const alerts = keys.filter(r => new URL(r.url).pathname.includes("/alert%3A"));
    for (const old of alerts.slice(0, Math.max(0, alerts.length - 100))) await cache.delete(old);
  }
}

// This is a repair marker, never a credential or offline page cache. An authenticated page
// reconciles the subscription when opened; browsers may not fire this event consistently.
self.addEventListener("pushsubscriptionchange", e => e.waitUntil((async () => {
  await pushState("repair", { needed: true, at: Date.now() });
  for (const c of await self.clients.matchAll({ type: "window", includeUncontrolled: true }))
    c.postMessage({ type: "ipop-push-repair", v: 1, reason: "subscriptionchange" });
})()));

self.addEventListener("message", e => e.waitUntil((async () => {
  const d = e.data;
  if (!d || d.v !== 1) return;
  // Only this origin's controlled pages may alter preferences/binding; no auth tokens live here.
  if (!e.source?.url || new URL(e.source.url).origin !== self.location.origin) return;
  let reply;
  if (d.type === "ipop-push-preferences") {
    await pushState("prefs", { soundEnabled: d.soundEnabled === true, volume: Number.isFinite(d.volume) ? Math.max(0, Math.min(1, d.volume)) : null });
  } else if (d.type === "ipop-push-binding") {
    const old = await pushState("binding");
    const valid = Number.isSafeInteger(d.gen) && d.gen >= 0 && (d.acct === null || /^[a-f0-9]{16}$/.test(d.acct));
    if (valid && (!old || d.gen >= old.gen)) {
      await pushState("binding", { acct: d.acct, gen: d.gen, optedOut: d.optedOut === true });
      if (d.acct === null || d.optedOut === true || old?.acct !== d.acct)
        for (const n of await self.registration.getNotifications()) n.close();
    }
    reply = { type: "ipop-push-binding", v: 1, schema: 1, gen: valid ? d.gen : null, accepted: valid && (!old || d.gen >= old.gen) };
  } else if (d.type === "ipop-push-repair-done") {
    await pushState("repair", { needed: false });
  } else if (d.type === "ipop-push-hello") {
    reply = { type: "ipop-push-hello", v: 1, schema: 1, repairNeeded: !!(await pushState("repair"))?.needed, lastResult: await pushState("last-result") };
  }
  if (reply && e.ports?.[0]) e.ports[0].postMessage(reply);
})()));

// ───────────────────────── App-shell cache ─────────────────────────
// Bump SW_VERSION on every deploy that changes caching; activate deletes older ipop-shell-/ipop-fw- caches.
const SW_VERSION = "w2-20261020push";
const SHELL = "ipop-shell-" + SW_VERSION;
const FW = "ipop-fw-" + SW_VERSION;
let NAV_TIMEOUT_MS = 4000;
let FW_LIMIT = 400;
let killed = false;

const NEVER = /^\/(api|hubs|livekit|onlyoffice|meet|download|downloads|uploads|\.well-known|connect)(\/|$)|^\/(sw\.js|sw-kill)$/i;
// .NET 10 fingerprints: name.<10 lowercase alphanumerics>.ext (dotnet.m4zqvqaj3u.js, IPop.Core.lqpourngtc.wasm);
// blazor.webassembly.js / dotnet.js have no fingerprint and stay network-first.
const FINGERPRINTED = /^\/_framework\/.+\.[a-z0-9]{10}\.(js|wasm|dat|json|pdb)$/;
const VERSIONED = /^\/(css|js|fonts|img|lib|sounds|stickers|images)\//i;
// Files the shell needs to boot that carry no ?v=: network-first, the cached copy only when offline.
const BOOT = /^\/(css|js|lib|_content)\/.+\.(css|js)$|^\/appsettings\.json$/i;

/** "none" | "navigate" | "cache-first" | "network-first" | "swr" — what the fetch handler does. */
function routeFor(req) {
  if (killed || req.method !== "GET") return "none";
  if (req.cache === "no-store" || req.cache === "only-if-cached") return "none";
  const h = req.headers;
  if (h && (h.get("range") || h.get("authorization"))) return "none";
  let url;
  try { url = new URL(req.url); } catch { return "none"; }
  if (url.origin !== self.location.origin) return "none";
  const p = url.pathname;
  if (NEVER.test(p)) return "none";
  if (req.mode === "navigate") return "navigate";
  if (p.startsWith("/_framework/")) return FINGERPRINTED.test(p) ? "cache-first" : "network-first";
  if (VERSIONED.test(p) && (url.searchParams.has("v") || /^\/img\/brand\//i.test(p))) return "swr";
  if (BOOT.test(p)) return "network-first";
  return "none";
}

const ok = res => res && res.ok && res.status === 200 && res.type !== "opaque";

async function put(cacheName, key, res) {
  try { const c = await caches.open(cacheName); await c.put(key, res); } catch { /* quota: just skip */ }
}

async function trim(cacheName, limit) {
  try {
    const c = await caches.open(cacheName);
    const keys = await c.keys();
    for (const k of keys.slice(0, Math.max(0, keys.length - limit))) await c.delete(k);
  } catch { }
}

function withTimeout(promise, ms) {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error("timeout")), ms);
    promise.then(v => { clearTimeout(t); resolve(v); }, e => { clearTimeout(t); reject(e); });
  });
}

const shellKey = () => new URL("index.html", self.registration.scope).href;
const offlineKey = () => new URL("offline.html", self.registration.scope).href;

async function navigate(req) {
  // Every route is the same SPA document; fetch it as index.html so one cache entry serves all.
  try {
    const res = await withTimeout(fetch(shellKey(), { cache: "no-cache", credentials: "same-origin" }), NAV_TIMEOUT_MS);
    if (ok(res)) { await put(SHELL, shellKey(), res.clone()); return res; }
    if (res) return res;
  } catch { /* offline or slow: fall through to the cached shell */ }
  const c = await caches.open(SHELL);
  return (await c.match(shellKey())) || (await c.match(offlineKey()))
    || new Response("<!doctype html><title>iPop</title><p>You're offline — iPop needs a connection.</p>",
                    { status: 503, headers: { "Content-Type": "text/html; charset=utf-8" } });
}

async function cacheFirst(req) {
  const c = await caches.open(FW);
  const hit = await c.match(req.url);
  if (hit) return hit;
  const res = await fetch(req);
  if (ok(res)) { await put(FW, req.url, res.clone()); trim(FW, FW_LIMIT); }
  return res;
}

async function networkFirst(req) {
  try {
    const res = await fetch(req);
    if (ok(res)) await put(SHELL, req.url, res.clone());
    return res;
  } catch (e) {
    const hit = await (await caches.open(SHELL)).match(req.url);
    if (hit) return hit;
    throw e;
  }
}

async function staleWhileRevalidate(req, event) {
  const c = await caches.open(SHELL);
  const hit = await c.match(req.url);
  const refresh = fetch(req).then(async res => { if (ok(res)) await put(SHELL, req.url, res.clone()); return res; });
  if (hit) {
    const bg = refresh.catch(() => { });
    if (event && event.waitUntil) event.waitUntil(bg);
    return hit;
  }
  return refresh;
}

self.addEventListener("fetch", e => {
  const route = routeFor(e.request);
  if (route === "none") return;
  if (route === "navigate") e.respondWith(navigate(e.request));
  else if (route === "cache-first") e.respondWith(cacheFirst(e.request));
  else if (route === "network-first") e.respondWith(networkFirst(e.request));
  else e.respondWith(staleWhileRevalidate(e.request, e));
});

async function precacheOffline() {
  try {
    const res = await fetch(offlineKey(), { cache: "no-cache" });
    if (ok(res)) await put(SHELL, offlineKey(), res);
  } catch { }
}

async function killSwitchOn() {
  try {
    const res = await fetch(new URL("sw-kill", self.registration.scope).href, { cache: "no-store" });
    if (!res.ok) return false;
    const j = await res.json();
    return j && j.off === true;
  } catch { return false; }
}

async function onActivate() {
  if (await killSwitchOn()) {
    killed = true;
    for (const n of await caches.keys()) if (/^ipop-(shell|fw)-/.test(n)) await caches.delete(n);
    await self.registration.unregister();
    return;
  }
  for (const n of await caches.keys())
    if (/^ipop-(shell|fw)-/.test(n) && n !== SHELL && n !== FW) await caches.delete(n);
  await self.clients.claim();
}

self.addEventListener("install", e => { self.skipWaiting(); if (e && e.waitUntil) e.waitUntil(precacheOffline()); });
self.addEventListener("activate", e => e.waitUntil(onActivate()));

self.addEventListener("push", e => e.waitUntil(onPush(e)));

async function onPush(e) {
  let d;
  try { d = e.data.json(); } catch { return; }

  if (d.kind === "close") {
    for (const n of await self.registration.getNotifications({ tag: d.tag })) n.close();
    return;
  }

  const binding = await pushState("binding");
  if (binding?.optedOut || (d.acct && (!binding?.acct || binding.acct !== d.acct))) return;
  const prefs = await pushState("prefs");

  // Someone is looking at iPop right now: the in-app banner / call modal covers it.
  // The test toast is always shown — it is sent from a click in Settings.
  const wins = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  if (d.kind !== "test" && wins.some(c => c.focused && c.visibilityState === "visible")) {
    await pushState("last-result", { outcome: "suppressed-foreground", at: Date.now(), kind: d.kind });
    return;
  }

  const data = { url: d.url || "/", kind: d.kind, callId: d.callId, token: d.actionToken, apiBase: d.apiBase, more: 0 };
  let body = d.body || "";

  let alert = true;
  // Keep one toast per conversation, but alert again after the cooldown even if a previous
  // toast remains in Notification Center. Remember timing across worker restarts.
  if (d.kind === "chat" && !d.requireInteraction) {
    const last = await pushState("alert:" + d.tag);
    alert = !last || Date.now() - last.at >= CHAT_ALERT_COOLDOWN;
    const prev = await self.registration.getNotifications({ tag: d.tag });
    const more = prev.reduce((n, p) => n + 1 + ((p.data && p.data.more) || 0), 0);
    if (more > 0) {
      data.more = more;
      body = `${body}\n(+${more} more)`;
    }
  }

  const options = {
    body,
    tag: d.tag,
    renotify: alert && !!d.tag,
    requireInteraction: !!d.requireInteraction,
    icon: ICON,
    badge: ICON,
    timestamp: Date.now(),
    data,
    // Background windows are not proof the user heard an in-app sound. Keep burst replacements
    // quiet, but permit the first/cooldown alert even with a minimized iPop window.
    silent: !alert || (d.kind !== "test" && (prefs?.soundEnabled === false || prefs?.volume === 0)),
  };
  if (d.kind === "call") {
    options.actions = [{ action: "jawab", title: "Answer" }, { action: "tolak", title: "Decline" }];
    options.renotify = true;
    // Phones ignore requireInteraction and play the toast sound once. A long ring-like
    // vibration makes an incoming call noticeable in a pocket (Android; ignored elsewhere).
    options.vibrate = CALL_VIBRATE;
    options.silent = false;
  } else if (!options.silent) {
    options.vibrate = [200, 100, 200];
  }
  try {
    await self.registration.showNotification(d.title || "iPop", options);
    await pushState("last-result", { outcome: "notification-created", at: Date.now(), kind: d.kind });
  } catch {
    await pushState("last-result", { outcome: "notification-failed", at: Date.now(), kind: d.kind });
    for (const c of wins) c.postMessage({ type: "ipop-push-repair", v: 1, reason: "notification-failed" });
    return;
  }
  if (d.kind === "chat" && !d.requireInteraction && alert)
    await pushState("alert:" + d.tag, { at: Date.now() });
}

self.addEventListener("notificationclick", e => {
  e.notification.close();
  e.waitUntil(onClick(e.notification.data || {}, e.action));
});

async function onClick(d, action) {
  if (d.kind === "call" && action === "tolak") {
    if (!d.token) return;
    const base = d.apiBase || self.location.origin;
    try {
      await fetch(`${base}/api/push/call-action`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token: d.token, action: "decline" }),
      });
    } catch { /* the ring times out on its own */ }
    return;
  }

  // "Jawab" (or a click on the call toast) opens /calls?answer=… — MainLayout accepts it.
  const url = d.kind === "call" && d.callId ? `/calls?answer=${d.callId}` : (d.url || "/");
  const wins = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  const win = wins.find(c => new URL(c.url).origin === self.location.origin);
  if (win) {
    try { await win.focus(); } catch { }
    win.postMessage({ type: "ipop-open", url });
    return;
  }
  await self.clients.openWindow(new URL(url, self.registration.scope).href);
}
