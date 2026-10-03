// Link clicks in chat messages, link-preview cards and mail bodies. Classic script: window.ipopLinks.
//
// One capture-phase listener on the document, scoped to: a.msg-link (chat text), a.lp-card
// (preview cards) and any anchor inside .mail-body-links (mail bodies). Everything else in the app
// keeps Blazor's own link handling.
//   internal (same origin or ipop.santos.co.id, not /api/…) → in-app navigation, no new tab
//   mailto / data-mailto → iPop's own mail composer (/mail?compose=1&toEmail=…)
//   data-unc (\\host\share\…) → Files at that folder when the host is a known NAS, else copy + toast
//   http(s) → new tab with noopener; in mail bodies, a link whose text names another domain asks first
// LinkPreviewCard.razor uses observe()/settle() to load previews only for cards on screen.
(function (root) {
  "use strict";

  const INTERNAL_HOSTS = ["ipop.santos.co.id"];
  const FILE_EXT = /\.(pdf|docx?|xlsx?|pptx?|csv|txt|rtf|odt|zip|rar|7z|png|jpe?g|gif|bmp|webp|svg|heic|mp4|mov|avi|mp3|wav|msg|eml|exe|msi|dwg)$/i;
  const NON_APP_PATHS = /^\/(api|hubs|_framework|_content|css|js|lib|img|images|fonts|sounds)(\/|$)/i;

  const api = {
    _dotNet: null,
    _nas: {},            // lower-case host → Files server id ("192.168.2.16" → "spj")
    _bound: false,

    /// Pure: what a click on `href` should do. origin = location.origin.
    classify(href, origin) {
      if (!href) return null;
      const raw = String(href).trim();
      if (/^mailto:/i.test(raw)) {
        const addr = decodeURIComponent(raw.slice(7).split("?")[0] || "").trim();
        return addr ? { kind: "mailto", addr } : null;
      }
      let u;
      try { u = new URL(raw, origin); } catch { return null; }
      if (u.protocol !== "http:" && u.protocol !== "https:") return null;
      const host = u.hostname.toLowerCase();
      let originHost = "";
      try { originHost = new URL(origin).host.toLowerCase(); } catch { }
      const internal = (u.host.toLowerCase() === originHost || INTERNAL_HOSTS.includes(host))
        && !NON_APP_PATHS.test(u.pathname);
      if (internal) return { kind: "internal", path: (u.pathname || "/") + u.search + u.hash };
      return { kind: "external", url: u.href };
    },

    /// Pure: Files route for a UNC path on a known NAS, else null.
    /// \\192.168.2.16\sja\Folder A\file.xlsx → /files?server=spj&path=%2Fsja%2FFolder%20A
    uncTarget(unc, nas) {
      const m = /^\\\\([^\\]+)\\(.+)$/.exec(String(unc || "").trim());
      if (!m) return null;
      const server = (nas || {})[m[1].toLowerCase()];
      if (!server) return null;
      let parts = m[2].split("\\").filter(p => p.length > 0);
      if (parts.length === 0) return null;
      // A trailing "name.ext" is a file: open the folder that holds it.
      if (parts.length > 1 && /\.[A-Za-z0-9]{1,5}$/.test(parts[parts.length - 1])) parts = parts.slice(0, -1);
      const path = "/" + parts.join("/");
      return "/files?server=" + encodeURIComponent(server) + "&path=" + encodeURIComponent(path);
    },

    _hostOf(s) {
      if (!s) return null;
      let t = String(s).trim().replace(/^[<(\["']+|[>)\]"'.,;:!?]+$/g, "");
      if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(t)) {
        // Bare "www.x.com/…" or "paypal.com": only domain-shaped text counts.
        if (!/^(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(?=$|[\/:?#])/i.test(t)) return null;
        // "report.pdf" / "photo.jpg" are file names, not a claim about where the link goes.
        if (!/^www\./i.test(t) && FILE_EXT.test(t.split(/[\/?#]/)[0])) return null;
        t = "http://" + t;
      }
      try {
        const u = new URL(t);
        return (u.protocol === "http:" || u.protocol === "https:") ? u.hostname.toLowerCase() : null;
      } catch { return null; }
    },

    /// Pure: true when the visible text looks like a URL/domain whose host differs from the
    /// real destination's (www. ignored) — the classic phishing link.
    mismatch(text, href) {
      if (!text || /\s/.test(String(text).trim())) return false;
      const shown = this._hostOf(text);
      const real = this._hostOf(href);
      if (!shown || !real) return false;
      const bare = h => h.replace(/^www\./, "");
      return bare(shown) !== bare(real);
    },

    attach(dotNet, nas) {
      this._dotNet = dotNet;
      this._nas = {};
      for (const [host, server] of Object.entries(nas || {})) this._nas[host.toLowerCase()] = server;
      if (this._bound || !root.document) return;
      this._bound = true;
      root.document.addEventListener("click", e => this._onClick(e), true);
      root.document.addEventListener("mouseover", e => this._onHover(e), true);
      root.document.addEventListener("focusin", e => this._onHover(e), true);
    },

    detach() { this._dotNet = null; },

    _anchor(e) {
      const a = e.target && e.target.closest && e.target.closest("a");
      if (!a) return null;
      if (a.classList.contains("msg-link") || a.classList.contains("lp-card")) return { a, mail: !!a.closest(".mail-body-links") };
      if (a.closest(".mail-body-links")) return { a, mail: true };
      return null;
    },

    // Mail bodies: the real destination on hover/focus (chat links carry it already).
    _onHover(e) {
      const hit = this._anchor(e);
      // Chat anchors carry the real URL already; a mail sender's own title could lie, so mail
      // anchors always show the real destination.
      if (!hit || (hit.a.title && !hit.mail)) return;
      const unc = hit.a.getAttribute("data-unc");
      const href = hit.a.getAttribute("href");
      if (unc) hit.a.title = unc;
      else if (href && !href.startsWith("#")) hit.a.title = href.replace(/^mailto:/i, "");
    },

    _onClick(e) {
      if (e.defaultPrevented || e.button !== 0) return;
      const hit = this._anchor(e);
      if (!hit) return;
      const a = hit.a;
      // Ctrl/Cmd/Shift-click on a web link: let the browser do its new-tab/window thing.
      const modified = e.ctrlKey || e.metaKey || e.shiftKey || e.altKey;

      const unc = a.getAttribute("data-unc");
      if (unc) { this._stop(e); this._openUnc(unc); return; }

      const mailto = a.getAttribute("data-mailto");
      const c = mailto ? { kind: "mailto", addr: mailto } : this.classify(a.getAttribute("href"), root.location.origin);
      if (!c) {
        // javascript:, data:, file:, "#"… never followed from message content.
        const href = (a.getAttribute("href") || "").trim();
        if (!href.startsWith("#") || hit.mail) this._stop(e);
        return;
      }
      if (c.kind === "mailto") { this._stop(e); this._go("/mail?compose=1&toEmail=" + encodeURIComponent(c.addr)); return; }
      if (modified) return;
      if (c.kind === "internal") { this._stop(e); this._go(c.path); return; }

      this._stop(e);
      const text = (a.textContent || "").trim();
      if (hit.mail && this.mismatch(text, c.url) && this._dotNet) {
        this._dotNet.invokeMethodAsync("ConfirmMismatch", text, c.url)
          .then(ok => { if (ok) this._open(c.url); })
          .catch(() => { });
        return;
      }
      this._open(c.url);
    },

    _stop(e) { e.preventDefault(); e.stopPropagation(); },

    _open(url) {
      const w = root.open(url, "_blank", "noopener,noreferrer");
      if (w) { try { w.opener = null; } catch { } }
    },

    _go(path) {
      if (this._dotNet) { this._dotNet.invokeMethodAsync("Navigate", path).catch(() => { root.location.href = path; }); return; }
      try { root.Blazor.navigateTo(path); } catch { root.location.href = path; }
    },

    _openUnc(unc) {
      const target = this.uncTarget(unc, this._nas);
      if (target) { this._go(target); return; }
      this.copy(unc).then(ok => {
        const msg = ok ? "Path copied — paste it in File Explorer (Win+E)" : "Couldn't copy the path: " + unc;
        if (this._dotNet) this._dotNet.invokeMethodAsync("Toast", msg, ok).catch(() => { });
      });
    },

    /// Clipboard write that also works on the plain-http LAN deploy (no navigator.clipboard).
    async copy(text) {
      try {
        if (root.isSecureContext && root.navigator && root.navigator.clipboard) {
          await root.navigator.clipboard.writeText(text);
          return true;
        }
      } catch { }
      try {
        const d = root.document;
        const ta = d.createElement("textarea");
        ta.value = text;
        ta.setAttribute("readonly", "");
        ta.style.position = "fixed"; ta.style.opacity = "0"; ta.style.top = "0"; ta.style.left = "0";
        const prev = d.activeElement;
        d.body.appendChild(ta);
        ta.select();
        const ok = d.execCommand("copy");
        ta.remove();
        if (prev && prev.focus) prev.focus();
        return !!ok;
      } catch { return false; }
    },

    // ── Lazy preview cards ──
    _io: null,
    _watch: new Map(),   // element → DotNetObjectReference

    /// Calls dotNet.OnVisible() once, the first time `el` comes near the viewport.
    observe(el, dotNet) {
      if (!el || !dotNet) return;
      if (!("IntersectionObserver" in root)) { dotNet.invokeMethodAsync("OnVisible").catch(() => { }); return; }
      if (!this._io) {
        this._io = new root.IntersectionObserver(entries => {
          for (const en of entries) {
            if (!en.isIntersecting) continue;
            const ref = this._watch.get(en.target);
            this._io.unobserve(en.target);
            this._watch.delete(en.target);
            // Remember whether the list was pinned to the bottom, so settle() can keep it there.
            const sc = en.target.closest(".messages");
            en.target._lpStick = !!sc && sc.scrollHeight - sc.scrollTop - sc.clientHeight < 150;
            if (ref) ref.invokeMethodAsync("OnVisible").catch(() => { });
          }
        }, { rootMargin: "200px 0px" });
      }
      this._watch.set(el, dotNet);
      this._io.observe(el);
    },

    unobserve(el) {
      if (!el) return;
      this._watch.delete(el);
      if (this._io) this._io.unobserve(el);
    },

    /// A card just got its content: if the list was at the bottom, keep it there.
    settle(el) {
      if (!el || !el._lpStick) return;
      el._lpStick = false;
      const sc = el.closest(".messages");
      if (sc) sc.scrollTop = sc.scrollHeight;
    },
  };

  root.ipopLinks = api;
})(typeof window !== "undefined" ? window : globalThis);
