// iPop service worker — Web Push only (Windows toasts). No fetch handler, no offline cache:
// the app always loads from the network exactly as before.
// Payload (PushDispatcher.PushPayload): { kind, title, body, url, tag, requireInteraction, callId, actionToken, apiBase }
//   kind: "notif" | "chat" (grouped per room tag) | "call" (sticky, Answer/Decline) | "close" (dismiss tag) | "test"

const ICON = "img/brand/icon-192.png";

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

  // Someone is looking at iPop right now: the in-app banner / call modal covers it.
  // The test toast is always shown — it is sent from a click in Settings.
  if (d.kind !== "test" && await isLooking()) return;

  const data = { url: d.url || "/", kind: d.kind, callId: d.callId, token: d.actionToken, apiBase: d.apiBase, more: 0 };
  let body = d.body || "";

  // One toast per room: a newer message replaces the old one quietly and counts the rest.
  if (d.kind === "chat" && !d.requireInteraction) {
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
    renotify: false,
    requireInteraction: !!d.requireInteraction,
    icon: ICON,
    badge: ICON,
    timestamp: Date.now(),
    data,
  };
  if (d.kind === "call") {
    options.actions = [{ action: "jawab", title: "Answer" }, { action: "tolak", title: "Decline" }];
    options.renotify = true;
  }
  await self.registration.showNotification(d.title || "iPop", options);
}

async function isLooking() {
  const wins = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  return wins.some(c => c.focused && c.visibilityState === "visible");
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
