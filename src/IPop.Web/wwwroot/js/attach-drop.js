// Drop files from Windows / paste with Ctrl+V into an existing Blazor <InputFile>
// (micro-interactions #12/#13/#14). Fully delegated: markup opts in with attributes, no C# wiring.
//
//   <div data-attach-scope>                      paste anywhere inside goes to a zone
//     <div data-attach-zone data-attach-default   drop target; default paste target in its scope
//          data-attach-title="Drop to attach" data-attach-hint="Photos or documents">
//       <InputFile ... />                         receives the files (same validation as the picker)
//
// Files are handed to the input through DataTransfer + a "change" event, exactly like
// ipop.chatBindPaste, so Blazor's InputFile streaming and limits apply unchanged.
window.ipopAttach = (() => {
  const hasFiles = e => !!(e.dataTransfer && [...(e.dataTransfer.types || [])].includes('Files'));

  function accepts(input, file) {
    const accept = (input.getAttribute('accept') || '').trim();
    if (!accept) return true;
    const name = (file.name || '').toLowerCase(), type = (file.type || '').toLowerCase();
    return accept.split(',').map(s => s.trim().toLowerCase()).filter(Boolean).some(a =>
      a.startsWith('.') ? name.endsWith(a) : a.endsWith('/*') ? type.startsWith(a.slice(0, -1)) : type === a);
  }

  function deliver(zone, files) {
    const input = zone && zone.querySelector('input[type="file"]');
    if (!input || input.disabled) return false;
    const list = [...files].filter(Boolean).filter(f => accepts(input, f));
    if (!list.length) return false;
    try {
      const dt = new DataTransfer();
      list.forEach(f => dt.items.add(f));
      input.files = dt.files;
      input.dispatchEvent(new Event('change', { bubbles: true }));
      if (window.ipopMicro) ipopMicro.replay(zone, 'mi-attach-took');
      return true;
    } catch { return false; }
  }

  // Lazily hand each zone to ipopMicro.registerDrop so it gets the shared overlay. Runs in the
  // capture phase on document, before the zone's own (just-added) dragenter listener fires.
  document.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    const zone = e.target.closest && e.target.closest('[data-attach-zone]');
    if (zone && window.ipopMicro && !zone.classList.contains('mi-drop-zone'))
      ipopMicro.registerDrop(zone, zone.dataset.attachTitle || 'Drop to attach', zone.dataset.attachHint || '');
  }, true);

  document.addEventListener('dragover', e => {
    if (!hasFiles(e)) return;
    if (e.target.closest && e.target.closest('[data-attach-zone]')) e.preventDefault();
  });

  document.addEventListener('drop', e => {
    const zone = e.target.closest && e.target.closest('[data-attach-zone]');
    if (!zone || !e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) return;
    e.preventDefault();
    deliver(zone, e.dataTransfer.files);
  });

  // Windows Snipping Tool / PrintScreen put the image on items, not files.
  function clipboardFiles(e) {
    const items = e.clipboardData && e.clipboardData.items;
    if (items && items.length) {
      const fromItems = [...items].filter(i => i.kind === 'file').map(i => i.getAsFile()).filter(Boolean);
      if (fromItems.length) return fromItems;
    }
    return e.clipboardData && e.clipboardData.files ? [...e.clipboardData.files] : [];
  }

  document.addEventListener('paste', e => {
    const scope = e.target.closest && e.target.closest('[data-attach-scope]');
    if (!scope) return;
    // Office copies text with an image rendition attached: pasting text into a field must stay text.
    const editable = e.target.closest('input, textarea, [contenteditable="true"]');
    if (editable && [...(e.clipboardData && e.clipboardData.types || [])].includes('text/plain')) return;
    const files = clipboardFiles(e);
    if (!files.length) return;
    // The zone the caret is in wins; otherwise the scope's default zone; otherwise the first that accepts.
    const zones = [e.target.closest('[data-attach-zone]'), scope.querySelector('[data-attach-default]'), ...scope.querySelectorAll('[data-attach-zone]')]
      .filter(Boolean);
    for (const z of zones) {
      if (deliver(z, files)) { e.preventDefault(); return; }
    }
  });

  return { deliver };
})();

