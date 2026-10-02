// Chat composer drafts (DraftStore.cs). Classic script: window.ipopDrafts.
//
// C# stages the latest wire text per account+room synchronously on every input; this file
// keeps it in a memory stash and writes it to localStorage after a short debounce, and
// synchronously on flush(), pagehide and visibilitychange→hidden, so a tab closed or
// reloaded mid-typing keeps what was typed. Keys: "<base><roomId>", where base is
// "ipop.chat.d2.<nik>."; "<base>index" maps roomId → ISO time for LRU pruning.
(function (root) {
  "use strict";
  const LEGACY = "ipop.chat.draft";   // pre-account drafts ("ipop.chat.draft.<room>" + "ipop.chat.drafts")
  const PREFIX = "ipop.chat.d2.";

  const api = {
    delayMs: 400,
    max: 50,
    _pending: new Map(),  // key -> { base, roomId, text|null }
    _timer: 0,

    _store() { try { return root.localStorage; } catch { return null; } },

    /// Remember the latest text (null = remove) and write it after the debounce.
    stage(base, roomId, text) {
      if (!base || !roomId) return;
      this._pending.set(base + roomId, { base, roomId, text: text == null || text === "" ? null : String(text) });
      if (this._timer) clearTimeout(this._timer);
      this._timer = setTimeout(() => { this._timer = 0; this.flush(); }, this.delayMs);
    },

    /// Stage and write now.
    set(base, roomId, text) { this.stage(base, roomId, text); this.flush(); },

    /// Latest text: the stash first (not yet written), then storage.
    get(base, roomId) {
      if (!base || !roomId) return null;
      const p = this._pending.get(base + roomId);
      if (p) return p.text;
      const s = this._store();
      try { return s ? s.getItem(base + roomId) : null; } catch { return null; }
    },

    /// Writes every staged draft synchronously.
    flush() {
      if (this._timer) { clearTimeout(this._timer); this._timer = 0; }
      if (this._pending.size === 0) return;
      const items = [...this._pending.values()];
      this._pending.clear();
      const s = this._store();
      if (!s) return;
      const touched = new Set();
      for (const p of items) {
        const idx = this._index(s, p.base);
        try {
          if (p.text == null) { s.removeItem(p.base + p.roomId); delete idx[p.roomId]; }
          else { s.setItem(p.base + p.roomId, p.text); idx[p.roomId] = new Date().toISOString(); }
        } catch { /* quota or blocked storage: the C# side still has the composer */ }
        this._saveIndex(s, p.base, idx);
        touched.add(p.base);
      }
      for (const base of touched) this._prune(s, base);
    },

    /// Drops a staged (unwritten) draft and the stored one.
    remove(base, roomId) {
      if (!roomId) return;
      for (const [k, p] of this._pending) if (p.roomId === roomId && (!base || p.base === base)) this._pending.delete(k);
      if (!base) return;
      const s = this._store();
      if (!s) return;
      try { s.removeItem(base + roomId); } catch { }
      const idx = this._index(s, base);
      if (roomId in idx) { delete idx[roomId]; this._saveIndex(s, base, idx); }
    },

    /// Forgets everything staged and not yet written (sign-out: nothing may be flushed later).
    discardAll() {
      if (this._timer) { clearTimeout(this._timer); this._timer = 0; }
      this._pending.clear();
    },

    /// Removes legacy unscoped drafts. They can't be attributed to an account, so never imported.
    purgeLegacy() { this._removePrefixes([LEGACY]); },

    /// Sign-out: every draft of every account on this browser, plus anything staged.
    clearAll() { this.discardAll(); this._removePrefixes([LEGACY, PREFIX]); },

    _removePrefixes(prefixes) {
      const s = this._store();
      if (!s) return;
      try {
        for (let i = s.length - 1; i >= 0; i--) {
          const k = s.key(i);
          if (k && prefixes.some(p => k.startsWith(p))) s.removeItem(k);
        }
      } catch { }
    },

    _index(s, base) {
      try { return JSON.parse(s.getItem(base + "index") || "{}") || {}; } catch { return {}; }
    },
    _saveIndex(s, base, idx) {
      try {
        if (Object.keys(idx).length === 0) s.removeItem(base + "index");
        else s.setItem(base + "index", JSON.stringify(idx));
      } catch { }
    },
    _prune(s, base) {
      const idx = this._index(s, base);
      const rooms = Object.keys(idx);
      if (rooms.length <= this.max) return;
      rooms.sort((a, b) => (idx[b] || "").localeCompare(idx[a] || ""));
      for (const r of rooms.slice(this.max)) { try { s.removeItem(base + r); } catch { } delete idx[r]; }
      this._saveIndex(s, base, idx);
    },
  };

  root.ipopDrafts = api;
  if (typeof root.addEventListener === "function") {
    root.addEventListener("pagehide", () => api.flush());
    if (root.document && typeof root.document.addEventListener === "function")
      root.document.addEventListener("visibilitychange", () => { if (root.document.visibilityState === "hidden") api.flush(); });
  }
})(typeof window !== "undefined" ? window : globalThis);
