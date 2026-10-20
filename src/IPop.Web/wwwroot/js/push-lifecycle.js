// iPop Web Push lifecycle (page side). PushState.cs drives it; sw.js shows the toasts.
//
// What lives here and not in C#:
//  - typed, sanitised registration errors (no endpoint, keys or raw browser text ever leave);
//  - the cross-tab "intent" (opt-out / signed-out / which account owns this browser's push),
//    stamped with a generation that only ever grows, shared through localStorage + BroadcastChannel;
//  - the service-worker message contract v1 (hello / binding / preferences / repair);
//  - bounded triggers (focus, online, visible, every 30 min) that ask .NET to re-check.
//
// One push subscription exists per browser profile + origin, shared by every tab. So opt-out
// is per browser profile, not per account, and the last account to sign in owns it.
//
// SW contract v1. Every message has v:1; unknown types and versions are ignored.
//  page→SW {type:'ipop-push-hello', v}                       + port → {type:'ipop-push-hello', v, schema, repairNeeded}
//  page→SW {type:'ipop-push-binding', v, acct|null, gen, optedOut} + port → {type:'ipop-push-binding', v, schema, gen, accepted}
//  page→SW {type:'ipop-push-preferences', v, soundEnabled, volume|null}
//  page→SW {type:'ipop-push-repair-done', v}
//  SW→page {type:'ipop-push-repair', v, reason}
// acct = the first 16 hex chars of SHA-256("ipop-push:" + lowercase user GUID). The raw GUID is never stored.
(function (root) {
  "use strict";
  const V = 1;
  const INTENT_KEY = "ipop.push.intent";
  const GEN_KEY = "ipop.push.gen";
  const REG_KEY = "ipop.push.reg";
  const LEGACY_OFF_KEY = "ipop-push-off";
  const CHANNEL = "ipop-push";
  const HELLO_TIMEOUT_MS = 1500;
  const PERIODIC_MS = 30 * 60 * 1000;

  // Fixed text per code. The browser's own message is never forwarded, because some engines put the push service URL in it.
  const MESSAGES = {
    unsupported: "This browser does not support Web Push.",
    insecure: "Windows notifications need the secure (https) iPop address.",
    permission_denied: "Notifications are blocked for iPop in this browser's site settings.",
    permission_default: "Notification permission has not been granted yet.",
    no_key: "The server has no push key configured.",
    sw_failed: "This phone does not trust the company certificate yet, so notifications cannot start. Install it from ipop.santos.co.id/app (Android: step 1), then reopen iPop.",
    subscribe_abort: "The browser's push service refused the registration (AbortError). Check that background apps and Google services are allowed, then try again.",
    subscribe_not_allowed: "The browser did not allow push registration.",
    subscribe_invalid_state: "The background worker was not ready; it will retry.",
    subscribe_not_supported: "Push registration is not supported in this browser mode (for example private or kiosk mode).",
    subscribe_failed: "The browser could not register for push.",
  };
  const err = code => ({ ok: false, code, message: MESSAGES[code] || MESSAGES.subscribe_failed });

  function mapSubscribeError(e) {
    switch (e && e.name) {
      case "AbortError": return err("subscribe_abort");
      case "NotAllowedError": return err("subscribe_not_allowed");
      case "InvalidStateError": return err("subscribe_invalid_state");
      case "NotSupportedError": return err("subscribe_not_supported");
      default: return err("subscribe_failed");
    }
  }

  const ls = () => { try { return root.localStorage; } catch { return null; } };
  const readJson = k => { try { const v = ls()?.getItem(k); return v ? JSON.parse(v) : null; } catch { return null; } };
  const writeJson = (k, v) => { try { v == null ? ls()?.removeItem(k) : ls()?.setItem(k, JSON.stringify(v)); } catch { } };

  function supported() {
    const n = root.navigator;
    return !!(root.isSecureContext && n && "serviceWorker" in n && "PushManager" in root && "Notification" in root);
  }

  function b64urlToBytes(s) {
    const bin = root.atob(s.replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(bin, c => c.charCodeAt(0));
  }
  function sameKey(a, b) {
    if (!a) return false;
    const x = new Uint8Array(a);
    return x.length === b.length && x.every((v, i) => v === b[i]);
  }

  async function sha16(text) {
    const data = new TextEncoder().encode(text);
    const buf = await root.crypto.subtle.digest("SHA-256", data);
    return Array.from(new Uint8Array(buf).slice(0, 8), b => b.toString(16).padStart(2, "0")).join("");
  }

  const P = {
    V,
    _reg: null,
    _dotnet: null,
    _channel: null,
    _timers: [],
    _swSchema: 0,

    // ── Service worker ──
    async swRegistration() {
      if (!supported()) return null;
      // Share ipop.js's single registration promise when it is there.
      if (root.ipop && typeof root.ipop._serviceWorker === "function") return await root.ipop._serviceWorker();
      if (!P._reg) {
        const n = root.navigator;
        const url = new URL("sw.js", root.document.baseURI).href;
        P._reg = n.serviceWorker.register(url, { scope: root.document.baseURI })
          .then(() => n.serviceWorker.ready)
          .catch(() => { P._reg = null; return null; });
      }
      return await P._reg;
    },

    async _post(msg, expectReply) {
      const reg = await P.swRegistration();
      const target = reg && (reg.active || root.navigator.serviceWorker.controller);
      if (!target) return null;
      if (!expectReply) { try { target.postMessage(msg); } catch { } return null; }
      return await new Promise(resolve => {
        let done = false;
        const ch = new root.MessageChannel();
        const t = root.setTimeout(() => { if (!done) { done = true; resolve(null); } }, HELLO_TIMEOUT_MS);
        ch.port1.onmessage = e => { if (!done) { done = true; root.clearTimeout(t); resolve(e.data || null); } };
        try { target.postMessage(msg, [ch.port2]); } catch { done = true; root.clearTimeout(t); resolve(null); }
      });
    },

    // An old worker that does not answer counts as schema 0. Its bindingGen seeds our counter so a cleared localStorage cannot lock us out.
    async hello() {
      const r = await P._post({ type: "ipop-push-hello", v: V }, true);
      if (!r || r.type !== "ipop-push-hello") { P._swSchema = 0; return { schema: 0, repairNeeded: false, lastResult: null }; }
      P._swSchema = r.schema | 0;
      if (typeof r.bindingGen === "number") P._seedGen(r.bindingGen);
      const lr = r.lastResult;
      const lastResult = lr && typeof lr.outcome === "string"
        ? { outcome: String(lr.outcome).slice(0, 40), at: Number(lr.at) || 0, kind: lr.kind ? String(lr.kind).slice(0, 20) : null }
        : null;
      return { schema: P._swSchema, repairNeeded: !!r.repairNeeded, lastResult };
    },
    // Returns true only when the worker confirmed it persisted this binding.
    async sendBinding(acct, gen, optedOut) {
      const r = await P._post({ type: "ipop-push-binding", v: V, acct: acct || null, gen, optedOut: !!optedOut }, true);
      return !!(r && r.type === "ipop-push-binding" && r.gen === gen && r.accepted === true);
    },
    async sendPreferences(soundEnabled, volume) {
      const vol = typeof volume === "number" && isFinite(volume) ? Math.min(1, Math.max(0, volume)) : null;
      await P._post({ type: "ipop-push-preferences", v: V, soundEnabled: !!soundEnabled, volume: vol }, false);
    },
    // The page-sound gate in sounds.js reads this. It is a plain flag that holds no auth data.
    setBackgroundHandoff(on) { root.ipopPushBackgroundHandoff = !!on; },
    async repairDone() { await P._post({ type: "ipop-push-repair-done", v: V }, false); },

    // ── State / subscribe ──
    async state() {
      const secure = !!root.isSecureContext;
      const ok = supported();
      let endpoint = null;
      if (ok && root.Notification.permission === "granted") {
        try { const reg = await P.swRegistration(); endpoint = (reg && (await reg.pushManager.getSubscription()))?.endpoint || null; } catch { }
      }
      return { secure, notify: ok ? root.Notification.permission : "unsupported", endpoint, swSchema: P._swSchema };
    },

    // Never prompts. Returns {ok:true, endpoint, p256dh, auth, created} or a typed {ok:false, code, message}.
    async subscribe(publicKey) {
      if (!root.isSecureContext) return err("insecure");
      if (!supported()) return err("unsupported");
      const perm = root.Notification.permission;
      if (perm === "denied") return err("permission_denied");
      if (perm !== "granted") return err("permission_default");
      if (!publicKey) return err("no_key");
      const reg = await P.swRegistration();
      if (!reg) return err("sw_failed");
      let key;
      try { key = b64urlToBytes(publicKey); } catch { return err("no_key"); }
      let created = false;
      try {
        let sub = await reg.pushManager.getSubscription();
        // A subscription for another VAPID key can never receive our pushes, so replace it. The key comes from a successful /config read.
        if (sub && sub.options && sub.options.applicationServerKey && !sameKey(sub.options.applicationServerKey, key)) {
          try { await sub.unsubscribe(); } catch { }
          sub = null;
        }
        if (!sub) { sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key }); created = true; }
        const j = sub.toJSON();
        if (!j || !j.endpoint || !j.keys || !j.keys.p256dh || !j.keys.auth) return err("subscribe_failed");
        return { ok: true, endpoint: j.endpoint, p256dh: j.keys.p256dh, auth: j.keys.auth, created };
      } catch (e) { return mapSubscribeError(e); }
    },

    // Drops this browser's subscription. Returns its endpoint (or null) so the API can forget it too.
    async unsubscribe() {
      try {
        const reg = await P.swRegistration();
        const sub = reg && (await reg.pushManager.getSubscription());
        if (!sub) return null;
        const endpoint = sub.endpoint;
        await sub.unsubscribe();
        return endpoint;
      } catch { return null; }
    },

    // ── Account hash + registration memo (hashes only, never raw ids or endpoints) ──
    acct: userId => userId ? sha16("ipop-push:" + String(userId).toLowerCase()) : Promise.resolve(null),
    endpointDigest: endpoint => endpoint ? sha16("ipop-ep:" + endpoint) : Promise.resolve(null),
    lastRegistration: () => readJson(REG_KEY),
    rememberRegistration: (acct, epDigest, id) => writeJson(REG_KEY, acct ? { acct, ep: epDigest, at: Date.now(), id: id || null } : null),

    // ── Cross-tab intent with a global monotonic generation ──
    _seedGen(g) {
      const cur = parseInt(ls()?.getItem(GEN_KEY) || "0", 10) || 0;
      if (g > cur) try { ls()?.setItem(GEN_KEY, String(g)); } catch { }
    },
    _bumpGen() {
      const cur = parseInt(ls()?.getItem(GEN_KEY) || "0", 10) || 0;
      const g = Math.max(Date.now(), cur + 1);
      try { ls()?.setItem(GEN_KEY, String(g)); } catch { }
      return g;
    },
    async nextGen() {
      const locks = root.navigator && root.navigator.locks;
      if (locks && locks.request) {
        try { return await locks.request("ipop-push-gen", () => P._bumpGen()); } catch { }
      }
      return P._bumpGen();
    },
    currentGen: () => parseInt(ls()?.getItem(GEN_KEY) || "0", 10) || 0,

    // {gen, acct, optedOut}. Before the first intent is written, the legacy ipop-push-off flag is honoured.
    intent() {
      const i = readJson(INTENT_KEY);
      if (i && typeof i.gen === "number") return { gen: i.gen, acct: i.acct || null, optedOut: !!i.optedOut };
      return { gen: 0, acct: null, optedOut: ls()?.getItem(LEGACY_OFF_KEY) === "1" };
    },
    // Records a new intent, tells the other tabs, and returns its generation.
    async setIntent(acct, optedOut) {
      const gen = await P.nextGen();
      const i = { gen, acct: acct || null, optedOut: !!optedOut };
      writeJson(INTENT_KEY, i);
      try { optedOut ? ls()?.setItem(LEGACY_OFF_KEY, "1") : ls()?.removeItem(LEGACY_OFF_KEY); } catch { }
      try { P._channel && P._channel.postMessage({ type: "intent", ...i }); } catch { }
      return gen;
    },
    // A reconcile started at `gen` may commit only while nothing newer has been decided.
    isCurrent: gen => P.intent().gen === gen,

    // ── Wiring to .NET ──
    start(dotnet) {
      P.stop();
      P._dotnet = dotnet;
      const call = (m, ...a) => { try { P._dotnet && P._dotnet.invokeMethodAsync(m, ...a); } catch { } };
      const onIntent = () => call("OnPushIntentChanged");
      if (typeof root.BroadcastChannel === "function") {
        P._channel = new root.BroadcastChannel(CHANNEL);
        P._channel.onmessage = e => { if (e.data && e.data.type === "intent") onIntent(); };
      }
      const onStorage = e => { if (e.key === INTENT_KEY) onIntent(); };
      const onFocus = () => call("OnPushCheck", "focus");
      const onOnline = () => call("OnPushCheck", "online");
      const onVisible = () => { if (root.document.visibilityState === "visible") call("OnPushCheck", "visible"); };
      const onSwMessage = e => {
        if (e.data && e.data.type === "ipop-push-repair" && (e.data.v | 0) >= 1) call("OnPushRepair");
      };
      root.addEventListener("storage", onStorage);
      root.addEventListener("focus", onFocus);
      root.addEventListener("online", onOnline);
      root.document.addEventListener("visibilitychange", onVisible);
      const sw = root.navigator && root.navigator.serviceWorker;
      if (sw) sw.addEventListener("message", onSwMessage);
      const periodic = root.setInterval(() => call("OnPushCheck", "periodic"), PERIODIC_MS);
      P._timers.push(() => {
        root.removeEventListener("storage", onStorage);
        root.removeEventListener("focus", onFocus);
        root.removeEventListener("online", onOnline);
        root.document.removeEventListener("visibilitychange", onVisible);
        if (sw) sw.removeEventListener("message", onSwMessage);
        root.clearInterval(periodic);
      });
    },
    stop() {
      for (const t of P._timers.splice(0)) try { t(); } catch { }
      try { P._channel && P._channel.close(); } catch { }
      P._channel = null;
      P._dotnet = null;
    },
  };

  root.ipopPushBackgroundHandoff = false;
  root.ipopPush = P;
  if (typeof module !== "undefined" && module.exports) module.exports = P;
})(typeof window !== "undefined" ? window : globalThis);
