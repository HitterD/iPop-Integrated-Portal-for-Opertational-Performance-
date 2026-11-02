// IPop UI helpers — called from Blazor components only.
// Lucide must be re-run after every Blazor render that adds <i data-lucide>.
// NEVER call lucide.createIcons() on the live DOM: it replaces each <i> with a
// fresh <svg>, detaching the <i> nodes Blazor's renderer still tracks. When a
// later render removes one of those <i>s (any @if toggling icon markup — e.g. the
// Mail loader spinner), the renderer calls removeChild on a detached node and the
// page dies with "Cannot read properties of null (reading 'removeChild')".
// icons() renders each icon into a scratch container, then injects the SVG as the
// inner markup of the ORIGINAL <i> — node identity survives re-renders and Blazor's
// tree stays in sync.
window.ipop = {
  // Keep the selection before an emoji picker takes focus. WeakMap keys never outlive inputs.
  _emojiCarets: new WeakMap(),
  emojiSaveCaret(input) {
    if (typeof input === "string") input = document.getElementById(input);
    if (input) this._emojiCarets.set(input, [input.selectionStart ?? input.value.length, input.selectionEnd ?? input.value.length]);
  },
  emojiInsert(input, text) {
    if (typeof input === "string") input = document.getElementById(input);
    if (!input) return "";
    const [start, end] = this._emojiCarets.get(input) ?? [input.selectionStart ?? input.value.length, input.selectionEnd ?? input.value.length];
    input.setRangeText(text, start, end, "end");
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.focus();
    this._emojiCarets.delete(input);
    return input.value;
  },
  // PageCache (C#): removes every last-seen page snapshot on sign-out, for all users of this browser.
  cacheClear(prefix) {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const k = localStorage.key(i);
      if (k && k.startsWith(prefix)) localStorage.removeItem(k);
    }
  },
  // Spell-check: off by default everywhere (the red squiggles distracted people writing in
  // Indonesian). The spellcheck attribute is inherited, so one on <html> covers every
  // input, textarea and contenteditable. Per device, in localStorage.
  spellcheck(on) {
    if (on !== undefined) {
      try { localStorage.setItem("ipop.spellcheck", on ? "1" : "0"); } catch { }
      document.documentElement.setAttribute("spellcheck", on ? "true" : "false");
      document.querySelectorAll(".jodit-wysiwyg").forEach(el => el.setAttribute("spellcheck", on ? "true" : "false"));
    }
    try { return localStorage.getItem("ipop.spellcheck") === "1"; } catch { return false; }
  },
  // Theme: "light" | "dark". Applied as data-theme on <html>, persisted in
  // localStorage so the choice survives reloads. Default: light.
  theme() {
    return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
  },
  themePreference() {
    try { return localStorage.getItem("ipop-theme") || "light"; } catch { return "light"; }
  },
  setTheme(t) {
    if (!["light", "dark", "auto"].includes(t)) t = "light";
    const mq = window.matchMedia?.("(prefers-color-scheme: dark)");
    document.documentElement.setAttribute("data-theme", t === "auto" ? (mq?.matches ? "dark" : "light") : t);
    try { localStorage.setItem("ipop-theme", t); } catch {}
    if (mq && !this._themeWatching) {
      this._themeWatching = true;
      mq.addEventListener("change", () => {
        if (this.themePreference() === "auto") { this.setTheme("auto"); this.syncThemeIcons(this.theme()); }
      });
    }
  },
  toggleTheme() {
    const next = window.ipop.theme() === "dark" ? "light" : "dark";
    window.ipop.setTheme(next);
    window.ipop.syncThemeIcons(next);
    return next;
  },
  syncThemeIcons(theme) {
    // Same rule as icons(): never replace the <i> node Blazor owns — only swap
    // the icon name and let icons() render the new glyph into the SAME node.
    // (replaceWith detached a Blazor-owned node and could desync its render tree.)
    const icon = theme === "dark" ? "sun" : "moon";
    document.querySelectorAll(".theme-btn").forEach(btn => {
      const i = btn.querySelector("i[data-lucide]");
      if (i) { i.setAttribute("data-lucide", icon); delete i.__lucideDone; }
    });
    this.icons();
  },
  restoreTheme() {
    let t = "light";
    try { t = localStorage.getItem("ipop-theme") || "light"; } catch {}
    window.ipop.setTheme(t);
    return t;
  },
  /// Selects the text of the focused input (a fresh "New task" title is typed over).
  selectFocused() { const el = document.activeElement; if (el && typeof el.select === "function") el.select(); },
  icons() {
    // Render every <i data-lucide> icon into its own element, then swap the SVG
    // content into the ORIGINAL <i>. The <i> node itself is never touched by the
    // icon library, so Blazor's renderer can still remove/update it later.
    if (!window.lucide) return;
    const toPascal = s => s.replace(/^([A-Z])|[\s-_]+(\w)/g, (_, a, b) => b ? b.toUpperCase() : a.toLowerCase()).replace(/^./, m => m.toUpperCase());
    const iconMap = window.lucide.icons || window.lucide;
    document.querySelectorAll("i[data-lucide]").forEach(i => {
      const icon = i.getAttribute("data-lucide");
      if (!icon || i.__lucideDone === icon) return;
      const pascal = toPascal(icon);
      const iconDef = iconMap[pascal] || iconMap[icon];
      if (!iconDef) return;
      let svg;
      try { svg = window.lucide.createElement(iconDef); }
      catch { return; } // unknown name / bad icon — leave the placeholder alone
      if (!svg) return;
      // createElement's raw svg carries NO class / data-lucide — the library's own
      // createIcons adds those. Add class="lucide lucide-{name}" plus data-lucide so
      // every CSS rule (.lucide, .member-picker-input > i > .lucide, .spin …) and the
      // dedupe check keep matching exactly as before.
      svg.setAttribute("data-lucide", icon);
      svg.classList.add("lucide", "lucide-" + icon);
      // Copy intent-relevant attrs the <i> carried (style, class) onto the SVG so
      // rendering stays the same as before, then inject as the <i>'s content.
      if (i.getAttribute("style")) svg.setAttribute("style", i.getAttribute("style"));
      const cls = i.getAttribute("class");
      if (cls) { const merged = Array.from(svg.classList); cls.trim().split(/\s+/).filter(Boolean).forEach(c => { if (!merged.includes(c)) svg.classList.add(c); }); }
      i.textContent = "";
      i.appendChild(svg);
      i.__lucideDone = icon;
    });
  },
  // ContextMenu placement: measure the menu, then flip near the viewport edges.
  measure(el) {
    if (!el) return [0, 0];
    const r = el.getBoundingClientRect();
    return [r.width, r.height];
  },
  viewportWidth() { return window.innerWidth; },
  // Projects → Reports: each chart box reports its width so the SVG is drawn in real pixels.
  // dotNet.invokeMethodAsync(method, index, width) per element; unwatchWidth(dotNet) stops it.
  _widthWatch: new Map(),
  watchWidth(dotNet, method, ...els) {
    const old = this._widthWatch.get(dotNet._id);
    if (old) old.disconnect();
    const list = els.filter(e => e instanceof Element);
    if (!list.length || !("ResizeObserver" in window)) return;
    let t;
    const send = () => list.forEach(e => {
      const i = els.indexOf(e);
      if (e.isConnected) dotNet.invokeMethodAsync(method, i, Math.round(e.clientWidth)).catch(() => {});
    });
    const ro = new ResizeObserver(() => { clearTimeout(t); t = setTimeout(send, 60); });
    list.forEach(e => ro.observe(e));
    this._widthWatch.set(dotNet._id, ro);
    send();
  },
  unwatchWidth(dotNet) {
    const ro = this._widthWatch.get(dotNet && dotNet._id);
    if (ro) { ro.disconnect(); this._widthWatch.delete(dotNet._id); }
  },
  viewportHeight() { return window.innerHeight; },
  focus(el) {
    if (el) { el.focus(); el.select(); }
  },
  // Ctrl+K box: arrows move the selection instead of the caret; Tab is swallowed only while a
  // person row is active (data-tab-trap="1"). Every other key types normally.
  cpBindInput(el) {
    if (!el || el._cpBound) return;
    el._cpBound = true;
    el.addEventListener("keydown", e => {
      if (e.isComposing) return;
      if (e.key === "ArrowDown" || e.key === "ArrowUp") e.preventDefault();
      else if (e.key === "Tab" && el.getAttribute("data-tab-trap") === "1") e.preventDefault();
    });
  },
  // Ctrl+K: a recent search fills the box without waiting for Blazor's next input event.
  cpSetValue(el, value) {
    if (!el) return;
    el.value = value ?? "";
    el.focus();
    try { el.setSelectionRange(el.value.length, el.value.length); } catch { }
  },
  shake(el) {
    if (!el) return;
    el.classList.remove("shake");
    void el.offsetWidth; // restart animation
    el.classList.add("shake");
  },
  // Spec §3.3 — browser-sourced fingerprint; not a hardware hash (WASM can't read one).
  fingerprint() {
    const d = navigator.userAgentData || {};
    return [
      (d.brands || []).map(b => b.brand).join("+") || navigator.userAgent.slice(0, 40),
      d.platform || navigator.platform,
      d.mobile ? "mobile" : "desktop",
      navigator.language,
      screen.width + "x" + screen.height
    ].join("|");
  },
  visibility() {
    return document.visibilityState;
  },

  // Chat read receipts: report whether this tab is visible AND its window focused, so a
  // background tab or a minimized window never marks messages read.
  watchAttention(dotnet) {
    ipop.unwatchAttention();
    let last = null;
    const report = () => {
      const now = document.visibilityState === "visible" && document.hasFocus();
      if (now === last) return;
      last = now;
      dotnet.invokeMethodAsync("OnAttentionChanged", now).catch(() => {});
    };
    // Focus moving into an iframe (embedded viewer) blurs the window; re-check after it settles.
    const later = () => setTimeout(report, 0);
    window.addEventListener("focus", report);
    window.addEventListener("blur", later);
    document.addEventListener("visibilitychange", report);
    ipop._attention = { report, later };
    report();
    return last;
  },
  unwatchAttention() {
    const a = ipop._attention;
    if (!a) return;
    window.removeEventListener("focus", a.report);
    window.removeEventListener("blur", a.later);
    document.removeEventListener("visibilitychange", a.report);
    ipop._attention = null;
  },

  // ── Presence idle detection ──
  // Away = the computer has been idle 10 minutes or its screen is locked. The Idle Detection
  // API (Chrome/Edge, secure context, one-time permission) sees system-wide input; without it
  // we fall back to input inside iPop tabs. Changes are pushed to AuthState.OnIdleChanged.
  _idle: { ref: null, idle: false, last: Date.now(), timer: null, detector: null, abort: null },
  idleThresholdMs: 10 * 60 * 1000,
  presenceStart(dotnet) {
    const s = ipop._idle;
    ipop.presenceStop();
    s.ref = dotnet;
    s.last = Date.now();
    s.idle = false;
    const touch = () => {
      s.last = Date.now();
      if (!s.detector && s.idle) ipop._setIdle(false);
    };
    s.onActivity = touch;
    ["pointerdown", "pointermove", "keydown", "wheel", "touchstart"].forEach(t =>
      window.addEventListener(t, touch, { passive: true, capture: true }));
    s.onVis = () => { if (document.visibilityState === "visible") touch(); };
    document.addEventListener("visibilitychange", s.onVis);
    // Share activity across tabs so a busy second tab keeps the first one "on" too.
    try {
      s.channel = new BroadcastChannel("ipop-activity");
      s.channel.onmessage = () => { s.last = Date.now(); if (!s.detector && s.idle) ipop._setIdle(false); };
      let lastPost = 0;
      s.onActivityShare = () => { const n = Date.now(); if (n - lastPost > 30000) { lastPost = n; s.channel.postMessage(1); } };
      ["pointerdown", "keydown", "pointermove"].forEach(t => window.addEventListener(t, s.onActivityShare, { passive: true, capture: true }));
    } catch { /* BroadcastChannel unsupported: per-tab only */ }
    s.timer = setInterval(() => {
      if (!s.detector && !s.idle && Date.now() - s.last >= ipop.idleThresholdMs) ipop._setIdle(true);
    }, 15000);
    ipop._startIdleDetector();
  },
  presenceStop() {
    const s = ipop._idle;
    if (s.onActivity) ["pointerdown", "pointermove", "keydown", "wheel", "touchstart"].forEach(t =>
      window.removeEventListener(t, s.onActivity, { capture: true }));
    if (s.onActivityShare) ["pointerdown", "keydown", "pointermove"].forEach(t =>
      window.removeEventListener(t, s.onActivityShare, { capture: true }));
    if (s.onVis) document.removeEventListener("visibilitychange", s.onVis);
    if (s.channel) { try { s.channel.close(); } catch { } s.channel = null; }
    if (s.timer) clearInterval(s.timer);
    if (s.abort) s.abort.abort();
    s.timer = s.abort = s.detector = s.onActivity = s.onActivityShare = s.onVis = null;
    s.ref = null;
  },
  presenceIdle() { return ipop._idle.idle; },
  _setIdle(idle) {
    const s = ipop._idle;
    if (s.idle === idle) return;
    s.idle = idle;
    if (s.ref) s.ref.invokeMethodAsync("OnIdleChanged", idle).catch(() => { });
  },
  async _startIdleDetector() {
    const s = ipop._idle;
    if (s.detector || !("IdleDetector" in window) || !window.isSecureContext) return false;
    try {
      const perm = await navigator.permissions.query({ name: "idle-detection" });
      if (perm.state !== "granted") return false;
      const detector = new IdleDetector();
      const abort = new AbortController();
      detector.addEventListener("change", () =>
        ipop._setIdle(detector.userState === "idle" || detector.screenState === "locked"));
      await detector.start({ threshold: ipop.idleThresholdMs, signal: abort.signal });
      s.detector = detector;
      s.abort = abort;
      ipop._setIdle(detector.userState === "idle" || detector.screenState === "locked");
      return true;
    } catch { return false; }
  },

  // ── Desktop alerts: Web Push → Windows toasts (wwwroot/sw.js) + computer-wide idle ──
  // Toasts come from the service worker, so they arrive even with every iPop tab closed.
  // Permission prompts must come from a click, hence the "Aktifkan" banner / menu item.
  _swReg: null,
  _pushSupported() {
    return window.isSecureContext && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
  },
  async _serviceWorker() {
    if (!ipop._pushSupported()) return null;
    if (!ipop._swReg) {
      ipop._swReg = navigator.serviceWorker.register(new URL("sw.js", document.baseURI).href, { scope: document.baseURI })
        .then(() => navigator.serviceWorker.ready)
        .catch(() => { ipop._swReg = null; return null; });
    }
    return await ipop._swReg;
  },
  async _pushSubscription() {
    const reg = await ipop._serviceWorker();
    return reg ? await reg.pushManager.getSubscription() : null;
  },
  async desktopAlertsState() {
    const secure = window.isSecureContext;
    const supported = ipop._pushSupported();
    let endpoint = null;
    if (supported && Notification.permission === "granted") {
      try { endpoint = (await ipop._pushSubscription())?.endpoint || null; } catch { }
    }
    return {
      secure,
      notify: supported ? Notification.permission : "unsupported",
      idle: !("IdleDetector" in window) || !secure ? "unsupported" : (ipop._idle.detector ? "granted" : "prompt"),
      endpoint,
    };
  },
  async enableDesktopAlerts() {
    if (ipop._pushSupported() && Notification.permission === "default") {
      try { await Notification.requestPermission(); } catch { }
    }
    if ("IdleDetector" in window && window.isSecureContext && !ipop._idle.detector) {
      try {
        if (await IdleDetector.requestPermission() === "granted") await ipop._startIdleDetector();
      } catch { }
    }
    return ipop.desktopAlertsState();
  },
  // Kept for older callers. The typed versions live in push-lifecycle.js (window.ipopPush).
  // Subscribes this browser (never prompts). Returns { endpoint, p256dh, auth } for the API, or null.
  async pushSubscribe(publicKey) {
    if (!window.ipopPush) return null;
    const r = await window.ipopPush.subscribe(publicKey);
    return r && r.ok ? { endpoint: r.endpoint, p256dh: r.p256dh, auth: r.auth } : null;
  },
  // Drops this browser's subscription; returns its endpoint so the API can forget it too.
  async pushUnsubscribe() {
    return window.ipopPush ? await window.ipopPush.unsubscribe() : null;
  },

  // ── Unread count in the tab title: "(3) Messages — IPop" ──
  // <PageTitle> rewrites document.title on every navigation, so an observer re-applies the prefix.
  _titleCount: 0,
  _titleObserver: null,
  setTitleBadge(n) {
    ipop._titleCount = Math.max(0, n | 0);
    ipop._applyTitleBadge();
    if (!ipop._titleObserver) {
      const head = document.querySelector("head");
      ipop._titleObserver = new MutationObserver(() => ipop._applyTitleBadge());
      ipop._titleObserver.observe(head, { subtree: true, childList: true, characterData: true });
    }
  },
  _applyTitleBadge() {
    const base = document.title.replace(/^\(\d+\+?\)\s+/, "");
    const n = ipop._titleCount;
    const want = n > 0 ? `(${n > 99 ? "99+" : n}) ${base}` : base;
    if (document.title !== want) document.title = want;
  },
  // News reader: J / K (or arrow down / up) step through the list while a post is open.
  newsKeys(dotnet) {
    if (window.ipop._newsKeys) document.removeEventListener("keydown", window.ipop._newsKeys);
    const onKey = e => {
      if (e.metaKey || e.ctrlKey || e.altKey || /INPUT|TEXTAREA|SELECT/.test(e.target.tagName) || e.target.isContentEditable) return;
      const k = e.key;
      if (k === "j" || k === "J") { e.preventDefault(); dotnet.invokeMethodAsync("NewsStep", 1); }
      else if (k === "k" || k === "K") { e.preventDefault(); dotnet.invokeMethodAsync("NewsStep", -1); }
    };
    window.ipop._newsKeys = onKey;
    document.addEventListener("keydown", onKey);
    return true;
  },
  newsKeysOff() {
    if (window.ipop._newsKeys) document.removeEventListener("keydown", window.ipop._newsKeys);
    window.ipop._newsKeys = null;
    return true;
  },
  // Global shortcuts live in JS because they fire on document, not on a component.
  // MainLayout registers once; the returned handles let Blazor detach on dispose.
  shortcuts(dotnet) {
    const onKey = e => {
      // ⌘K / Ctrl+K opens the command palette from anywhere, including inputs.
      if (e.key.toLowerCase() === "k" && (e.metaKey || e.ctrlKey)) {
        e.preventDefault();
        dotnet.invokeMethodAsync("OnCommandPalette");
        return;
      }
      if (e.key === "Escape") dotnet.invokeMethodAsync("OnEscape");
      if (e.key === "[" && !/INPUT|TEXTAREA/.test(e.target.tagName)) {
        dotnet.invokeMethodAsync("OnToggleSidebar");
      }
    };
    const onClick = e => {
      // The account and status menus close themselves: clicking one of their items must reach Blazor first.
      if (!e.target.closest(".notif-pop, .bell-btn, .me-menu, .me-card, .status-wrap")) {
        dotnet.invokeMethodAsync("OnClickAway");
      }
    };
    // Capture phase: Blazor's root handler re-renders and lucide.createIcons()
    // swaps the <svg> out, which detaches e.target and breaks closest(). Running
    // first means we still see the original DOM.
    document.addEventListener("keydown", onKey);
    document.addEventListener("click", onClick, true);
    window.ipop._off = () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("click", onClick, true);
    };
  },
  shortcutsOff() {
    if (window.ipop._off) { window.ipop._off(); window.ipop._off = null; }
  },
  download(b64, name) {
    const a = document.createElement("a");
    a.href = "data:application/octet-stream;base64," + b64;
    a.download = name;
    a.click();
  },
  // Rows are arrays of cells; the first row is the header. Falls back to CSV if SheetJS didn't load.
  exportXlsx(rows, sheetName, fileName) {
    if (window.XLSX) {
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet(rows), sheetName || "Sheet1");
      XLSX.writeFile(wb, fileName);
      return;
    }
    const esc = v => { const s = v == null ? "" : String(v); return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
    const csv = rows.map(r => r.map(esc).join(",")).join("\r\n");
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob(["﻿" + csv], { type: "text/csv" }));
    a.download = fileName.replace(/\.xlsx$/i, ".csv");
    a.click();
    URL.revokeObjectURL(a.href);
  },
  previewUrl: null,
  preview(b64, mime, name) {
    if (this.previewUrl) URL.revokeObjectURL(this.previewUrl);
    const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    const blob = new Blob([bytes], { type: mime });
    this.previewUrl = URL.createObjectURL(blob);
    return this.previewUrl;
  },
  printUrl(url) {
    const f = document.getElementById("preview-frame");
    if (f) { f.focus(); try { f.contentWindow.print(); } catch { window.open(url, "_blank"); } }
    else window.open(url, "_blank");
  },
  revokePreview() {
    if (this.previewUrl) { URL.revokeObjectURL(this.previewUrl); this.previewUrl = null; }
  },
  // Client-side Office preview — rendered into #office-preview host
  renderOffice(b64, ext) {
    const host = document.getElementById("office-preview");
    if (!host) return false;
    const bin = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
    try {
      if (ext === "xlsx" || ext === "xls" || ext === "csv") {
        if (!window.XLSX) return false;
        const wb = XLSX.read(bin, { type: "array" });
        const renderSheet = name => {
          const html = XLSX.utils.sheet_to_html(wb.Sheets[name], { editable: false });
          return '<div style="overflow:auto; max-height:calc(100dvh - 230px); border:1px solid var(--line-soft); border-radius:12px; background:var(--surface); padding:12px">' + html + '</div>';
        };
        const styleSheet = root => {
          root.querySelectorAll("table").forEach(t => { t.style.width="100%"; t.style.borderCollapse="collapse"; t.style.fontSize="12px"; });
          root.querySelectorAll("td,th").forEach(c => { c.style.border="1px solid var(--line-soft)"; c.style.padding="6px 8px"; });
        };
        // Active tab mirrors the app's .nav-item.active / .admin-tab.active pattern:
        // soft accent fill + accent-ink text (WCAG AA in both themes).
        const tabCss = a => a
          ? "border:1px solid rgba(46,125,91,0.3); background:var(--accent-soft); color:var(--accent-ink)"
          : "border:1px solid var(--line); background:var(--surface); color:var(--ink)";
        const tabs = wb.SheetNames.map((n, i) =>
          '<button data-sheet="' + n + '" style="' + tabCss(i === 0) + '; padding:4px 10px; border-radius:7px; cursor:pointer; font-size:11px; font-weight:600">' + n + '</button>'
        ).join('');
        host.innerHTML = '<div style="display:flex; flex-wrap:wrap; gap:4px; margin-bottom:8px; padding:4px 2px; border-bottom:1px solid var(--line-soft)">' + tabs + '</div><div id="sheet-view">' + renderSheet(wb.SheetNames[0]) + '</div>';
        host.querySelectorAll('[data-sheet]').forEach(b => b.addEventListener("click", () => {
          host.querySelectorAll('[data-sheet]').forEach(x => x.style.cssText = tabCss(x === b));
          document.getElementById("sheet-view").innerHTML = renderSheet(b.dataset.sheet);
          styleSheet(document.getElementById("sheet-view"));
        }));
        styleSheet(document.getElementById("sheet-view"));
        return true;
      }
    } catch { host.innerHTML = '<div class="note err"><span>Preview failed - use Download.</span></div>'; return true; }
    return false;
  },
  openExplorer(url) {
    // Try custom protocol ipop:// — tray agent handles it; fallback is handled by caller
    const a = document.createElement("a");
    a.href = url;
    a.click();
    return true;
  },
  // Position a fixed dropdown pop below the trigger. Works inside any overflow context.
  // Portal the popup to <body> and use position:fixed against the viewport.
  // Ancestors with overflow:hidden (panel cores), backdrop-filter or
  // transform (rise fill) would otherwise clip or trap the popup.
  positionSelect(trigger, pop) {
    const wrap = pop.closest(".app-select");
    // Sit above the overlay holding the trigger (calendar drawer 901, modals ...), like positionPopup.
    let z = 100;
    for (let el = trigger.parentElement; el && el !== document.body; el = el.parentElement) {
      const v = parseInt(getComputedStyle(el).zIndex, 10);
      if (!isNaN(v)) z = Math.max(z, v + 10);
    }
    if (!window.ipop._selectPop) {
      pop._selectHost = wrap || pop.parentElement;
      document.body.appendChild(pop);
      window.ipop._selectPop = pop;
    }
    const place = () => {
      const tr = trigger.getBoundingClientRect();
      pop.style.position = "fixed";
      pop.style.left = tr.left + "px";
      pop.style.minWidth = tr.width + "px";
      pop.style.maxWidth = Math.max(tr.width, Math.min(360, window.innerWidth - 16)) + "px";
      pop.style.zIndex = String(z);
      // Open upwards when the list would run off the bottom and there is more room above.
      const below = window.innerHeight - tr.bottom - 12, above = tr.top - 12;
      const ph = Math.min(pop.scrollHeight, 300);
      if (ph > below && above > below) {
        pop.style.maxHeight = Math.min(300, above) + "px";
        pop.style.top = Math.max(8, tr.top - 6 - Math.min(ph, above)) + "px";
      } else {
        pop.style.maxHeight = Math.max(120, Math.min(300, below)) + "px";
        pop.style.top = (tr.bottom + 6) + "px";
      }
      // flip left if it would overflow the right edge
      const pw = pop.offsetWidth;
      if (tr.left + pw > window.innerWidth - 8) {
        pop.style.left = Math.max(8, window.innerWidth - pw - 8) + "px";
      }
    };
    place();
    requestAnimationFrame(() => place()); // catch layout shifts from portal
    window.ipop._selectPlace = () => place();
    window.addEventListener("scroll", window.ipop._selectPlace, true);
    window.addEventListener("resize", window.ipop._selectPlace);
    // Outside-click close: mousedown anywhere outside the wrapper closes the
    // dropdown, but a mousedown on another trigger should only close this one
    // (that trigger's own @onclick then opens it).
    window.ipop._selectOutside = e => {
      if (wrap.contains(e.target) || pop.contains(e.target)) return;
      if (e.target.closest && e.target.closest(".app-select-trigger")) return;
      window.ipop.closeSelect();
    };
    document.addEventListener("mousedown", window.ipop._selectOutside);
    return true;
  },
  scrollSelectOpt(pop, idx) {
    const el = pop && pop.children[idx];
    if (el && el.scrollIntoView) el.scrollIntoView({ block: "nearest" });
    return true;
  },
  closeSelect() {
    if (window.ipop._selectPlace) {
      window.removeEventListener("scroll", window.ipop._selectPlace, true);
      window.removeEventListener("resize", window.ipop._selectPlace);
      window.ipop._selectPlace = null;
    }
    if (window.ipop._selectOutside) {
      document.removeEventListener("mousedown", window.ipop._selectOutside);
      window.ipop._selectOutside = null;
    }
    // Return the popup to its original slot so Blazor's render tree stays
    // consistent; it is hidden (opacity:0) until next open.
    const pop = window.ipop._selectPop;
    if (pop) {
      window.ipop._selectPop = null;
      if (pop._selectHost && pop._selectHost !== pop.parentElement) {
        pop._selectHost.appendChild(pop);
      }
    }
    return true;
  },
  // Member-picker combobox: portal the suggestion popup to <body> and pin it
  // under the input. Unlike positionSelect, no .app-select coupling and no
  // single-global-pop assumption — each call portals its own pop.
  positionPopup(trigger, pop) {
    pop._ppHost = pop.parentElement;
    // Once portalled to <body> the pop leaves its host's stacking context, so a flat
    // z-index lands it behind whatever overlay holds the input (calendar drawer z 901,
    // meetings modal 190, ...). Sit just above the highest z-indexed ancestor instead:
    // above the drawer/modal it belongs to, still under toasts and later dialogs.
    let z = 100;
    for (let el = trigger.parentElement; el && el !== document.body; el = el.parentElement) {
      const v = parseInt(getComputedStyle(el).zIndex, 10);
      if (!isNaN(v)) z = Math.max(z, v + 10);
    }
    document.body.appendChild(pop);
    const place = () => {
      const tr = trigger.getBoundingClientRect();
      pop.style.position = "fixed";
      pop.style.left = tr.left + "px";
      pop.style.top = (tr.bottom + 6) + "px";
      pop.style.minWidth = tr.width + "px";
      pop.style.maxWidth = Math.min(320, window.innerWidth - 16) + "px";
      pop.style.zIndex = String(z);
      const pw = pop.offsetWidth;
      if (tr.left + pw > window.innerWidth - 8) {
        pop.style.left = Math.max(8, window.innerWidth - pw - 8) + "px";
      }
    };
    place();
    requestAnimationFrame(() => place());
    // Called on every render while open: drop the previous listeners before adding new ones.
    if (window.ipop._ppPlace) {
      window.removeEventListener("scroll", window.ipop._ppPlace, true);
      window.removeEventListener("resize", window.ipop._ppPlace);
    }
    window.ipop._ppPlace = () => place();
    window.addEventListener("scroll", window.ipop._ppPlace, true);
    window.addEventListener("resize", window.ipop._ppPlace);
    return true;
  },
  /// People-picker lists whose picker was removed while the list sat on <body>.
  dropOrphanPopups() {
    // A list stays on <body> only while a picker is still showing results; with no picker left
    // to own it (its host closed after a pick), it is stale.
    if (document.querySelector(".member-picker-input input:focus")) return true;
    document.querySelectorAll("body > .member-picker-pop").forEach(p => p.remove());
    return true;
  },
  closePopup() {
    if (window.ipop._ppPlace) {
      window.removeEventListener("scroll", window.ipop._ppPlace, true);
      window.removeEventListener("resize", window.ipop._ppPlace);
      window.ipop._ppPlace = null;
    }
    const pops = document.querySelectorAll(".member-picker-pop");
    pops.forEach(p => {
      if (!p._ppHost || p._ppHost === p.parentElement) return;
      // The picker that owned it may already be gone (e.g. its row closed on pick): drop the orphan.
      if (p._ppHost.isConnected) p._ppHost.appendChild(p); else p.remove();
    });
    return true;
  },
  // Row/card action menus (Projects board, list, task drawer). The menu is portalled to
  // <body> and pinned to its trigger with position:fixed, so no overflow:auto lane, card or
  // drawer body can clip it; it flips above the trigger when there is no room below. One menu
  // is open at a time. Esc (returns focus to the trigger), scrolling anything outside the menu,
  // and a new menuOpen all close it via dotnet.CloseMenuFromJs; outside clicks are caught by the
  // component's own backdrop. The menu element must be the last child of its host (Blazor
  // inserts siblings relative to it). Blazor removes it from <body> when it stops rendering it.
  // Phones (<= 767px) keep the CSS bottom sheet for .kc-menu/.plist-menu/.quick-assign-popover.
  menuOpen(pop, dotnet, align) {
    ipop.menuClose();
    if (!pop || !pop.parentElement || pop.parentElement === document.body) return false;
    const host = pop.parentElement;
    const trigger = host.querySelector("[data-menu-trigger]") || host.querySelector("button");
    let z = 100;
    for (let el = host; el && el !== document.body; el = el.parentElement) {
      const v = parseInt(getComputedStyle(el).zIndex, 10);
      if (!isNaN(v)) z = Math.max(z, v + 10);
    }
    document.body.appendChild(pop);
    pop.classList.add("ipop-menu-portal");
    const st = { pop, trigger, dotnet, align: align || "left", z, openedAt: Date.now() };
    st.place = () => ipop._menuPlace(st);
    st.onScroll = e => {
      if (Date.now() - st.openedAt < 200) return;
      if (e.target && e.target.nodeType === 1 && (pop.contains(e.target) || e.target.closest(".dp-portal"))) return;
      ipop._menuDismiss(false);
    };
    st.onKey = e => {
      if (!pop.isConnected) { ipop.menuClose(); return; }
      // A date picker opened from inside the menu handles its own keys (Esc closes it first).
      if (e.target && e.target.closest && e.target.closest(".dp-portal")) return;
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopImmediatePropagation();
        ipop._menuDismiss(true);
        return;
      }
      if (!pop.contains(document.activeElement)) return;
      const items = ipop._menuItems(pop);
      const i = items.indexOf(document.activeElement);
      let next = null;
      if (e.key === "ArrowDown") next = items[(i + 1) % items.length];
      else if (e.key === "ArrowUp") next = items[(i - 1 + items.length) % items.length];
      else if (e.key === "Home") next = items[0];
      else if (e.key === "End") next = items[items.length - 1];
      else if ((e.key === "Enter" || e.key === " ") && i >= 0 && !/BUTTON|INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName)) {
        e.preventDefault();
        document.activeElement.click();
        return;
      }
      if (next && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName || "")) {
        e.preventDefault();
        next.focus({ preventScroll: true });
      }
    };
    // Outside press closes. Backdrops handle their own click (and swallow it), the trigger
    // toggles itself, and a date picker opened from the menu lives in its own portal.
    st.onDown = e => {
      const t = e.target;
      if (!t || t.nodeType !== 1 || pop.contains(t)) return;
      if ((st.trigger && st.trigger.contains(t)) || t.closest(".kc-menu-backdrop, .plist-backdrop, .td-menu-backdrop, .dp-portal")) return;
      ipop._menuDismiss(false);
    };
    window.addEventListener("scroll", st.onScroll, true);
    window.addEventListener("resize", st.place);
    document.addEventListener("keydown", st.onKey, true);
    document.addEventListener("pointerdown", st.onDown, true);
    ipop._menu = st;
    st.place();
    requestAnimationFrame(() => { if (ipop._menu === st) st.place(); });
    const first = pop.querySelector("[data-autofocus]") || ipop._menuItems(pop)[0];
    if (first) { try { first.focus({ preventScroll: true }); } catch { } }
    return true;
  },
  // Called after every render while a menu is open: portal it if this is a new element
  // (first open, or Blazor re-created it after a data refresh), otherwise just re-measure.
  menuSync(pop, dotnet, align) {
    const st = ipop._menu;
    if (st && st.pop === pop && pop && pop.isConnected) { ipop.menuPlace(); return true; }
    if (st && st.pop !== pop && st.pop.parentElement === document.body && !st.trigger?.isConnected) st.pop.remove();
    return ipop.menuOpen(pop, dotnet, align);
  },
  // Re-measure the open menu after a re-render changed its content.
  menuPlace() {
    const st = ipop._menu;
    if (!st) return;
    if (!st.pop.isConnected) { ipop.menuClose(); return; }
    // The row/card that owned the menu was re-rendered away (live refresh, filter): Blazor
    // removed the host subtree but not the portalled node, so drop it and tell the component.
    if (st.trigger && !st.trigger.isConnected) { st.pop.remove(); ipop._menuDismiss(false); return; }
    st.place();
  },
  // Projects task drawer pager: Alt+Left / Alt+Right step through the current view's tasks.
  // Capture phase + preventDefault so the browser's Alt+Arrow history navigation never fires
  // while the drawer is open; ignored while a modal/confirm sits over the drawer.
  taskDrawerKeys(dotnet) {
    if (ipop._tdKeys) document.removeEventListener("keydown", ipop._tdKeys, true);
    ipop._tdKeys = null;
    if (!dotnet) return;
    ipop._tdKeys = e => {
      if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
      if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      if (!document.querySelector(".v6-task-drawer")) return;
      // Option/Alt+Arrow moves by word in text fields (macOS): leave typing alone.
      if (ipop.isEditableTarget(e.target)) return;
      if (document.querySelector(".evidence-modal-backdrop, .confirm-center")) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      dotnet.invokeMethodAsync("OnPagerKey", e.key === "ArrowLeft" ? -1 : 1).catch(() => { });
    };
    document.addEventListener("keydown", ipop._tdKeys, true);
  },
  // Component disposed while its menu was open: remove the portalled node Blazor can no longer reach.
  menuDrop() {
    const st = ipop._menu;
    if (!st) return;
    ipop.menuClose();
    if (st.pop.parentElement === document.body) st.pop.remove();
  },
  // Drop the open menu's listeners (Blazor already removed or is removing the element).
  menuClose() {
    const st = ipop._menu;
    if (!st) return true;
    ipop._menu = null;
    window.removeEventListener("scroll", st.onScroll, true);
    window.removeEventListener("resize", st.place);
    document.removeEventListener("keydown", st.onKey, true);
    document.removeEventListener("pointerdown", st.onDown, true);
    return true;
  },
  _menuDismiss(refocus) {
    const st = ipop._menu;
    if (!st) return;
    ipop.menuClose();
    if (refocus && st.trigger && st.trigger.isConnected) {
      try { st.trigger.focus({ preventScroll: true }); } catch { }
    }
    if (st.dotnet) st.dotnet.invokeMethodAsync("CloseMenuFromJs").catch(() => { });
  },
  _menuItems(pop) {
    return [...pop.querySelectorAll("button:not([disabled]), [role=menuitem], [role=menuitemcheckbox], .quick-assign-item, input:not([type=hidden]):not([disabled])")]
      .filter((el, i, a) => a.indexOf(el) === i && el.offsetParent !== null);
  },
  _menuPlace(st) {
    const { pop, trigger } = st;
    if (!pop.isConnected) return;
    const s = pop.style;
    s.position = "fixed";
    s.zIndex = String(st.z);
    s.margin = "0";
    s.maxHeight = "";
    s.overflowY = "";
    const sheet = !!(window.ipop.mobile && window.ipop.mobile.isMobile())
      && pop.matches(".kc-menu, .plist-menu, .quick-assign-popover");
    if (sheet || !trigger || !trigger.isConnected) {
      // Mobile bottom sheet: the stylesheet owns left/right/bottom.
      s.left = s.top = s.right = s.bottom = "";
      pop.classList.remove("above");
      return;
    }
    s.right = "auto";
    s.bottom = "auto";
    const r = trigger.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight, m = 8, gap = 4;
    const pw = pop.offsetWidth, ph = pop.offsetHeight;
    let left = st.align === "right" ? r.right - pw : r.left;
    left = Math.max(m, Math.min(left, vw - pw - m));
    const below = vh - r.bottom - gap - m, above = r.top - gap - m;
    let top, up = false;
    if (ph <= below) top = r.bottom + gap;
    else if (ph <= above) { top = r.top - gap - ph; up = true; }
    else if (above > below) {
      up = true;
      s.maxHeight = Math.max(120, above) + "px";
      s.overflowY = "auto";
      top = Math.max(m, r.top - gap - Math.min(ph, above));
    } else {
      s.maxHeight = Math.max(120, below) + "px";
      s.overflowY = "auto";
      top = r.bottom + gap;
    }
    s.left = Math.round(left) + "px";
    s.top = Math.round(top) + "px";
    pop.classList.toggle("above", up);
  },
  // Chat: scroll a container to its bottom (used after new message arrives)
  // Jump to the newest message and STAY there while the list is still settling: images,
  // link previews and reply quotes finish loading after the jump and grow the rows, which
  // used to leave the view stranded partway up the conversation. The pin ends as soon as
  // the reader scrolls/touches/keys, or after a short settle window.
  // soft: only when the reader is already at (or near) the bottom — an incoming message must
  // never yank someone who scrolled up to read history. Returns whether it scrolled.
  scrollBottom(el, soft) {
    if (!el) return false;
    // _ipopNear is refreshed on scroll only, so it still describes where the reader was
    // before the new message grew the list.
    if (soft && !el._ipopPin && el._ipopNear === false) return false;
    el.scrollTop = el.scrollHeight;
    if (el._ipopPin) el._ipopPin();
    const pin = () => { el.scrollTop = el.scrollHeight; };
    const ro = new ResizeObserver(pin);
    ro.observe(el);
    el.querySelectorAll("[data-msg-id], img, video").forEach((n) => ro.observe(n));
    const mo = new MutationObserver(() => { el.querySelectorAll("[data-msg-id], img, video").forEach((n) => ro.observe(n)); pin(); });
    mo.observe(el, { childList: true, subtree: true });
    const imgs = [...el.querySelectorAll("img")].filter((i) => !i.complete);
    imgs.forEach((i) => i.addEventListener("load", pin, { once: true }));
    const stop = () => {
      ro.disconnect(); mo.disconnect(); clearTimeout(timer);
      el.removeEventListener("scroll", onScroll);
      ["wheel", "touchstart", "keydown", "pointerdown", "mousedown"].forEach((ev) => el.removeEventListener(ev, stop));
      if (el._ipopPin === stop) delete el._ipopPin;
    };
    // Our own pins land exactly at the bottom; any scroll that leaves the bottom is the reader
    // (including scrollbar drags, which don't reliably fire pointerdown on the scroller).
    const onScroll = () => { if (el.scrollHeight - el.scrollTop - el.clientHeight > 4) stop(); };
    const timer = setTimeout(stop, 2500);
    ["wheel", "touchstart", "keydown", "pointerdown", "mousedown"].forEach((ev) => el.addEventListener(ev, stop, { passive: true }));
    el.addEventListener("scroll", onScroll, { passive: true });
    el._ipopPin = stop;
    return true;
  },
  async authBlob(url, token) {
    const res = await fetch(url, { headers: token ? { Authorization: "Bearer " + token } : {} });
    if (!res.ok) return null;
    return URL.createObjectURL(await res.blob());
  },
  // ---- Mail inline images ----------------------------------------------------
  // Pasted images in mail bodies are stored on the NAS and referenced as
  // <img src="/api/attachments/{id}" data-ipop-att="{id}">. The API wants a bearer token, so
  // the page fetches each one with it and swaps in a blob: URL (one fetch per id, shared by the
  // reader, the thread stack and the composer). Failures are not cached: the next render retries.
  mailImages: {
    _urls: new Map(),   // id -> Promise<string|null>
    _base: "",
    _token: null,
    setAuth(apiBase, token) {
      if (apiBase != null) this._base = String(apiBase).replace(/\/+$/, "");
      if (token !== undefined) this._token = token;
    },
    _get(id) {
      let p = this._urls.get(id);
      if (!p) {
        p = (async () => {
          try {
            const res = await fetch(`${this._base}/api/attachments/${id}`,
              { headers: this._token ? { Authorization: "Bearer " + this._token } : {} });
            return res.ok ? URL.createObjectURL(await res.blob()) : null;
          } catch { return null; }
        })();
        this._urls.set(id, p);
        p.then(u => { if (!u && this._urls.get(id) === p) this._urls.delete(id); });
      }
      return p;
    },
    // root: element, selector or null (whole document). Returns once every image is settled.
    async hydrate(root, apiBase, token) {
      this.setAuth(apiBase, token);
      const scope = typeof root === "string" ? document.querySelector(root) : (root || document);
      if (!scope) return;
      const imgs = Array.from(scope.querySelectorAll("img[data-ipop-att]"));
      await Promise.all(imgs.map(async img => {
        const id = img.getAttribute("data-ipop-att");
        if (!/^[0-9a-f-]{36}$/i.test(id || "")) return;
        if ((img.getAttribute("src") || "").startsWith("blob:") && img.dataset.ipopAttFor === id) return;
        const url = await this._get(id);
        if (url && img.getAttribute("data-ipop-att") === id) { img.dataset.ipopAttFor = id; img.setAttribute("src", url); }
      }));
    },
    // Editor HTML going back to the server: blob: URLs only make sense in this tab.
    restoreSrc(html) {
      if (!html || html.indexOf("blob:") < 0 || html.indexOf("data-ipop-att") < 0) return html;
      const t = document.createElement("template");
      t.innerHTML = html;
      t.content.querySelectorAll("img[data-ipop-att]").forEach(img => {
        const id = img.getAttribute("data-ipop-att");
        if (/^[0-9a-f-]{36}$/i.test(id || "")) img.setAttribute("src", `/api/attachments/${id}`);
        img.removeAttribute("data-ipop-att-for");
      });
      return t.innerHTML;
    },
    // Revoke what is no longer on screen (page dispose). Images still shown elsewhere (an open
    // composer) keep their URL.
    async release() {
      for (const [id, p] of Array.from(this._urls)) {
        const url = await p;
        if (url && document.querySelector(`img[src="${url}"]`)) continue;
        if (url) URL.revokeObjectURL(url);
        if (this._urls.get(id) === p) this._urls.delete(id);
      }
    },
    // Sign-out / account switch: nothing fetched with the old token survives.
    clear() {
      for (const p of this._urls.values()) p.then(u => u && URL.revokeObjectURL(u));
      this._urls.clear();
      this._token = null;
    },
  },
  async authDownload(url, token, filename) {
    const res = await fetch(url, { headers: token ? { Authorization: "Bearer " + token } : {} });
    if (!res.ok) return;
    const href = URL.createObjectURL(await res.blob());
    const a = document.createElement("a");
    a.href = href;
    a.download = filename || "download";
    a.click();
    URL.revokeObjectURL(href);
  },
  revokeBlob(url) { if (url) URL.revokeObjectURL(url); },
  // Chat: trigger the hidden file input for attachment upload
  triggerFileInput(selector) {
    // Mail passes its own selector: its InputFile sits in the composer footer, not in .composer-wrap.
    var input = selector ? document.querySelector(selector) : null;
    if (!input) {
      var wrap = document.querySelector(".composer-wrap");
      input = wrap && wrap.querySelector('input[type="file"]');
    }
    if (input) input.click();
  },
  // Chat: clipboard paste + drag-drop for attachments (bound to composer wrap)
  _chatPaste: null,
  chatBindPaste(wrap, dotNet) {
    if (!wrap || !dotNet) return;
    if (wrap._ipopPaste) this.chatUnbindPaste(wrap);
    const deliver = files => {
      if (!wrap.isConnected) return;
      if (wrap.dataset.uploading === "true") {
        dotNet.invokeMethodAsync("OnPasteUploadNotice", "Wait for the current upload to finish.").catch(() => {});
        return;
      }
      const input = wrap.querySelector('input[type="file"]');
      if (!input) return;
      try {
        // Reuse Blazor InputFile streaming and the same validation for picker, paste and drop.
        // No base64 copies of whole files and no silent size/count filtering here.
        const transfer = new DataTransfer();
        Array.from(files).filter(Boolean).forEach(file => transfer.items.add(file));
        input.files = transfer.files;
        input.dispatchEvent(new Event("change", { bubbles: true }));
      } catch {
        dotNet.invokeMethodAsync("OnPasteUploadNotice", "Couldn't attach these files. Use the attachment button to select them.").catch(() => {});
      }
    };
    // Windows Snipping Tool / PrintScreen put the image on items, not files.
    const filesFromClipboard = e => {
      const items = e.clipboardData && e.clipboardData.items;
      if (items && items.length) {
        const fromItems = Array.from(items).filter(i => i.kind === "file").map(i => i.getAsFile()).filter(Boolean);
        if (fromItems.length) return fromItems;
      }
      return e.clipboardData && e.clipboardData.files ? Array.from(e.clipboardData.files) : [];
    };
    const onPaste = async e => {
      const files = filesFromClipboard(e);
      if (!files.length) return;
      e.preventDefault();
      deliver(files);
    };
    // Files dropped anywhere on the open conversation (messages + composer) attach to the
    // composer, so the target is the whole pane rather than the thin composer strip.
    const zone = wrap.closest(".conv-pane") || wrap;
    const onDrop = async e => {
      const files = e.dataTransfer && e.dataTransfer.files;
      if (!files || files.length === 0) return;
      e.preventDefault();
      wrap.classList.remove("drag-over");
      deliver(files);
    };
    const onDragOver = e => {
      if (e.dataTransfer && e.dataTransfer.types && Array.from(e.dataTransfer.types).includes("Files")) {
        e.preventDefault();
        e.dataTransfer.dropEffect = "copy";
      }
    };
    const onDragLeave = e => {
      if (!zone.contains(e.relatedTarget)) wrap.classList.remove("drag-over");
    };
    window.ipopMicro && ipopMicro.registerDrop(zone, "Drop to send", "Images, PDF, Office files — they attach to your message");
    wrap.addEventListener("paste", onPaste);
    zone.addEventListener("drop", onDrop);
    zone.addEventListener("dragover", onDragOver);
    zone.addEventListener("dragleave", onDragLeave);
    wrap._ipopZone = zone;
    wrap._ipopPaste = onPaste;
    wrap._ipopDrop = onDrop;
    wrap._ipopDragOver = onDragOver;
    wrap._ipopDragLeave = onDragLeave;
  },
  chatUnbindPaste(wrap) {
    if (!wrap || !wrap._ipopPaste) return;
    const zone = wrap._ipopZone || wrap;
    window.ipopMicro && ipopMicro.unregisterDrop(zone);
    wrap.removeEventListener("paste", wrap._ipopPaste);
    zone.removeEventListener("drop", wrap._ipopDrop);
    zone.removeEventListener("dragover", wrap._ipopDragOver);
    zone.removeEventListener("dragleave", wrap._ipopDragLeave);
    wrap.classList.remove("drag-over");
    delete wrap._ipopPaste;
    delete wrap._ipopDrop;
    delete wrap._ipopDragOver;
    delete wrap._ipopDragLeave;
  },

  // Mail composer: drag-and-drop file zone (Fase 2). Meneruskan FileList ke
  // input[type=file] tersembunyi di dalam composer, lalu memicu event change
  // agar Blazor InputFile mengambilnya.
  mailBindDropZone(el, fileInputSelector) {
    if (!el || el._ipopMailDrop) return;
    const input = el.querySelector(fileInputSelector) || document.querySelector(fileInputSelector);
    if (!input) return;
    const onDragOver = e => {
      if (e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files")) {
        e.preventDefault();
        el.classList.add("drag-over");
      }
    };
    const onDragLeave = e => {
      if (!el.contains(e.relatedTarget)) el.classList.remove("drag-over");
    };
    // Capture phase: the rich-text editor (Jodit) handles drops on its body itself and, with no
    // upload URL, swallows anything that isn't an image ("Need URL for AJAX request"). Take the drop
    // first: images dropped onto the message body stay inline as before, everything else (and images
    // dropped outside the body) becomes an attachment through the same InputFile path as the picker.
    const isImage = f => /^image\//.test(f.type || "");
    const onDrop = e => {
      const files = e.dataTransfer && e.dataTransfer.files;
      if (!files || files.length === 0) return;
      el.classList.remove("drag-over", "mi-drag-over");
      const inBody = e.target && e.target.closest && e.target.closest(".jodit-wysiwyg, [contenteditable='true']");
      const list = Array.from(files);
      if (inBody && list.every(isImage)) return; // the editor inserts these inline
      e.preventDefault();
      e.stopPropagation();
      const attach = inBody ? list.filter(f => !isImage(f)) : list;
      const inline = inBody ? list.filter(isImage) : [];
      try {
        const dt = new DataTransfer();
        attach.slice(0, 5).forEach(f => dt.items.add(f));
        if (dt.files.length) {
          input.files = dt.files;
          input.dispatchEvent(new Event("change", { bubbles: true }));
        }
      } catch { /* DataTransfer ctor not supported — ignore */ }
      // Mixed drop on the body: hand the images back to the editor as a paste so they stay inline.
      if (inline.length && inBody) {
        try {
          const dt = new DataTransfer();
          inline.forEach(f => dt.items.add(f));
          inBody.dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
        } catch { /* older browsers: images in a mixed drop are skipped */ }
      }
    };
    el.addEventListener("dragover", onDragOver, true);
    el.addEventListener("dragleave", onDragLeave);
    el.addEventListener("drop", onDrop, true);
    el._ipopMailDrop = { onDragOver, onDragLeave, onDrop };
    window.ipopMicro && ipopMicro.registerDrop(el, "Drop to attach", "Images, PDF, Office files");
  },
  // Mail composer: the latest unsaved draft request, sent with keepalive if the tab closes
  // or reloads before the 2 s autosave fires. Called with nulls once it is saved.
  mailDraftStash(url, method, json, token) {
    ipop._mailDraft = url ? { url, method, json, token } : null;
    if (ipop._mailDraftBound) return;
    ipop._mailDraftBound = true;
    window.addEventListener("pagehide", () => {
      const d = ipop._mailDraft;
      if (!d) return;
      ipop._mailDraft = null;
      try {
        fetch(d.url, { method: d.method, body: d.json, keepalive: true,
          headers: { "Content-Type": "application/json", ...(d.token ? { Authorization: "Bearer " + d.token } : {}) } });
      } catch { /* best effort */ }
    });
  },

  // Mail attachments: the files picked/dropped into an <InputFile> go straight from the browser
  // to /api/mail/attachments/upload, one XHR per file, so a 500 MB file is streamed from disk
  // instead of being copied through Blazor's memory. `bridge` (MailUploadBridge) hands out the
  // token (refreshed once on 401) and receives byte progress. Returns { files, errors }.
  async mailUploadInput(el, url, bridge, maxFiles, maxBytes) {
    if (typeof el === "string") el = document.querySelector(el);
    const input = !el ? null : el.tagName === "INPUT" ? el : el.querySelector('input[type="file"]');
    const all = input && input.files ? Array.from(input.files) : [];
    const errors = [];
    const mb = n => Math.round(n / 1048576);
    let picked = all.filter(f => {
      if (f.size <= 0) { errors.push(`${f.name} is empty.`); return false; }
      if (f.size > maxBytes) { errors.push(`${f.name} is larger than ${mb(maxBytes)} MB.`); return false; }
      return true;
    });
    if (picked.length > maxFiles) {
      errors.push(`Only the first ${maxFiles} files were attached.`);
      picked = picked.slice(0, maxFiles);
    }
    const total = picked.reduce((s, f) => s + f.size, 0);
    let done = 0, last = 0;
    const report = (loaded, force) => {
      const now = performance.now();
      if (!force && now - last < 150) return;
      last = now;
      bridge.invokeMethodAsync("Progress", done + loaded, total).catch(() => { });
    };
    const send = (file, token) => new Promise(resolve => {
      const xhr = new XMLHttpRequest();
      xhr.open("POST", url);
      if (token) xhr.setRequestHeader("Authorization", "Bearer " + token);
      xhr.upload.onprogress = e => report(e.loaded, false);
      xhr.onload = () => {
        let body = null;
        try { body = JSON.parse(xhr.responseText || "null"); } catch { /* not JSON */ }
        resolve({ status: xhr.status, body });
      };
      xhr.onerror = () => resolve({ status: 0, body: null });
      xhr.onabort = () => resolve({ status: 0, body: null });
      const form = new FormData();
      form.append("files", file, file.name);
      xhr.send(form);
    });
    const files = [];
    for (const f of picked) {
      let res = await send(f, await bridge.invokeMethodAsync("Token", false));
      if (res.status === 401) res = await send(f, await bridge.invokeMethodAsync("Token", true));
      if (res.status >= 200 && res.status < 300 && Array.isArray(res.body)) files.push(...res.body);
      else if (res.status === 413) errors.push(`${f.name} is too large to upload.`);
      else errors.push((res.body && res.body.message) || (res.status === 0 ? `${f.name}: network error.` : `${f.name}: upload failed (${res.status}).`));
      done += f.size;
      report(0, true);
    }
    try { input.value = ""; } catch { /* re-picking the same file must raise change again */ }
    return { files, errors };
  },

  mailUnbindDropZone(el) {
    if (!el || !el._ipopMailDrop) return;
    const h = el._ipopMailDrop;
    el.removeEventListener("dragover", h.onDragOver, true);
    el.removeEventListener("dragleave", h.onDragLeave);
    el.removeEventListener("drop", h.onDrop, true);
    el.classList.remove("drag-over");
    delete el._ipopMailDrop;
  },

  // Mail RTE: paste gambar dari clipboard (Ctrl+V) disisipkan inline sebagai
  // <img src="data:..."> di posisi kursor. Teks tetap dibiarkan ke handler
  // paste bawaan contenteditable.
  // Micro-interaction #14: a shimmering placeholder holds the caret spot while the file is read,
  // then the picture fades in (img.mi-revealed). Capture phase, so it runs before Jodit's own paste.
  // `onDone` lets the host editor sync its value (Jodit does not listen to "input").
  rteBindImagePaste(el, onDone) {
    if (!el || el._ipopRtePaste) return;
    const onPaste = e => {
      const items = e.clipboardData && e.clipboardData.items;
      if (!items) return;
      const imgItem = Array.from(items).find(i => i.kind === "file" && i.type.startsWith("image/"));
      if (!imgItem) return; // bukan gambar → biarkan teks tertempel normal
      // Word / web copies carry HTML next to a rendered image: keep Jodit's HTML paste for those.
      if (Array.from(e.clipboardData.types || []).includes("text/html")) return;
      const file = imgItem.getAsFile();
      if (!file) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      const ph = window.ipopMicro ? ipopMicro.pastePlaceholder() : document.createElement("span");
      const sel = window.getSelection();
      if (sel && sel.rangeCount > 0 && el.contains(sel.anchorNode)) {
        const range = sel.getRangeAt(0);
        range.deleteContents();
        range.insertNode(ph);
        range.setStartAfter(ph);
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
      } else {
        el.appendChild(ph);
      }
      const reader = new FileReader();
      reader.onerror = () => ph.remove();
      reader.onload = ev => {
        const img = document.createElement("img");
        img.src = ev.target.result;
        img.style.maxWidth = "100%";
        if (!(window.ipopMicro && ipopMicro.reduced())) {
          img.className = "mi-revealed";
          // Drop the class again (and re-sync) so it never ends up in the saved HTML.
          const clear = () => { if (!img.classList.contains("mi-revealed")) return; img.classList.remove("mi-revealed"); if (!img.getAttribute("class")) img.removeAttribute("class"); if (onDone) onDone(); };
          img.addEventListener("animationend", clear, { once: true });
          setTimeout(clear, 600);
        }
        if (ph.isConnected) ph.replaceWith(img); else el.appendChild(img);
        el.dispatchEvent(new Event("input", { bubbles: true }));
        if (onDone) onDone();
      };
      reader.readAsDataURL(file);
    };
    el.addEventListener("paste", onPaste, true);
    el._ipopRtePaste = onPaste;
  },
  rteUnbindImagePaste(el) {
    if (!el || !el._ipopRtePaste) return;
    el.removeEventListener("paste", el._ipopRtePaste, true);
    delete el._ipopRtePaste;
  },

  // Mail RTE (Fase 2): gambar di editor bisa digeser (drag) dan diubah
  // ukurannya (handle pojok kanan bawah). Posisi disimpan sebagai float/absolut
  // di dalam HTML sehingga ikut tersimpan ke draf.
  rteBindImageManipulation(el) {
    if (!el || el._ipopRteManip) return;

    const makeManipulable = img => {
      if (img._ipopManip) return;
      img._ipopManip = true;
      img.style.cursor = "move";
      img.style.maxWidth = "100%";

      const wrapInFloater = () => {
        if (img.parentElement && img.parentElement.classList.contains("rte-img-float")) return img.parentElement;
        const wrap = document.createElement("span");
        wrap.className = "rte-img-float";
        wrap.style.position = "relative";
        wrap.style.display = "inline-block";
        img.parentNode.insertBefore(wrap, img);
        wrap.appendChild(img);
        const handle = document.createElement("span");
        handle.className = "rte-img-resize";
        handle.title = "Drag to resize";
        wrap.appendChild(handle);
        return wrap;
      };

      // Drag: klik-gerak memindahkan gambar lewat margin kiri/atas relatif.
      img.addEventListener("mousedown", e => {
        e.preventDefault();
        const startX = e.clientX, startY = e.clientY;
        const startMl = parseFloat(getComputedStyle(img).marginLeft) || 0;
        const startMt = parseFloat(getComputedStyle(img).marginTop) || 0;
        const move = ev => {
          img.style.marginLeft = (startMl + ev.clientX - startX) + "px";
          img.style.marginTop = (startMt + ev.clientY - startY) + "px";
        };
        const up = () => {
          document.removeEventListener("mousemove", move);
          document.removeEventListener("mouseup", up);
          el.dispatchEvent(new Event("input", { bubbles: true }));
        };
        document.addEventListener("mousemove", move);
        document.addEventListener("mouseup", up);
      });

      // Resize handle: tarik pojok kanan bawah mengubah lebar.
      const wrap = wrapInFloater();
      const handle = wrap.querySelector(".rte-img-resize");
      handle.addEventListener("mousedown", e => {
        e.preventDefault();
        e.stopPropagation();
        const startX = e.clientX;
        const startW = img.offsetWidth;
        const move = ev => {
          const w = Math.max(40, startW + ev.clientX - startX);
          img.style.width = w + "px";
        };
        const up = () => {
          document.removeEventListener("mousemove", move);
          document.removeEventListener("mouseup", up);
          el.dispatchEvent(new Event("input", { bubbles: true }));
        };
        document.addEventListener("mousemove", move);
        document.addEventListener("mouseup", up);
      });
    };

    // Terapkan ke gambar yang sudah ada + amati gambar baru (hasil paste).
    el.querySelectorAll("img").forEach(makeManipulable);
    const observer = new MutationObserver(muts => {
      for (const m of muts) {
        for (const n of m.addedNodes) {
          if (n.nodeType !== 1) continue;
          if (n.tagName === "IMG") makeManipulable(n);
          else n.querySelectorAll && n.querySelectorAll("img").forEach(makeManipulable);
        }
      }
    });
    observer.observe(el, { childList: true, subtree: true });
    el._ipopRteManip = observer;
  },
  rteUnbindImageManipulation(el) {
    if (!el || !el._ipopRteManip) return;
    el._ipopRteManip.disconnect();
    delete el._ipopRteManip;
  },

  chatBindComposer(el, dotNet) {
    if (!el || !dotNet) return;
    if (el._ipopComposerBound) this.chatUnbindComposer(el);
    el._ipopComposerBound = true;

    const onKey = (e) => {
      // @-mention list open: arrows/Enter/Tab/Esc drive the list and never send.
      if (el.getAttribute('data-mention-open') === '1' && !e.isComposing &&
          (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Tab' || e.key === 'Escape' ||
           (e.key === 'Enter' && !e.shiftKey))) {
        e.preventDefault();
        e.stopPropagation();
        dotNet.invokeMethodAsync('OnJsMentionKey', e.key);
        return;
      }
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        el.style.height = 'auto';
        dotNet.invokeMethodAsync('OnJsEnter');
      } else if (e.key === 'Escape') {
        dotNet.invokeMethodAsync('OnJsEscape');
      }
    };
    const onInput = () => {
      window.ipop.autoGrow(el);
    };
    // Caret moved without typing (click / arrows / Home / End): let .NET re-check the
    // @token under the caret so the list follows it (or closes).
    const onCaret = (e) => {
      if (e.type === 'keyup' && !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
      dotNet.invokeMethodAsync('OnJsComposerCaret', el.selectionStart ?? 0);
    };

    el.addEventListener('keydown', onKey);
    el.addEventListener('input', onInput);
    el.addEventListener('keyup', onCaret);
    el.addEventListener('click', onCaret);
    el._ipopKey = onKey;
    el._ipopInput = onInput;
    el._ipopCaret = onCaret;
  },

  chatUnbindComposer(el) {
    if (!el || !el._ipopComposerBound) return;
    if (el._ipopKey) el.removeEventListener('keydown', el._ipopKey);
    if (el._ipopInput) el.removeEventListener('input', el._ipopInput);
    if (el._ipopCaret) { el.removeEventListener('keyup', el._ipopCaret); el.removeEventListener('click', el._ipopCaret); }
    delete el._ipopCaret;
    delete el._ipopKey;
    delete el._ipopInput;
    delete el._ipopComposerBound;
  },

  resetComposer(el) {
    if (!el) return;
    el.value = '';
    el.style.height = 'auto';
  },

  placeContextMenu(el, clientX, clientY) {
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const w = rect.width > 0 ? rect.width : 180;
    const h = rect.height > 0 ? rect.height : 230;
    const pad = 12;

    let x = clientX;
    let y = clientY;

    // Flip or clamp if opening to the right would overflow viewport
    if (x + w > window.innerWidth - pad) {
      x = Math.max(pad, x - w);
    }
    // Flip or clamp if opening downwards would overflow viewport
    if (y + h > window.innerHeight - pad) {
      y = Math.max(pad, y - h);
    }

    el.style.position = 'fixed';
    el.style.left = x + 'px';
    el.style.top = y + 'px';
    el.style.right = 'auto';
    el.style.bottom = 'auto';
  },

  // ==========================================================================
  // Chat attachments — preview, zoom, document rendering
  // ==========================================================================

  // Like authBlob, but also reports the server's Content-Type and byte length so
  // the viewer can pick a renderer even when the DB row has a stale MIME type.
  // `status` is always reported so the caller can tell an expired token (401 — retry after a
  // refresh) from a genuinely absent file (404 — do not retry). status 0 means the request never
  // reached the server (offline, DNS, CORS), which is also worth one retry.
  async authBlob2(url, token) {
    try {
      const res = await fetch(url, { headers: token ? { Authorization: "Bearer " + token } : {} });
      if (!res.ok) return { url: null, type: "", size: 0, status: res.status };
      const blob = await res.blob();
      return { url: URL.createObjectURL(blob), type: blob.type || res.headers.get("content-type") || "", size: blob.size, status: res.status };
    } catch {
      return { url: null, type: "", size: 0, status: 0 };
    }
  },

  // Click a hidden <InputFile> by element reference. The old version hunted for
  // `.composer-wrap input[type=file]` via querySelector, which broke whenever the
  // composer markup moved.
  clickFileInput(el) {
    if (!el) return false;
    const input = el.tagName === "INPUT" ? el : el.querySelector('input[type="file"]');
    if (!input) return false;
    input.value = "";  // re-selecting the same file must still raise onchange
    input.click();
    return true;
  },

  openBlobTab(url) {
    if (url) window.open(url, "_blank", "noopener");
  },

  // ---- Image zoom / pan -----------------------------------------------------
  // State lives on the element so multiple viewers cannot collide. Transform is
  // applied directly (not through Blazor) to keep dragging at display framerate.
  _zoomApply(el) {
    const s = el._zoom;
    el.style.transform = `translate(${s.x}px, ${s.y}px) scale(${s.scale})`;
    el.style.cursor = s.scale > 1 ? (s.dragging ? "grabbing" : "grab") : "zoom-in";
  },
  _zoomClampPan(el) {
    const s = el._zoom;
    if (s.scale <= 1) { s.x = 0; s.y = 0; return; }
    // Keep at least part of the image inside its frame.
    const r = el.getBoundingClientRect();
    const maxX = Math.max(0, (r.width - el.clientWidth) / 2 + el.clientWidth * 0.25);
    const maxY = Math.max(0, (r.height - el.clientHeight) / 2 + el.clientHeight * 0.25);
    s.x = Math.max(-maxX, Math.min(maxX, s.x));
    s.y = Math.max(-maxY, Math.min(maxY, s.y));
  },
  zoomBind(el) {
    if (!el || el._zoom) return 1;
    el._zoom = { scale: 1, x: 0, y: 0, dragging: false, px: 0, py: 0 };
    el.style.transformOrigin = "center center";
    el.style.willChange = "transform";

    const onWheel = e => {
      e.preventDefault();
      const s = el._zoom;
      const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
      s.scale = Math.max(1, Math.min(8, s.scale * factor));
      if (s.scale === 1) { s.x = 0; s.y = 0; }
      window.ipop._zoomClampPan(el);
      window.ipop._zoomApply(el);
    };
    const onDown = e => {
      const s = el._zoom;
      if (s.scale <= 1) return;
      s.dragging = true; s.px = e.clientX; s.py = e.clientY;
      el.setPointerCapture?.(e.pointerId);
      window.ipop._zoomApply(el);
    };
    const onMove = e => {
      const s = el._zoom;
      if (!s.dragging) return;
      s.x += e.clientX - s.px; s.y += e.clientY - s.py;
      s.px = e.clientX; s.py = e.clientY;
      window.ipop._zoomClampPan(el);
      window.ipop._zoomApply(el);
    };
    const onUp = e => {
      const s = el._zoom;
      if (!s.dragging) return;
      s.dragging = false;
      el.releasePointerCapture?.(e.pointerId);
      window.ipop._zoomApply(el);
    };
    const onDbl = e => { e.preventDefault(); window.ipop.zoomToggle(el); };

    el.addEventListener("wheel", onWheel, { passive: false });
    el.addEventListener("pointerdown", onDown);
    el.addEventListener("pointermove", onMove);
    el.addEventListener("pointerup", onUp);
    el.addEventListener("pointercancel", onUp);
    el.addEventListener("dblclick", onDbl);
    el._zoomHandlers = { onWheel, onDown, onMove, onUp, onDbl };
    window.ipop._zoomApply(el);
    return 1;
  },
  zoomUnbind(el) {
    if (!el || !el._zoomHandlers) return;
    const h = el._zoomHandlers;
    el.removeEventListener("wheel", h.onWheel);
    el.removeEventListener("pointerdown", h.onDown);
    el.removeEventListener("pointermove", h.onMove);
    el.removeEventListener("pointerup", h.onUp);
    el.removeEventListener("pointercancel", h.onUp);
    el.removeEventListener("dblclick", h.onDbl);
    delete el._zoomHandlers;
    delete el._zoom;
  },
  // step > 0 zooms in, < 0 zooms out. Returns the new scale so Blazor can label the UI.
  zoomStep(el, step) {
    if (!el || !el._zoom) return 1;
    const s = el._zoom;
    s.scale = Math.max(1, Math.min(8, s.scale * (step > 0 ? 1.4 : 1 / 1.4)));
    if (s.scale === 1) { s.x = 0; s.y = 0; }
    window.ipop._zoomClampPan(el);
    window.ipop._zoomApply(el);
    return s.scale;
  },
  zoomReset(el) {
    if (!el || !el._zoom) return 1;
    el._zoom.scale = 1; el._zoom.x = 0; el._zoom.y = 0;
    window.ipop._zoomApply(el);
    return 1;
  },
  zoomToggle(el) {
    if (!el || !el._zoom) return 1;
    return el._zoom.scale > 1 ? window.ipop.zoomReset(el) : window.ipop.zoomStep(el, 1);
  },
  zoomGet(el) { return el && el._zoom ? el._zoom.scale : 1; },

  // ---- Lazy script loader --------------------------------------------------
  _scripts: {},
  loadScript(src) {
    if (this._scripts[src]) return this._scripts[src];
    this._scripts[src] = new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = src;
      s.async = true;
      s.onload = () => resolve(true);
      s.onerror = () => reject(new Error("failed to load " + src));
      document.head.appendChild(s);
    });
    return this._scripts[src];
  },

  // ---- DOCX preview --------------------------------------------------------
  // docx-preview renders real OOXML layout (styles, tables, images) into a host
  // element. It needs jszip alongside it. Both are pulled on first use only, so a
  // user who never opens a Word file pays nothing.
  async renderDocx(hostId, blobUrl) {
    const host = document.getElementById(hostId);
    if (!host || !blobUrl) return "no-host";
    try {
      if (!window.JSZip) await this.loadScript("https://cdn.jsdelivr.net/npm/jszip@3.10.1/dist/jszip.min.js");
      if (!window.docx) await this.loadScript("https://cdn.jsdelivr.net/npm/docx-preview@0.3.5/dist/docx-preview.min.js");
      if (!window.docx || !window.docx.renderAsync) return "no-lib";
      const blob = await (await fetch(blobUrl)).blob();
      host.innerHTML = "";
      await window.docx.renderAsync(blob, host, null, {
        className: "docx",
        inWrapper: true,
        ignoreWidth: false,
        ignoreHeight: true,
        breakPages: true,
        renderHeaders: true,
        renderFooters: true,
        useBase64URL: true
      });
      return "ok";
    } catch (e) {
      return "error:" + (e && e.message ? e.message : "unknown");
    }
  },

  // ---- Legacy .xls preview -------------------------------------------------
  // SheetJS is already loaded globally (index.html) and reads the old binary
  // format that RadzenSpreadsheet cannot open.
  async renderXls(hostId, blobUrl) {
    const host = document.getElementById(hostId);
    if (!host || !blobUrl) return "no-host";
    try {
      if (!window.XLSX) return "no-lib";
      const buf = await (await fetch(blobUrl)).arrayBuffer();
      const wb = XLSX.read(new Uint8Array(buf), { type: "array" });
      const draw = name => {
        host.querySelector(".xls-sheet").innerHTML = XLSX.utils.sheet_to_html(wb.Sheets[name], { editable: false });
        host.querySelectorAll(".xls-sheet table").forEach(t => { t.style.width = "100%"; t.style.borderCollapse = "collapse"; t.style.fontSize = "12px"; });
        host.querySelectorAll(".xls-sheet td, .xls-sheet th").forEach(c => { c.style.border = "1px solid var(--line-soft)"; c.style.padding = "6px 8px"; });
      };
      const tabs = wb.SheetNames.map(n => `<button class="xls-tab" data-sheet="${n}">${n}</button>`).join("");
      host.innerHTML = `<div class="xls-tabs">${tabs}</div><div class="xls-sheet"></div>`;
      const buttons = Array.from(host.querySelectorAll(".xls-tab"));
      buttons.forEach(b => b.addEventListener("click", () => {
        buttons.forEach(x => x.classList.toggle("on", x === b));
        draw(b.dataset.sheet);
      }));
      if (buttons[0]) buttons[0].classList.add("on");
      draw(wb.SheetNames[0]);
      return "ok";
    } catch (e) {
      return "error:" + (e && e.message ? e.message : "unknown");
    }
  },

  // Print whatever the viewer currently shows.
  printFrame(id) {
    const f = document.getElementById(id);
    if (f && f.contentWindow) { try { f.contentWindow.focus(); f.contentWindow.print(); return; } catch {} }
    window.print();
  },

  // ---- Viewer keyboard shortcuts -------------------------------------------
  // Esc / arrows are bound on document because the overlay is not focusable and
  // clicks inside iframes (PDF) would otherwise swallow keydown.
  bindViewerKeys(dotNet) {
    if (this._viewerKeys) this.unbindViewerKeys();
    this._viewerKeys = e => {
      let name = null;
      if (e.key === "Escape") name = "Escape";
      else if (e.key === "ArrowRight") name = "Next";
      else if (e.key === "ArrowLeft") name = "Prev";
      else if (e.key === "+" || e.key === "=") name = "ZoomIn";
      else if (e.key === "-" || e.key === "_") name = "ZoomOut";
      else if (e.key === "0") name = "ZoomReset";
      if (!name) return;
      e.preventDefault();
      dotNet.invokeMethodAsync("OnViewerKey", name);
    };
    document.addEventListener("keydown", this._viewerKeys);
  },
  unbindViewerKeys() {
    if (!this._viewerKeys) return;
    document.removeEventListener("keydown", this._viewerKeys);
    this._viewerKeys = null;
  },

  // Scroll a message into view inside the chat list and highlight it (jump from a reply
  // quote, search results, pins). The glow starts once the smooth scroll has settled: a long
  // jump used to spend the whole flash still scrolling, so nobody saw which message it was.
  scrollToMessage(container, messageId) {
    if (!container) return false;
    const el = container.querySelector('[data-msg-id="' + messageId + '"]');
    if (!el) return false;
    clearTimeout(el._hlTimer);
    el.classList.remove("highlighted");
    el.scrollIntoView({ behavior: "smooth", block: "center" });
    const scroller = el.closest(".messages, .chat-messages, [data-scroll-root]") || container;
    let last = -1, still = 0;
    const settle = () => {
      const top = scroller.scrollTop;
      still = top === last ? still + 1 : 0;
      last = top;
      if (still < 3) { requestAnimationFrame(settle); return; }   // ~3 frames without movement
      void el.offsetWidth;   // restart the animation if it was already running
      el.classList.add("highlighted");
      el._hlTimer = setTimeout(() => el.classList.remove("highlighted"), 2400);
    };
    requestAnimationFrame(settle);
    return true;
  },

  // Scroll an element into view (deep links: /projects/{id}, /chat/{room}).
  scrollIntoView(selector) {
    const el = document.querySelector(selector);
    if (!el) return false;
    el.scrollIntoView({ behavior: "smooth", block: "start" });
    return true;
  },

  // Distance from the bottom of a scroller, used to decide between auto-scroll
  // and showing the "jump to latest" button.
  scrollInfo(el) {
    if (!el) return { fromBottom: 0, scrollTop: 0, scrollHeight: 0 };
    return {
      fromBottom: el.scrollHeight - el.scrollTop - el.clientHeight,
      scrollTop: el.scrollTop,
      scrollHeight: el.scrollHeight
    };
  },
  setScrollTop(el, top) { if (el) el.scrollTop = top; },

  // Watch a scroller and notify .NET only when it crosses `threshold` px from the
  // bottom. A Blazor @onscroll handler fires on every scroll tick and each tick costs
  // an interop round-trip plus a possible re-render, which saturates the main thread
  // and makes the list feel stuck. Throttling to one rAF and reporting only state
  // *changes* keeps scrolling on the compositor where it belongs.
  bindScrollWatch(el, dotNet, threshold) {
    if (!el || !dotNet) return;
    if (el._ipopScroll) this.unbindScrollWatch(el);
    let queued = false;
    let last = null;
    let nearTop = true; // not before the first real scroll: binding happens before the jump to the bottom
    const evaluate = () => {
      queued = false;
      const gap = el.scrollHeight - el.scrollTop - el.clientHeight;
      const away = gap > (threshold || 400);
      const near = gap < 150;
      el._ipopNear = near;
      // Near the top: ask for older history (once per approach, re-armed on leaving).
      const top = el.scrollTop < 300 && el.scrollHeight > el.clientHeight + 10;
      if (top && !nearTop) dotNet.invokeMethodAsync("OnNearTop");
      nearTop = top;
      const state = (away ? 2 : 0) + (near ? 1 : 0);
      if (state === last) return;
      last = state;
      dotNet.invokeMethodAsync("OnScrollStateChanged", away, near);
    };
    const onScroll = () => {
      if (queued) return;
      queued = true;
      requestAnimationFrame(evaluate);
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    el._ipopScroll = onScroll;
    evaluate();
  },
  unbindScrollWatch(el) {
    if (!el || !el._ipopScroll) return;
    el.removeEventListener("scroll", el._ipopScroll);
    delete el._ipopScroll;
    delete el._ipopNear;
  },

  // ipop-22: unbind project/task chip click delegation
  unbindMentionChips(container) {
    if (!container || !container._ipopMentionChip) return;
    container.removeEventListener('click', container._ipopMentionChip);
    if (container._ipopMentionKey) container.removeEventListener('keydown', container._ipopMentionKey);
    delete container._ipopMentionChip;
    delete container._ipopMentionKey;
  },

  // Auto-grow the composer textarea up to its CSS max-height.
  autoGrow(el) {
    if (!el) return;
    if (!el.value) {
      el.style.height = "auto";
      return;
    }
    el.style.height = "auto";
    el.style.height = Math.min(el.scrollHeight, 130) + "px";
  },

  // ── DraftStore helpers (Req 18) ──
  draftGet(roomId) { try { return localStorage.getItem("ipop.chat.draft." + roomId); } catch { return null; } },
  draftSet(roomId, text, iso) { try { localStorage.setItem("ipop.chat.draft." + roomId, text); const idx = JSON.parse(localStorage.getItem("ipop.chat.drafts") || "{}"); idx[roomId] = iso; localStorage.setItem("ipop.chat.drafts", JSON.stringify(idx)); } catch(e) { throw e; } },
  draftRemove(roomId) { try { localStorage.removeItem("ipop.chat.draft." + roomId); const idx = JSON.parse(localStorage.getItem("ipop.chat.drafts") || "{}"); delete idx[roomId]; localStorage.setItem("ipop.chat.drafts", JSON.stringify(idx)); } catch {} },
  draftList() { try { return JSON.parse(localStorage.getItem("ipop.chat.drafts") || "{}"); } catch { return {}; } },

  // Projects List: per-user, per-project field column prefs ({ hidden: [fieldId], widths: { fieldId: px } }).
  prefGet(key) { try { return localStorage.getItem("ipop.pref." + key); } catch { return null; } },
  prefSet(key, json) { try { localStorage.setItem("ipop.pref." + key, json); } catch {} },

  // ── Composer caret helpers (Req 2/3) ──
  caretOf(el) { if (!el) return { start: 0, end: 0 }; return { start: el.selectionStart ?? 0, end: el.selectionEnd ?? 0 }; },
  setCaret(el, start, end) { if (!el) return; el.focus(); try { el.setSelectionRange(start, end ?? start); } catch {} },
  chatComposerSyncScroll(ta, overlay) { if (!ta || !overlay) return; overlay.scrollTop = ta.scrollTop; overlay.scrollLeft = ta.scrollLeft; },
  scrollItemIntoView(container, itemId) { if (!container || !itemId) return; const el = container.querySelector("#" + CSS.escape(itemId)); if (el) el.scrollIntoView({ block: "nearest" }); },

  // ipop-22: delegation for project/task mention chips rendered as <span class="mention project-chip" data-project-id="..."> or task-chip
  bindMentionChips(container, dotNet) {
    if (!container || !dotNet) return;
    if (container._ipopMentionChip) container.removeEventListener('click', container._ipopMentionChip);
    if (container._ipopMentionKey) container.removeEventListener('keydown', container._ipopMentionKey);
    const h = (e) => {
      const p = e.target.closest('.project-chip[data-project-id]');
      if (p) {
        const id = p.getAttribute('data-project-id');
        if (id) { e.preventDefault(); dotNet.invokeMethodAsync('OnProjectChipClick', id); }
        return;
      }
      const t = e.target.closest('.task-chip[data-task-id]');
      if (t) {
        const id = t.getAttribute('data-task-id');
        if (id) { e.preventDefault(); dotNet.invokeMethodAsync('OnTaskChipClick', id); }
        return;
      }
      // @person mention → open their profile.
      const u = e.target.closest('.mention[data-nik]');
      if (u) {
        const nik = u.getAttribute('data-nik');
        if (nik) { e.preventDefault(); e.stopPropagation(); dotNet.invokeMethodAsync('OnMentionUserClick', nik); }
      }
    };
    // Keyboard: Enter/Space on a focused @person mention behaves like a click.
    const k = (e) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const u = e.target.closest && e.target.closest('.mention[data-nik]');
      if (u) { e.preventDefault(); u.click(); }
    };
    container.addEventListener('click', h);
    container.addEventListener('keydown', k);
    container._ipopMentionChip = h;
    container._ipopMentionKey = k;
  },

  // Ctrl+K / Cmd+K quick switcher — global document listener so it fires from anywhere.
  // We ignore the event when focus is already inside an input/textarea/contenteditable.
  chatBindQuickSwitcher(dotNet) {
    if (!dotNet) return;
    if (document._ipopQs) this.chatUnbindQuickSwitcher();
    const h = (e) => {
      const isK = e.key.toLowerCase() === 'k';
      if (!isK) return;
      if (e.ctrlKey || e.metaKey) {
        const tag = (e.target && e.target.tagName) || '';
        const editable = e.target && (e.target.isContentEditable || tag === 'INPUT' || tag === 'TEXTAREA');
        if (!editable) {
          e.preventDefault();
          dotNet.invokeMethodAsync('OnQuickSwitcher');
        }
      }
    };
    document.addEventListener('keydown', h, true);
    document._ipopQs = h;
  },
  chatUnbindQuickSwitcher() {
    if (!document._ipopQs) return;
    document.removeEventListener('keydown', document._ipopQs, true);
    delete document._ipopQs;
  },

  // Kanban drag (mockup parity): keep the browser's native drag image and just dim the
  // source card a tick after dragstart, so the snapshot is taken at full opacity.
  initKanbanDragFollower() {
    if (window._ipopKanbanDragInited) return;
    window._ipopKanbanDragInited = true;

    document.addEventListener('dragstart', (e) => {
      const card = e.target.closest && e.target.closest('.kanban-task-card');
      if (!card) return;
      if (e.dataTransfer) { try { e.dataTransfer.effectAllowed = 'move'; } catch {} }
      setTimeout(() => card.classList.add('is-source-dragged'), 0);
    });

    let lane = null;
    const setLane = (el) => {
      if (lane === el) return;
      if (lane) lane.classList.remove('drag-over');
      lane = el;
      if (lane) lane.classList.add('drag-over');
    };
    document.addEventListener('dragover', (e) => {
      if (!document.querySelector('.kanban-task-card.is-source-dragged')) return;
      setLane(e.target.closest ? e.target.closest('.kanban-column[data-lane]') : null);
    });
    const clear = () => {
      setLane(null);
      document.querySelectorAll('.kanban-task-card.is-source-dragged').forEach(c => {
        c.classList.remove('is-source-dragged');
      });
    };
    document.addEventListener('drop', clear);
    document.addEventListener('dragend', clear);
  },
  // ---- ONLYOFFICE Document Editor ------------------------------------------
  _onlyOfficeInstances: {},

  async initOnlyOffice(placeholderId, serverUrl, config, dotNetHelper, isPhone = false) {
    config = { ...config, type: isPhone ? "mobile" : "desktop" };
    const el = document.getElementById(placeholderId);
    if (!el) return false;

    const scriptUrl = `${serverUrl.replace(/\/+$/, '')}/web-apps/apps/api/documents/api.js`;
    if (!window.DocsAPI) {
      try {
        await this.loadScript(scriptUrl);
      } catch (e) {
        console.error("Gagal memuat OnlyOffice API script:", e);
        return false;
      }
    }

    if (!window.DocsAPI || !window.DocsAPI.DocEditor) {
      console.error("DocsAPI.DocEditor tidak tersedia.");
      return false;
    }

    if (this._onlyOfficeInstances[placeholderId]) {
      try { this._onlyOfficeInstances[placeholderId].destroyEditor(); } catch {}
      delete this._onlyOfficeInstances[placeholderId];
    }

    el.innerHTML = "";

    // Hubungkan DocsAPI events ke Blazor jika dotNetHelper diberikan
    if (dotNetHelper) {
      config.events = config.events || {};
      const notifyReady = () => {
        try { dotNetHelper.invokeMethodAsync("OnEditorReady"); } catch {}
      };
      config.events.onDocumentReady = notifyReady;
      config.events.onAppReady = notifyReady;
      config.events.onDocumentStateChange = (event) => {
        try { dotNetHelper.invokeMethodAsync("OnDocumentStateChange", !!(event && event.data)); } catch {}
      };
      config.events.onError = (event) => {
        let msg = "The document editor ran into a problem.";
        if (event) {
          if (typeof event.data === "string") {
            msg = event.data;
          } else if (event.data && typeof event.data === "object") {
            msg = event.data.description || event.data.message || JSON.stringify(event.data);
          } else if (event.description || event.message) {
            msg = event.description || event.message;
          }
        }
        try { dotNetHelper.invokeMethodAsync("OnEditorError", msg); } catch {}
      };
    }

    try {
      this._onlyOfficeInstances[placeholderId] = new window.DocsAPI.DocEditor(placeholderId, config);
      return true;
    } catch (err) {
      console.error("Gagal inisialisasi DocEditor:", err);
      return false;
    }
  },

  destroyOnlyOffice(placeholderId) {
    if (this._onlyOfficeInstances[placeholderId]) {
      try { this._onlyOfficeInstances[placeholderId].destroyEditor(); } catch {}
      delete this._onlyOfficeInstances[placeholderId];
    }
  },

  closeOrNavigate(fallbackUrl) {
    try {
      window.close();
    } catch {}
    setTimeout(() => {
      if (!window.closed) {
        window.location.href = fallbackUrl || '/files';
      }
    }, 200);
  },

  // ---- Mail Rich Text Editor (RTE) -----------------------------------------
  rteFormat(el, command, value) {
    if (!el) return false;
    el.focus();
    document.execCommand(command, false, value || null);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    return true;
  },

  rteGetHtml(el) {
    return el ? el.innerHTML : "";
  },

  rteSetHtml(el, html) {
    if (!el) return;
    if (el.innerHTML !== (html || "")) {
      el.innerHTML = html || "";
    }
  },

  rteInsertTable(el, rows, cols) {
    if (!el) return;
    el.focus();
    let r = Math.max(1, parseInt(rows) || 2);
    let c = Math.max(1, parseInt(cols) || 2);
    let table = '<table style="width:100%;border-collapse:collapse;margin:8px 0;"><tbody>';
    for (let i = 0; i < r; i++) {
      table += '<tr>';
      for (let j = 0; j < c; j++) {
        table += '<td style="border:1px solid var(--line);padding:6px 8px;">&nbsp;</td>';
      }
      table += '</tr>';
    }
    table += '</tbody></table><p></p>';
    document.execCommand("insertHTML", false, table);
    el.dispatchEvent(new Event("input", { bubbles: true }));
  },

  rteInsertImage(el, src) {
    if (!el || !src) return;
    el.focus();
    const html = `<img src="${src}" style="max-width:100%;border-radius:6px;margin:6px 0;" alt="Image" /><p></p>`;
    document.execCommand("insertHTML", false, html);
    el.dispatchEvent(new Event("input", { bubbles: true }));
  },

  // ---- Audio / Voice Note Recording -----------------------------------------
  _mediaRecorder: null,
  _audioChunks: [],
  _audioStream: null,
  _audioStartedAt: 0,
  _audioLimitTimer: null,

  async startAudioRecord(dotNet) {
    try {
      this._audioDotNet = dotNet;
      this._audioChunks = [];
      this._audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      this._mediaRecorder = new MediaRecorder(this._audioStream);
      this._mediaRecorder.ondataavailable = e => {
        if (e.data && e.data.size > 0) this._audioChunks.push(e.data);
      };
      this._mediaRecorder.start(200);
      this._audioStartedAt = Date.now();
      this._audioLimitTimer = setTimeout(() => this.stopAudioRecord(this._audioDotNet), 120000);
      return true;
    } catch (e) {
      console.warn("Audio record permission error:", e);
      return false;
    }
  },

  stopAudioRecord(dotNet) {
    clearTimeout(this._audioLimitTimer);
    this._audioLimitTimer = null;
    return new Promise(resolve => {
      if (!this._mediaRecorder || this._mediaRecorder.state === "inactive") {
        resolve(null);
        return;
      }
      dotNet = dotNet || this._audioDotNet;
      this._mediaRecorder.onstop = async () => {
        const durationMs = Math.min(Date.now() - this._audioStartedAt, 120000);
        const audioBlob = new Blob(this._audioChunks, { type: "audio/webm" });
        if (this._audioStream) {
          this._audioStream.getTracks().forEach(t => t.stop());
          this._audioStream = null;
        }
        const buf = await audioBlob.arrayBuffer();
        const bytes = new Uint8Array(buf);
        let binary = "";
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk) {
          binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        const b64 = btoa(binary);
        if (dotNet) {
          dotNet.invokeMethodAsync("OnVoiceRecorded", b64, audioBlob.size, durationMs);
        }
        resolve(b64);
      };
      this._mediaRecorder.stop();
    });
  },

  cancelAudioRecord() {
    clearTimeout(this._audioLimitTimer);
    this._audioLimitTimer = null;
    if (this._mediaRecorder && this._mediaRecorder.state !== "inactive") {
      this._mediaRecorder.onstop = null;
      this._mediaRecorder.stop();
    }
    if (this._audioStream) {
      this._audioStream.getTracks().forEach(t => t.stop());
      this._audioStream = null;
    }
    this._audioChunks = [];
  },

  // ---- Mail rich-text editor (Jodit, vendored in lib/jodit) ------------------
  // Jodit's own toolbar is hidden; MailRteEditor.razor draws an Apple-style toolbar and
  // drives the editor through these calls. Selection state flows back via OnState.
  rte: {
    _eds: {},
    _maxDataImage: 10 * 1024 * 1024 / 3 * 4 + 64, // mirrors MailHtml.MaxDataImageChars
    _loading: null,
    load() {
      if (window.Jodit) return Promise.resolve();
      if (!this._loading) {
        if (!document.querySelector("link[data-jodit]")) {
          const l = document.createElement("link");
          l.rel = "stylesheet"; l.href = "lib/jodit/jodit.fat.min.css"; l.dataset.jodit = "1";
          document.head.appendChild(l);
        }
        this._loading = ipop.loadScript("lib/jodit/jodit.fat.min.js");
      }
      return this._loading;
    },
    async create(host, id, html, placeholder, dotNet) {
      if (!host) return false;
      await this.load();
      this.destroy(id);
      host.innerHTML = "";
      const area = document.createElement("textarea");
      host.appendChild(area);
      const ed = Jodit.make(area, {
        toolbar: false, statusbar: false, showCharsCounter: false, showWordsCounter: false,
        showXPathInStatusbar: false, showPlaceholder: true, placeholder: placeholder || "",
        language: "en", height: "auto", minHeight: 220, iframe: false, beautifyHTML: false,
        defaultFontSizePoints: "pt", spellcheck: ipop.spellcheck(), toolbarInlineForSelection: false,
        askBeforePasteFromWord: false, askBeforePasteHTML: false,
        defaultActionOnPasteFromWord: "insert_as_html", defaultActionOnPaste: "insert_as_html",
        processPasteFromWord: true, nl2brInPlainText: true,
        uploader: { insertImageAsBase64URI: true, imagesExtensions: ["jpg", "jpeg", "png", "gif", "webp"] },
        enableDragAndDropFileToEditor: true, tableAllowCellResize: true, tableAllowCellSelection: true,
        disablePlugins: ["about", "print", "preview", "powered-by-jodit", "speech-recognize", "ai-assistant", "xpath", "stat", "source", "fullsize"],
      });
      const st = { ed, dotNet, range: null, lastState: "", lastValue: "", changeT: 0, stateT: 0 };
      this._eds[id] = st;
      // #14: pasted screenshots show a placeholder, then fade in (size/format still checked on change).
      ipop.rteBindImagePaste(ed.editor, () => { try { ed.synchronizeValues(); ed.e.fire("change"); } catch { } });
      // Stored images (drafts, reply/forward quotes) show through blob: URLs in this tab only;
      // every value read from the editor carries the /api/attachments/{id} src again.
      ed.e.on("afterGetValueFromEditor", d => { if (d && typeof d.value === "string") d.value = ipop.mailImages.restoreSrc(d.value); });
      ed.value = html || "";
      try { ed.history.clear(); } catch { }
      st.lastValue = ed.value;
      ipop.mailImages.hydrate(ed.editor);

      // Same limits as the server sanitizer, so the author sees the drop instead of the recipient.
      const dropBadImages = () => {
        let dropped = 0;
        ed.editor.querySelectorAll('img[src^="data:"]').forEach(img => {
          const src = img.getAttribute("src") || "";
          if (src.length > this._maxDataImage || !/^data:image\/(png|jpeg|gif|webp);/i.test(src)) { img.remove(); dropped++; }
        });
        if (dropped) dotNet.invokeMethodAsync("OnNotice", "The image is larger than 10 MB or its format isn't supported, so it wasn't inserted.").catch(() => { });
      };
      // Plain text (Notepad): one paragraph per line, blank lines kept, nothing added in between.
      ed.e.on("beforePaste", ev => {
        const dt = ev && ev.clipboardData;
        if (!dt) return;
        const types = Array.from(dt.types || []);
        if (types.includes("text/html") || types.includes("Files")) return;
        const text = dt.getData("text/plain");
        if (!text) return;
        const esc = l => l.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
          .replace(/\t/g, "\u00a0\u00a0\u00a0\u00a0").replace(/ {2}/g, " \u00a0");
        const lines = text.replace(/\r\n?/g, "\n").split("\n");
        if (lines.length > 1 && lines[lines.length - 1] === "") lines.pop();
        ev.preventDefault();
        ed.s.insertHTML(lines.length === 1 ? esc(lines[0])
          : lines.map(l => l ? `<p>${esc(l)}</p>` : "<p><br></p>").join(""));
        return false;
      });
      ed.e.on("change", () => {
        clearTimeout(st.changeT);
        st.changeT = setTimeout(() => {
          dropBadImages();
          this._pinMargins(ed);
          const v = ed.value;
          if (v === st.lastValue) return;
          st.lastValue = v;
          dotNet.invokeMethodAsync("OnChange", v).catch(() => { });
        }, 200);
        this._queueState(id);
      });
      ed.e.on("changeSelection keyup mouseup focus", () => this._queueState(id));
      ed.e.on("blur", () => this._saveRange(st));
      ed.e.on("keydown", e => {
        if (!(e.ctrlKey || e.metaKey)) return;
        if (e.key === "Enter") {
          e.preventDefault();
          dotNet.invokeMethodAsync("OnSubmitShortcut").catch(() => { });
          return false;
        }
        if (e.key === "k" || e.key === "K") {
          e.preventDefault();
          this._saveRange(st);
          dotNet.invokeMethodAsync("OnLinkShortcut").catch(() => { });
          return false;
        }
      });
      return true;
    },
    // Outlook-style: Enter starts a new paragraph with no gap. The margins are written inline
    // so the recipient's mail client (whose default <p> margin is 1em) shows the same spacing.
    _pinMargins(ed) {
      ed.editor.querySelectorAll("p").forEach(p => {
        if (!p.style.marginTop) p.style.marginTop = "0";
        if (!p.style.marginBottom) p.style.marginBottom = "0";
      });
    },
    _blocksInSelection(ed) {
      const Dom = Jodit.modules.Dom;
      let range = null;
      try { const sel = ed.ew.getSelection(); if (sel && sel.rangeCount) range = sel.getRangeAt(0); } catch { }
      if (!range || !ed.editor.contains(range.commonAncestorContainer)) return [];
      const sel = "p, h1, h2, h3, h4, blockquote, pre, li";
      if (range.collapsed) {
        const n = range.startContainer;
        const b = Dom.closest(n.nodeType === 1 ? n : n.parentElement, sel.split(", "), ed.editor);
        return b ? [b] : [];
      }
      return Array.from(ed.editor.querySelectorAll(sel)).filter(b => range.intersectsNode(b));
    },
    destroy(id) {
      const st = this._eds[id];
      if (!st) return;
      clearTimeout(st.changeT); clearTimeout(st.stateT);
      try { st.ed.destruct(); } catch { }
      delete this._eds[id];
    },
    _saveRange(st) {
      try {
        const sel = st.ed.ew.getSelection();
        if (sel && sel.rangeCount && st.ed.editor.contains(sel.getRangeAt(0).commonAncestorContainer))
          st.range = sel.getRangeAt(0).cloneRange();
      } catch { }
    },
    // Toolbar controls (selects, popovers) take focus; put the caret back where it was first.
    _focus(st) {
      const ed = st.ed;
      if (ed.s.isFocused()) return;
      ed.s.focus();
      if (st.range) { try { ed.s.selectRange(st.range); } catch { } }
    },
    _flush(id) {
      const st = this._eds[id];
      if (!st) return;
      st.ed.synchronizeValues();
      st.ed.e.fire("change");
      this._queueState(id);
    },
    _cell(ed) {
      const cur = ed.s.current();
      return cur ? Jodit.modules.Dom.closest(cur, ["td", "th"], ed.editor) : null;
    },
    exec(id, cmd, arg) {
      const st = this._eds[id];
      if (!st) return;
      const ed = st.ed;
      this._focus(st);
      if (cmd === "fontsize" && arg && !/pt$/.test(arg)) arg = arg + "pt";
      if (/^table/.test(cmd)) {
        // Jodit's table commands act on selected cells; with none, use the cell under the caret.
        const tm = ed.getInstance("Table", ed.o);
        if (!tm.getAllSelectedCells().length) {
          const cell = this._cell(ed);
          if (!cell) return;
          tm.addSelection(cell);
        }
        ed.execCommand(cmd);
        try { tm.getAllSelectedCells().forEach(c => tm.removeSelection(c)); } catch { }
      } else if (cmd === "copyformat") {
        Jodit.defaultOptions.controls.copyformat.exec(ed, ed.s.current() || ed.editor.firstChild, { button: { update() { } } });
      } else if (cmd === "spaceAfter") {
        this._pinMargins(ed);
        this._blocksInSelection(ed).forEach(b => { b.style.marginBottom = arg === "0" ? "0" : arg + "pt"; });
        ed.synchronizeValues();
      } else if (cmd === "unlink") {
        const cur = ed.s.current();
        const a = cur && Jodit.modules.Dom.closest(cur, "a", ed.editor);
        if (a) Jodit.modules.Dom.unwrap(a);
      } else {
        ed.execCommand(cmd, false, arg ?? null);
      }
      this._flush(id);
    },
    link(id, url, text) {
      const st = this._eds[id];
      if (!st || !url) return;
      const ed = st.ed;
      this._focus(st);
      const cur = ed.s.current();
      const a = cur && Jodit.modules.Dom.closest(cur, "a", ed.editor);
      if (a) {
        a.setAttribute("href", url);
        if (text && text !== a.textContent) a.textContent = text;
      } else if (ed.s.isCollapsed()) {
        const el = ed.createInside.element("a");
        el.setAttribute("href", url);
        el.textContent = text || url;
        ed.s.insertNode(el, false, true);
        ed.s.setCursorAfter(el);
      } else {
        ed.execCommand("createLink", false, url);
      }
      this._flush(id);
    },
    linkInfo(id) {
      const st = this._eds[id];
      if (!st) return null;
      const ed = st.ed;
      let range = null;
      try { const sel = ed.ew.getSelection(); if (sel && sel.rangeCount) range = sel.getRangeAt(0); } catch { }
      if (!range || !ed.editor.contains(range.commonAncestorContainer)) range = st.range;
      else st.range = range.cloneRange();
      const node = range ? range.commonAncestorContainer : null;
      const a = node ? Jodit.modules.Dom.closest(node, "a", ed.editor) : null;
      return { href: a ? a.getAttribute("href") || "" : "", text: a ? a.textContent : (range ? range.toString() : "") };
    },
    insertTable(id, rows, cols) {
      const r = Math.min(Math.max(1, rows | 0), 20), c = Math.min(Math.max(1, cols | 0), 12);
      let h = '<table style="border-collapse: collapse; width: 100%"><tbody>';
      for (let i = 0; i < r; i++) {
        h += "<tr>";
        for (let j = 0; j < c; j++) h += '<td style="border: 1px solid #c7c7cc; padding: 6px 8px; vertical-align: top"><br></td>';
        h += "</tr>";
      }
      this.insertHtml(id, h + "</tbody></table><p><br></p>");
    },
    insertImage(id, src) {
      if (!src) return;
      const safe = String(src).replace(/"/g, "&quot;");
      this.insertHtml(id, `<img src="${safe}" style="max-width: 100%; height: auto" alt="">`);
    },
    saveCaret(id) {
      const st = this._eds[id];
      if (st) this._saveRange(st);
    },
    insertText(id, text) {
      const st = this._eds[id];
      if (!st) return;
      this._focus(st);
      const safe = document.createElement("span");
      safe.textContent = text || "";
      st.ed.s.insertHTML(safe.innerHTML);
      this._flush(id);
    },
    // Templates/signatures go through insertHTML so Ctrl+Z can take them back out.
    insertHtml(id, html) {
      const st = this._eds[id];
      if (!st) return;
      this._focus(st);
      st.ed.s.insertHTML(html || "");
      this._flush(id);
      ipop.mailImages.hydrate(st.ed.editor);
    },
    // Replaces the whole document. Opening a draft/new message restarts history; applying a
    // template keeps it so Ctrl+Z brings the previous text back.
    setHtml(id, html, keepHistory) {
      const st = this._eds[id];
      if (!st || st.ed.value === (html || "")) return;
      st.ed.value = html || "";
      st.lastValue = st.ed.value;
      if (!keepHistory) { try { st.ed.history.clear(); } catch { } }
      this._queueState(id);
      ipop.mailImages.hydrate(st.ed.editor);
    },
    getHtml(id) {
      const st = this._eds[id];
      return st ? st.ed.value : "";
    },
    focus(id) {
      const st = this._eds[id];
      if (st) this._focus(st);
    },
    _queueState(id) {
      const st = this._eds[id];
      if (!st) return;
      clearTimeout(st.stateT);
      st.stateT = setTimeout(() => this._reportState(id), 120);
    },
    _reportState(id) {
      const st = this._eds[id];
      if (!st) return;
      const ed = st.ed;
      const q = c => { try { return document.queryCommandState(c); } catch { return false; } };
      const cur = ed.s.current();
      const el = cur ? (cur.nodeType === 1 ? cur : cur.parentElement) : null;
      const inside = !!(el && ed.editor.contains(el));
      let font = "", size = "", block = "p", lineHeight = "", spaceAfter = "0";
      if (inside) {
        const cs = getComputedStyle(el);
        font = (cs.fontFamily || "").split(",")[0].replace(/["']/g, "").trim();
        const px = parseFloat(cs.fontSize);
        if (px) size = String(Math.round(px * 0.75));
        const b = Jodit.modules.Dom.closest(el, Jodit.modules.Dom.isBlock, ed.editor);
        if (b) {
          block = b.nodeName.toLowerCase();
          lineHeight = b.style.lineHeight || "";
          const mb = parseFloat(b.style.marginBottom || "0");
          spaceAfter = mb ? String(Math.round(mb)) : "0";
        }
      }
      const hist = ed.history;
      const state = {
        bold: q("bold"), italic: q("italic"), underline: q("underline"), strike: q("strikeThrough"),
        sub: q("subscript"), sup: q("superscript"),
        ul: q("insertUnorderedList"), ol: q("insertOrderedList"),
        align: q("justifyCenter") ? "center" : q("justifyRight") ? "right" : q("justifyFull") ? "full" : "left",
        font, size, block, lineHeight, spaceAfter,
        inTable: !!(inside && Jodit.modules.Dom.closest(el, ["td", "th"], ed.editor)),
        inLink: !!(inside && Jodit.modules.Dom.closest(el, "a", ed.editor)),
        painter: ed.buffer.exists("copy-format"),
        canUndo: !!(hist && hist.canUndo && hist.canUndo()),
        canRedo: !!(hist && hist.canRedo && hist.canRedo()),
      };
      const json = JSON.stringify(state);
      if (json === st.lastState) return;
      st.lastState = json;
      st.dotNet.invokeMethodAsync("OnState", state).catch(() => { });
    },
  },

  // ---- Mail page keyboard shortcuts ------------------------------------------
  // Plain keys only, and only while nothing editable has focus and no composer or dialog is in
  // front. Escape is also passed through when an overlay (preview, shortcut sheet) is open.
  isEditableTarget(el) {
    if (!el || el === document.body) return false;
    const tag = el.tagName;
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return true;
    return !!(el.isContentEditable || (el.closest && el.closest("[contenteditable='true'], .jodit-wysiwyg")));
  },
  mailBindShortcuts(dotNet) {
    this.mailUnbindShortcuts();
    this.mailBindLightbox();
    const handler = e => {
      if (e.ctrlKey || e.metaKey || e.altKey || e.isComposing || e.defaultPrevented) return;
      const overlay = document.querySelector(".mail-preview-overlay, .mail-kbd-sheet");
      const blocker = document.querySelector(".syno-modal-overlay, .outlook-composer:not(.minimized), [aria-modal='true']:not(.mail-preview-overlay):not(.mail-kbd-sheet)");
      const editing = ipop.isEditableTarget(e.target) || ipop.isEditableTarget(document.activeElement);
      const key = e.key;
      if (key === "Escape") {
        if (overlay || (!blocker && !editing)) dotNet.invokeMethodAsync("OnShortcut", "Escape").catch(() => { });
        return;
      }
      if (overlay || blocker || editing) return;
      let k = null;
      if (key === "Delete" || key === "#") k = "Delete";
      else if (key === "?") k = "?";
      else if (key.length === 1 && "craefjkl".includes(key.toLowerCase()) && !e.shiftKey) k = key.toLowerCase();
      if (!k) return;
      e.preventDefault();
      dotNet.invokeMethodAsync("OnShortcut", k).catch(() => { });
    };
    this._mailKeys = handler;
    document.addEventListener("keydown", handler);
  },
  // Click an image inside a message body to view it full size (Esc / click outside to close).
  mailBindLightbox() {
    if (this._mailLbClick) return;
    const sel = ".mail-page .detail-scroll-area .body img, .mail-page .detail-scroll-area .thr-body img, .mail-page .mail-html-body img, .mail-page .mlx-rscroll .thr-body img";
    this._mailLbClick = e => {
      const img = e.target instanceof Element ? e.target.closest("img") : null;
      if (!img || !img.matches(sel) || img.closest("a")) return;
      e.preventDefault();
      this.mailOpenLightbox(img.currentSrc || img.src, img.alt || "");
    };
    document.addEventListener("click", this._mailLbClick);
  },
  mailOpenLightbox(src, alt) {
    this.mailCloseLightbox();
    const ov = document.createElement("div");
    ov.className = "mail-lightbox";
    ov.setAttribute("role", "dialog");
    ov.setAttribute("aria-modal", "true");
    ov.setAttribute("aria-label", "Image preview");
    const img = document.createElement("img");
    img.src = src; img.alt = alt;
    let zoomed = false;
    img.addEventListener("click", e => { e.stopPropagation(); zoomed = !zoomed; ov.classList.toggle("zoomed", zoomed); });
    const bar = document.createElement("div");
    bar.className = "mail-lightbox-bar";
    const dl = document.createElement("a");
    dl.href = src; dl.download = alt || "image"; dl.textContent = "Download"; dl.className = "mail-lightbox-btn";
    dl.addEventListener("click", e => e.stopPropagation());
    const close = document.createElement("button");
    close.type = "button"; close.className = "mail-lightbox-btn"; close.setAttribute("aria-label", "Close"); close.textContent = "Close";
    close.addEventListener("click", () => this.mailCloseLightbox());
    bar.append(dl, close);
    ov.append(img, bar);
    ov.addEventListener("click", () => this.mailCloseLightbox());
    this._mailLbKey = e => { if (e.key === "Escape") { e.preventDefault(); e.stopImmediatePropagation(); this.mailCloseLightbox(); } };
    document.addEventListener("keydown", this._mailLbKey, true);
    document.body.appendChild(ov);
    this._mailLbFocus = document.activeElement;
    close.focus();
  },
  mailCloseLightbox() {
    document.querySelector(".mail-lightbox")?.remove();
    if (this._mailLbKey) document.removeEventListener("keydown", this._mailLbKey, true);
    this._mailLbKey = null;
    if (this._mailLbFocus && this._mailLbFocus.focus) { try { this._mailLbFocus.focus(); } catch { } }
    this._mailLbFocus = null;
  },
  // Focus an element by id after Blazor rendered it (popover search fields).
  focusById(id) {
    requestAnimationFrame(() => document.getElementById(id)?.focus({ preventScroll: true }));
  },
  mailFocusReply() {
    requestAnimationFrame(() => {
      const box = document.getElementById("mail-quick-reply");
      if (!box) return;
      box.scrollIntoView({ block: "nearest", behavior: "smooth" });
      // the reply box sits under the newest message; keep it in the middle of the reading pane when far away
      document.getElementById("mail-quick-reply-text")?.focus({ preventScroll: true });
    });
  },
  mailUnbindShortcuts() {
    if (this._mailLbClick) document.removeEventListener("click", this._mailLbClick);
    this._mailLbClick = null;
    this.mailCloseLightbox();
    if (this._mailKeys) document.removeEventListener("keydown", this._mailKeys);
    this._mailKeys = null;
  },
  // J/K moves the selection; keep the highlighted row visible in the list.
  mailRevealSelected() {
    const el = document.querySelector(".mlr.sel, .mail-row.sel");
    if (el) el.scrollIntoView({ block: "nearest" });
  },
  // Attachment preview: Esc / arrow keys. Capture phase so the page shortcuts never see them.
  mailPreviewKeys(dotNet) {
    if (this._mailPrevKeys) document.removeEventListener("keydown", this._mailPrevKeys, true);
    this._mailPrevKeys = null;
    if (!dotNet) return;
    const handler = e => {
      if (!document.querySelector(".mail-preview-overlay")) return;
      if (e.key !== "Escape" && e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
      e.preventDefault();
      e.stopImmediatePropagation();
      dotNet.invokeMethodAsync("OnPreviewKey", e.key).catch(() => { });
    };
    this._mailPrevKeys = handler;
    document.addEventListener("keydown", handler, true);
  },
};

// "Install iPop" (AccountSheet): Edge/Chrome offer it only over https; keep their prompt for our button.
ipop._installEvt = null;
ipop._installed = false;
window.addEventListener("beforeinstallprompt", e => { e.preventDefault(); ipop._installEvt = e; });
window.addEventListener("appinstalled", () => { ipop._installEvt = null; ipop._installed = true; });
/** "installed" | "available" | "insecure" (plain http: browsers never offer it) | "unsupported". */
ipop.installState = () => {
  if (ipop._installed || matchMedia("(display-mode: standalone)").matches || matchMedia("(display-mode: window-controls-overlay)").matches) return "installed";
  if (ipop._installEvt) return "available";
  return window.isSecureContext ? "unsupported" : "insecure";
};
ipop.install = async () => {
  const e = ipop._installEvt;
  if (!e) return ipop.installState();
  e.prompt();
  const { outcome } = await e.userChoice;
  ipop._installEvt = null;
  if (outcome === "accepted") ipop._installed = true;
  return ipop.installState();
};
// The push-only worker also makes iPop installable; register it up front instead of on first push use.
if (window.isSecureContext && "serviceWorker" in navigator) ipop._serviceWorker?.();

// A toast click in sw.js asks an open iPop window to go to the toast's page.
if ("serviceWorker" in navigator) {
  navigator.serviceWorker.addEventListener("message", e => {
    if (!e.data || e.data.type !== "ipop-open" || !e.data.url) return;
    if (window.ipopSession?.isLocked()) { ipopSession.forwardOpen(e.data.url); return; }
    try { Blazor.navigateTo(e.data.url); } catch { location.href = e.data.url; }
  });
}

document.documentElement.setAttribute("spellcheck", ipop.spellcheck() ? "true" : "false");