// Projects list motion (#37 task completed, #9 row exit). Lives here because the brief allows one
// new script; it only builds on window.ipopMicro and is a no-op under prefers-reduced-motion.
window.ipopTaskMotion = (() => {
  const reduced = () => !window.ipopMicro || ipopMicro.reduced();
  const ease = 'cubic-bezier(.16,1,.3,1)';
  let first = null;

  /** Remember where every keyed task row sits, before the list re-renders. */
  function prepare() {
    first = null;
    if (reduced()) return;
    first = new Map([...document.querySelectorAll('.plist-card [data-flip-key]')].map(e => [e.dataset.flipKey, e.getBoundingClientRect()]));
  }

  /** After the re-render: pop the check, draw the strike, then glide rows that moved (only if any did). */
  function completed(taskId) {
    const before = first; first = null;
    if (reduced()) return;
    const row = document.querySelector(`.plist-card [data-flip-key="${taskId}"]`);
    if (row) {
      ipopMicro.replay(row.querySelector('.plist-check'), 'mi-pop');
      ipopMicro.replay(row, 'mi-just-done');
    }
    if (!before) return;
    for (const e of document.querySelectorAll('.plist-card [data-flip-key]')) {
      const a = before.get(e.dataset.flipKey); if (!a) continue;
      const b = e.getBoundingClientRect(), dx = a.left - b.left, dy = a.top - b.top;
      if (Math.abs(dx) < 1 && Math.abs(dy) < 1) continue;
      // Hold the old slot while the check pops and the strike draws, then glide.
      e.animate([{ transform: `translate(${dx}px,${dy}px)` }, { transform: 'none' }],
        { duration: 340, delay: 420, easing: ease, fill: 'backwards' });
    }
  }

  /** Slide out every task row/card for these ids (list rows and kanban cards), then resolve. */
  function leave(ids) {
    if (reduced() || !ids || !ids.length) return Promise.resolve();
    const els = ids.flatMap(id => [...document.querySelectorAll(`[data-flip-key="${id}"], #task-card-${id}`)]);
    return Promise.all(els.map(el => ipopMicro.leave(el)));
  }

  return { prepare, completed, leave };
})();

// Directory favorites (#3 star pop, #36 favorite flies to the Favorites panel).
window.ipopDirMotion = (() => {
  const esc = s => (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/"/g, '\\"');
  function favorite(nik, nowFav) {
    if (!window.ipopMicro || ipopMicro.reduced()) return;
    const key = esc(nik);
    for (const star of document.querySelectorAll(`[data-fav-nik="${key}"]`)) ipopMicro.pop(star);
    if (!nowFav) return;
    // Fly only when the panel is already on screen (the first favorite creates it — nothing to fly to).
    const panel = document.querySelector('.dir-favs');
    const avatar = document.querySelector(`[data-fav-av="${key}"]`);
    if (!panel || !avatar || !panel.offsetParent) return;
    const r = panel.getBoundingClientRect();
    if (r.bottom < 0 || r.top > innerHeight) return;
    ipopMicro.fly(avatar, panel.querySelector('.card-meta') || panel, { endScale: .35 });
  }
  return { favorite };
})();

// Stats count-up (#33) for numbers that Blazor renders as text with data-mi-count. The text is
// already correct without JS; this only animates it. Elements are re-keyed by value in Razor, so a
// changed number is a fresh element. data-mi-count-key remembers the last value so a refresh counts
// from the old number instead of from zero.
window.ipopCount = (() => {
  const last = new Map();
  const fmt = n => Math.round(n).toLocaleString('en-US');
  // Write into Blazor's own text node (never replace it) so later renders still find it.
  const put = (el, v) => { const t = el.firstChild; if (t && t.nodeType === 3 && el.childNodes.length === 1) t.nodeValue = v; else el.textContent = v; };
  function scan() {
    const still = !window.ipopMicro || ipopMicro.reduced();
    for (const el of document.querySelectorAll('[data-mi-count]:not([data-mi-counted])')) {
      el.dataset.miCounted = '1';
      const to = Number(el.dataset.miCount) || 0, key = el.dataset.miCountKey;
      const from = key && last.has(key) ? last.get(key) : 0;
      if (key) last.set(key, to);
      if (still || from === to) continue;
      const t0 = performance.now(), dur = 700, final = el.textContent;
      const step = t => {
        if (!el.isConnected) return;
        const k = Math.min(1, (t - t0) / dur);
        // End on exactly the text Blazor rendered (its culture/format), not our approximation.
        put(el, k >= 1 ? final : fmt(from + (to - from) * (1 - Math.pow(1 - k, 3))));
        if (k < 1) requestAnimationFrame(step);
      };
      put(el, fmt(from));
      requestAnimationFrame(step);
    }
  }
  return { scan };
})();

/** Inline edit saved (#32): flash the element that saved in place, after Blazor's next paint. */
window.ipopSavedFlash = sel => new Promise(res => requestAnimationFrame(() => {
  if (window.ipopMicro) ipopMicro.saved(document.querySelector(sel));
  res();
}));
