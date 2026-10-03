// iPop service worker — Web Push only (Windows toasts). No fetch handler, no offline cache:
// the app always loads from the network exactly as before.
// Payload (PushDispatcher.PushPayload): { kind, title, body, url, tag, requireInteraction, callId, actionToken, apiBase }
//   kind: "notif" | "chat" (grouped per room tag) | "call" (sticky, Answer/Decline) | "close" (dismiss tag) | "test"

const ICON = "img/brand/icon-192.png";
const PUSH_STATE = "ipop-push-state-v1";
const CHAT_ALERT_COOLDOWN = 30000;

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

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", e => e.waitUntil(self.clients.claim()));

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
