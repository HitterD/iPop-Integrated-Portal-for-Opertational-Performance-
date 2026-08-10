// IPop UI helpers — called from Blazor components only.
// Lucide must be re-run after every Blazor render that adds <i data-lucide>.
window.ipop = {
  // Theme: "light" | "dark". Applied as data-theme on <html>, persisted in
  // localStorage so the choice survives reloads. Default: light.
  theme() {
    return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
  },
  setTheme(t) {
    document.documentElement.setAttribute("data-theme", t);
    try { localStorage.setItem("ipop-theme", t); } catch {}
  },
  toggleTheme() {
    const next = window.ipop.theme() === "dark" ? "light" : "dark";
    window.ipop.setTheme(next);
    window.ipop.syncThemeIcons(next);
    return next;
  },
  syncThemeIcons(theme) {
    // Replace whatever lucide rendered with a fresh <i> for the new icon,
    // then let lucide render it. Avoids class accumulation on old <svg>.
    const icon = theme === "dark" ? "sun" : "moon";
    document.querySelectorAll(".theme-btn").forEach(btn => {
      const i = btn.querySelector("i[data-lucide], svg.lucide");
      if (i) {
        const fresh = document.createElement("i");
        fresh.setAttribute("data-lucide", icon);
        i.replaceWith(fresh);
      }
    });
    if (window.lucide) lucide.createIcons();
  },
  restoreTheme() {
    let t = "light";
    try { t = localStorage.getItem("ipop-theme") || "light"; } catch {}
    window.ipop.setTheme(t);
    return t;
  },
  icons() {
    if (window.lucide) lucide.createIcons();
  },
  focus(el) {
    if (el) { el.focus(); el.select(); }
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
  // Global shortcuts live in JS because they fire on document, not on a component.
  // MainLayout registers once; the returned handles let Blazor detach on dispose.
  shortcuts(dotnet) {
    const onKey = e => {
      if (e.key === "Escape") dotnet.invokeMethodAsync("OnEscape");
      if (e.key === "[" && !/INPUT|TEXTAREA/.test(e.target.tagName)) {
        dotnet.invokeMethodAsync("OnToggleSidebar");
      }
    };
    const onClick = e => {
      if (!e.target.closest(".notif-pop") && !e.target.closest(".bell-btn")) {
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
    if (!window.ipop._selectPop) {
      pop._selectHost = wrap || pop.parentElement;
      document.body.appendChild(pop);
      window.ipop._selectPop = pop;
    }
    const place = () => {
      const tr = trigger.getBoundingClientRect();
      pop.style.position = "fixed";
      pop.style.left = tr.left + "px";
      pop.style.top = (tr.bottom + 6) + "px";
      pop.style.minWidth = tr.width + "px";
      pop.style.maxWidth = Math.min(320, window.innerWidth - 16) + "px";
      pop.style.zIndex = "100";
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
    document.body.appendChild(pop);
    const place = () => {
      const tr = trigger.getBoundingClientRect();
      pop.style.position = "fixed";
      pop.style.left = tr.left + "px";
      pop.style.top = (tr.bottom + 6) + "px";
      pop.style.minWidth = tr.width + "px";
      pop.style.maxWidth = Math.min(320, window.innerWidth - 16) + "px";
      pop.style.zIndex = "100";
      const pw = pop.offsetWidth;
      if (tr.left + pw > window.innerWidth - 8) {
        pop.style.left = Math.max(8, window.innerWidth - pw - 8) + "px";
      }
    };
    place();
    requestAnimationFrame(() => place());
    window.ipop._ppPlace = () => place();
    window.addEventListener("scroll", window.ipop._ppPlace, true);
    window.addEventListener("resize", window.ipop._ppPlace);
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
      if (p._ppHost && p._ppHost !== p.parentElement) p._ppHost.appendChild(p);
    });
    return true;
  }
};
