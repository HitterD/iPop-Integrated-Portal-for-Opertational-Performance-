// One active iPop tab per browser, like WhatsApp Web. A tab that opens the app "claims" the
// session over a BroadcastChannel; the previously active tab locks itself (hubs off, modal on)
// unless it is in a call, in which case it answers "busy" and the NEW tab yields instead.
// Only MainLayout calls start(), so the ONLYOFFICE editor tab (EmptyLayout) never takes part.
window.ipopSession = {
  _ch: null,
  _dotNet: null,
  _id: null,
  _state: "idle",   // idle | claiming | active | locked
  _busy: false,
  _claimT: 0,
  _lost: false,
  _waiters: {},
  _blinkT: 0,
  _blinkSaved: null,

  /// Resolves "active" (this tab owns the session) or "yield" (another tab is in a call).
  start(dotNet) {
    this._dotNet = dotNet;
    if (!("BroadcastChannel" in window)) { this._state = "active"; return Promise.resolve("active"); }
    if (!this._ch) {
      this._id = (crypto.randomUUID && crypto.randomUUID()) || String(Math.random()).slice(2);
      this._ch = new BroadcastChannel("ipop-session");
      this._ch.onmessage = e => this._onMessage(e.data || {});
    }
    return this.claim(false);
  },

  setBusy(busy) { this._busy = !!busy; },

  isLocked() { return this._state === "locked"; },

  /// Take the session. force=true ends the other tab's call (the "Tetap gunakan di sini" path).
  claim(force) {
    if (!this._ch) return Promise.resolve("active");
    this._state = "claiming";
    this._lost = false;
    this._claimT = Date.now();
    const t = this._claimT;
    this._ch.postMessage({ type: force ? "force" : "claim", from: this._id, t });
    // A busy tab answers within a few ms; a forced release waits for the call to hang up.
    return new Promise(resolve => {
      const done = result => {
        delete this._waiters[t];
        if (result === "active" && this._lost) result = "locked";
        this._state = result === "active" ? "active" : result === "yield" ? "idle" : "locked";
        resolve(result);
      };
      this._waiters[t] = done;
      setTimeout(() => this._waiters[t] && done("active"), force ? 4000 : 450);
    });
  },

  _onMessage(m) {
    if (m.from === this._id) return;
    switch (m.type) {
      case "claim":
      case "force":
        if (this._state === "claiming") {
          // Two tabs opened at once: the newer claim wins, the older one steps back.
          if (m.t > this._claimT || (m.t === this._claimT && m.from > this._id)) this._lost = true;
          return;
        }
        if (this._state !== "active") return;
        if (m.type === "claim" && this._busy) {
          this._ch.postMessage({ type: "busy", from: this._id, to: m.from, t: m.t });
          return;
        }
        this._state = "locked";
        this._dotNet?.invokeMethodAsync("OnSessionTaken", m.type === "force")
          .catch(() => { })
          .finally(() => this._ch.postMessage({ type: "released", from: this._id, to: m.from, t: m.t }));
        return;
      case "busy":
        this._waiters[m.t]?.("yield");
        return;
      case "released":
        this._waiters[m.t]?.("active");
        return;
      case "open":
        // A toast click the service worker delivered to a locked tab.
        if (this._state !== "active" || !m.url) return;
        this.openHere(m.url);
        return;
      case "handoff":
        // /open.html in a fresh tab (a Notifier link): the tab that owns the session takes the target,
        // and the fresh tab closes itself — ten messages must not leave ten iPop tabs behind.
        if (this._state !== "active" || !m.url || !m.from) return;
        this._ch.postMessage({ type: "handoff-ok", from: this._id, to: m.from });
        this.openHere(window.ipopOpenTarget ? ipopOpenTarget(m.url) : m.url);
        return;
    }
  },

  /// Go to a page another tab or the service worker asked for. A browser cannot raise a background
  /// tab from script, so a hidden tab blinks its title until the user looks at it.
  openHere(url) {
    try { window.focus(); } catch { }
    try { Blazor.navigateTo(url); } catch { location.href = url; }
    if (document.visibilityState !== "visible") this._blinkTitle();
  },

  _blinkTitle() {
    if (this._blinkT) return;
    let on = false;
    const stop = () => {
      clearInterval(this._blinkT);
      this._blinkT = 0;
      if (this._blinkSaved != null) document.title = this._blinkSaved;
      this._blinkSaved = null;
      document.removeEventListener("visibilitychange", onVis);
    };
    const onVis = () => { if (document.visibilityState === "visible") stop(); };
    this._blinkT = setInterval(() => {
      on = !on;
      if (on) { this._blinkSaved = document.title; document.title = "● New in iPop"; }
      else if (this._blinkSaved != null) document.title = this._blinkSaved;
    }, 1000);
    document.addEventListener("visibilitychange", onVis);
    setTimeout(stop, 60000);
  },

  forwardOpen(url) { this._ch?.postMessage({ type: "open", from: this._id, url }); },

  /// window.close() only works for script-opened tabs; otherwise leave an empty page behind.
  closeTab() {
    window.close();
    setTimeout(() => { if (!window.closed) location.replace("about:blank"); }, 200);
  },
};
