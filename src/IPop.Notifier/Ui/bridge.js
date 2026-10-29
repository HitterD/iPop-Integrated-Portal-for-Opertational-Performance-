// Talks to the app (C#) over WebView2 messages. The page never holds the iPop token: the app calls an
// allow-listed set of iPop routes for it (Core/ApiBridge.cs).
(function () {
  const wv = window.chrome && window.chrome.webview;
  const waits = new Map();
  let seq = 0;
  const handlers = {};
  const post = m => wv && wv.postMessage(m);

  if (wv) wv.addEventListener('message', e => {
    const m = e.data || {};
    if ((m.type === 'apiResult' || m.type === 'imageResult') && waits.has(m.id)) {
      const w = waits.get(m.id); waits.delete(m.id); w(m); return;
    }
    if (m.type === 'theme') {
      const h = document.documentElement;
      h.classList.toggle('dark', !!m.dark); h.classList.toggle('large', !!m.large);
      if (m.mode) h.dataset.theme = m.mode;
    }
    if (m.type === 'winstate') winState(m);
    if (m.type === 'toast') snack(m.text);
    (handlers[m.type] || []).forEach(h => h(m));
  });

  function call(type, extra) {
    return new Promise(res => { const id = ++seq; waits.set(id, res); post(Object.assign({ type, id }, extra)); });
  }
  async function api(method, path, body) {
    const r = await call('api', { method, path, body });
    return { ok: r.status >= 200 && r.status < 300, status: r.status, json: r.json };
  }
  const imgCache = new Map();
  function image(path) {
    if (!imgCache.has(path)) imgCache.set(path, call('image', { path }).then(r => r.url));
    return imgCache.get(path);
  }
  function snack(text) {
    document.querySelectorAll('.snack').forEach(s => s.remove());
    const s = document.createElement('div'); s.className = 'snack'; s.textContent = text; document.body.appendChild(s);
    setTimeout(() => s.remove(), 3200);
  }
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  window.app = {
    post, api, image, snack, esc,
    on: (type, h) => (handlers[type] = handlers[type] || []).push(h),
    syncMax: () => syncMax(),
    ready: () => post({ type: 'ready' }),
  };
  // Maximise (1.4.0): □ in the title bar ([data-max]) becomes Restore when maximised; html.max = maximised.
  let canMax = null;
  function winState(m) {
    if (m.canMax !== undefined) canMax = !!m.canMax;
    document.documentElement.classList.toggle('max', !!m.max);
    syncMax();
  }
  function syncMax() {
    const max = document.documentElement.classList.contains('max');
    document.querySelectorAll('[data-max]').forEach(b => {
      if (canMax === false) { b.hidden = true; return; }
      b.title = max ? 'Restore' : 'Maximize';
      b.setAttribute('aria-label', b.title);
      if (b.dataset.icon !== String(max)) { b.dataset.icon = String(max); b.innerHTML = window.IC ? IC(max ? 'copy' : 'square') : ''; }
    });
  }
  document.addEventListener('click', e => { if (e.target.closest('[data-max]')) post({ type: 'maximize' }); });

  // Borderless windows: the HTML title bar moves the window (buttons inside it don't). Once Windows runs the move
  // loop the page never sees the first click's mouseup, so a double-click on the header is detected by timing.
  let lastDown = { t: 0, x: 0, y: 0 };
  document.addEventListener('mousedown', e => {
    if (e.button !== 0) return;
    const bar = e.target.closest('[data-drag]');
    if (!bar || e.target.closest('button,input,a,[data-nodrag]')) return;
    e.preventDefault();
    const now = Date.now(), dbl = now - lastDown.t < 500 && Math.abs(e.screenX - lastDown.x) < 6 && Math.abs(e.screenY - lastDown.y) < 6;
    lastDown = dbl ? { t: 0, x: 0, y: 0 } : { t: now, x: e.screenX, y: e.screenY };
    if (dbl && document.querySelector('[data-max]:not([hidden])')) post({ type: 'maximize' });
    else post({ type: 'drag' });
  });
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && document.body.dataset.esc !== 'off') post({ type: 'close' }); });
})();

// Icons: Lucide, the same set and names as iPop web (IPop.Web/Services/NotificationModules.cs).
// IC() returns a placeholder; lucide.createIcons() turns it into the SVG (run after every render).
window.IC = (name, cls) => `<i data-lucide="${name}"${cls ? ` class="${cls}"` : ''}></i>`;
window.icons = () => { try { window.lucide && window.lucide.createIcons({ attrs: { 'stroke-width': 1.9 } }); } catch { } };
// Freshly rendered title bars get the right □ / Restore icon too.
new MutationObserver(() => { if (document.querySelector('[data-max]:not([data-icon])')) app.syncMax(); if (document.querySelector('i[data-lucide]')) window.icons(); })
  .observe(document.documentElement, { childList: true, subtree: true });
window.ICON = {
  mail: IC('mail'), tickets: IC('life-buoy'), calendar: IC('video'), news: IC('newspaper'), projects: IC('kanban'),
  notes: IC('notebook-pen'), calls: IC('phone'), chat: IC('message-circle'), system: IC('bell'),
  x: IC('x'), min: IC('minus'), max: IC('square'), check: IC('check'), gear: IC('settings'), readAll: IC('check-check'),
};
window.MOD = {
  mail: { c: 'var(--mail)', n: 'Mail' }, tickets: { c: 'var(--tix)', n: 'iDesk' }, calendar: { c: 'var(--cal)', n: 'Calendar' },
  news: { c: 'var(--news)', n: 'News & memos' }, projects: { c: 'var(--proj)', n: 'Projects' }, notes: { c: 'var(--notes)', n: 'Notes' },
  calls: { c: 'var(--calls)', n: 'Calls' }, chat: { c: 'var(--chat)', n: 'Chat' }, system: { c: 'var(--sys)', n: 'iPop' },
};
window.initials = name => String(name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();
window.avatarColor = name => { const p = ['#c2442e', '#7c4dd8', '#2563eb', '#1f8fa6', '#2e9e5b', '#d19a2a', '#a0457a', '#4f6b8f', '#5562b8']; let h = 0; for (const c of String(name || '')) h = (h * 31 + c.charCodeAt(0)) >>> 0; return p[h % p.length]; };
window.timeShort = iso => { const d = new Date(iso); const now = new Date(); const same = d.toDateString() === now.toDateString(); return same ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : d.toLocaleDateString([], { day: 'numeric', month: 'short' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
