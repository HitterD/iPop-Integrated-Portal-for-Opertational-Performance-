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
    if (m.type === 'theme') document.documentElement.classList.toggle('dark', !!m.dark);
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
    ready: () => post({ type: 'ready' }),
  };
  // Borderless windows: the HTML title bar moves the window (buttons inside it don't).
  document.addEventListener('mousedown', e => {
    if (e.button !== 0) return;
    const bar = e.target.closest('[data-drag]');
    if (bar && !e.target.closest('button,input,a,[data-nodrag]')) { e.preventDefault(); post({ type: 'drag' }); }
  });
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && document.body.dataset.esc !== 'off') post({ type: 'close' }); });
})();

// Icons (same strokes as the mockups)
window.ICON = {
  mail: '<svg class="i" viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="m3 7 9 6 9-6"/></svg>',
  tickets: '<svg class="i" viewBox="0 0 24 24"><path d="M3 9a3 3 0 0 0 0 6v3h18v-3a3 3 0 0 1 0-6V6H3Z"/><path d="M13 6v12"/></svg>',
  calendar: '<svg class="i" viewBox="0 0 24 24"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M16 3v4M8 3v4M3 11h18"/></svg>',
  news: '<svg class="i" viewBox="0 0 24 24"><path d="m3 11 18-5v12L3 14v-3z"/><path d="M11.6 16.8a3 3 0 1 1-5.8-1.6"/></svg>',
  projects: '<svg class="i" viewBox="0 0 24 24"><path d="M9 11l3 3 8-8"/><path d="M20 12v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h9"/></svg>',
  notes: '<svg class="i" viewBox="0 0 24 24"><path d="M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z"/><path d="M14 3v6h6M8 13h8M8 17h5"/></svg>',
  calls: '<svg class="i" viewBox="0 0 24 24"><path d="M22 16.9v3a2 2 0 0 1-2.2 2 19.8 19.8 0 0 1-8.6-3.1 19.5 19.5 0 0 1-6-6A19.8 19.8 0 0 1 2.1 4.2 2 2 0 0 1 4.1 2h3a2 2 0 0 1 2 1.7c.1.9.4 1.8.7 2.7a2 2 0 0 1-.5 2.1L8 9.8a16 16 0 0 0 6 6l1.3-1.3a2 2 0 0 1 2.1-.4c.9.3 1.8.6 2.7.7a2 2 0 0 1 1.7 2Z"/></svg>',
  chat: '<svg class="i" viewBox="0 0 24 24"><path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12Z"/></svg>',
  system: '<svg class="i" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/></svg>',
  x: '<svg class="i s" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  min: '<svg class="i s" viewBox="0 0 24 24"><path d="M5 12h14"/></svg>',
  check: '<svg class="i s" viewBox="0 0 24 24" style="stroke-width:3"><path d="m5 12 5 5 9-10"/></svg>',
  gear: '<svg class="i" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z"/></svg>',
  readAll: '<svg class="i" viewBox="0 0 24 24"><path d="m3 12 5 5L19 6M13 17l1 1 9-11"/></svg>',
};
window.MOD = {
  mail: { c: 'var(--mail)', n: 'Mail' }, tickets: { c: 'var(--tix)', n: 'iDesk' }, calendar: { c: 'var(--cal)', n: 'Calendar' },
  news: { c: 'var(--news)', n: 'News & memos' }, projects: { c: 'var(--proj)', n: 'Projects' }, notes: { c: 'var(--notes)', n: 'Notes' },
  calls: { c: 'var(--calls)', n: 'Calls' }, chat: { c: 'var(--chat)', n: 'Chat' }, system: { c: 'var(--sys)', n: 'iPop' },
};
window.initials = name => String(name || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();
window.avatarColor = name => { const p = ['#c2442e', '#7c4dd8', '#2563eb', '#1f8fa6', '#2e9e5b', '#d19a2a', '#a0457a', '#4f6b8f', '#5562b8']; let h = 0; for (const c of String(name || '')) h = (h * 31 + c.charCodeAt(0)) >>> 0; return p[h % p.length]; };
window.timeShort = iso => { const d = new Date(iso); const now = new Date(); const same = d.toDateString() === now.toDateString(); return same ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : d.toLocaleDateString([], { day: 'numeric', month: 'short' }) + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
