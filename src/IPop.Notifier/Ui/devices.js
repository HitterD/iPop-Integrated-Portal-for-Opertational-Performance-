(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], sel = null, t;
  $('[data-min]').innerHTML = ICON.min; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-close]').onclick = () => post({ type: 'close' });
  $('#q').oninput = () => { clearTimeout(t); t = setTimeout(load, 250); };

  async function load() {
    const q = $('#q').value.trim();
    const r = await api('GET', 'api/notifier/devices' + (q ? '?q=' + encodeURIComponent(q.slice(0, 80)) : ''));
    if (!r.ok) { $('#devs').innerHTML = `<div class="empty">${r.status === 403 ? 'Only ICT and Administrators can see devices.' : 'Can\'t load devices.'}</div>`; return; }
    rows = r.json || [];
    const online = rows.filter(d => d.online).length;
    $('#cnt').textContent = `${rows.length} computer${rows.length === 1 ? '' : 's'} · ${online} online`;
    $('#devs').innerHTML = rows.map((d, i) => `<div class="dev ${sel === d.id ? 'on' : ''}" data-i="${i}"><span class="pc">🖥<i class="${d.online ? '' : 'off'}"></i></span>
      <span class="m"><b>${esc(d.machineName)}</b><small>${esc(d.userName)} · ${d.online ? esc(firstIp(d)) : 'last seen ' + esc(ago(d.lastSeenAt))}</small></span></div>`).join('') || '<div class="empty">No computers found</div>';
    const cur = rows.find(d => d.id === sel); if (cur) show(cur);
  }
  const firstIp = d => (d.localIps || '').split(',')[0] || d.remoteIp || '';
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : new Date(iso).toLocaleDateString(); };

  $('#devs').onclick = e => { const el = e.target.closest('.dev'); if (!el) return; const d = rows[+el.dataset.i]; sel = d.id; document.querySelectorAll('.dev').forEach(x => x.classList.toggle('on', x === el)); show(d); };

  function show(d) {
    $('#detail').innerHTML = `<h3>${esc(d.machineName)}</h3><div class="sub">${d.online ? 'Online · signed in to iPop Notifier' : 'Offline · last seen ' + esc(ago(d.lastSeenAt))}</div>
      <div class="kv"><span>User</span><span>${esc(d.userName)}${d.department || d.site ? ` (${esc([d.department, d.site].filter(Boolean).join(' · '))})` : ''}</span>
      <span>Windows user</span><span>${esc(d.windowsUser || '—')}</span>
      <span>IP address</span><span class="sel">${esc(d.localIps || '—')}</span>
      <span>Seen from</span><span class="sel">${esc(d.remoteIp || '—')}</span>
      <span>Windows</span><span>${esc(d.osVersion || '—')}</span>
      <span>App version</span><span>${esc(d.appVersion || '—')}</span>
      <span>Last active</span><span>${esc(ago(d.lastSeenAt))}</span></div>
      <div class="acts"><button class="p" disabled title="Coming in phase 3">🖥 Remote help</button><button data-a="copy">📋 Copy IP</button><button data-a="rdp">Open Remote Desktop</button></div>
      <div class="note">Remote help inside iPop Notifier comes in phase 3. Until then, “Open Remote Desktop” starts Windows Remote Desktop to this computer.</div>`;
    $('#detail').querySelector('[data-a=copy]').onclick = () => { post({ type: 'copy', text: firstIp(d) }); app.snack('IP copied'); };
    $('#detail').querySelector('[data-a=rdp]').onclick = () => post({ type: 'rdp', host: firstIp(d) });
  }

  app.ready();
  load();
  setInterval(load, 30000);
})();
