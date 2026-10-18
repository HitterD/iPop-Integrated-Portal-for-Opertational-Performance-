// ICT / Admin: signed-in PCs, remote help, unattended switch (Admin), session log (mockup remote.html §4).
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], sel = null, t, me = { isAdmin: false, canRemote: false };
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
    $('#devs').innerHTML = rows.map((d, i) => `<div class="dev ${sel === d.id ? 'on' : ''}" data-i="${i}"><span class="pc">${IC((d.osVersion || '').includes('Laptop') ? 'laptop' : 'monitor')}<i class="dot ${d.online ? '' : 'off'}"></i></span>
      <span class="m"><b>${esc(d.machineName)}</b><small>${esc(d.userName)} · ${d.online ? esc(firstIp(d)) : 'last seen ' + esc(ago(d.lastSeenAt))}</small></span>${d.unattended ? '<span class="ua">UNATTENDED</span>' : ''}</div>`).join('') || '<div class="empty">No computers found</div>';
    const cur = rows.find(d => d.id === sel); if (cur) show(cur);
  }
  const firstIp = d => (d.localIps || '').split(',')[0] || d.remoteIp || '';
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : new Date(iso).toLocaleDateString(); };
  const dur = (a, b) => { if (!a) return '—'; const s = Math.max(0, ((b ? new Date(b) : new Date()) - new Date(a)) / 1000); return s < 60 ? Math.round(s) + ' s' : Math.round(s / 60) + ' min'; };

  $('#devs').onclick = e => { const el = e.target.closest('.dev'); if (!el) return; const d = rows[+el.dataset.i]; sel = d.id; document.querySelectorAll('.dev').forEach(x => x.classList.toggle('on', x === el)); show(d); };

  async function show(d) {
    const canRemote = me.canRemote && d.online;
    $('#detail').innerHTML = `<h3>${esc(d.machineName)}</h3><div class="sub">${d.online ? 'Online · iPop Notifier ' + esc(d.appVersion || '') : 'Offline · last seen ' + esc(ago(d.lastSeenAt))}</div>
      <div class="kv"><span>User</span><span>${esc(d.userName)}${d.department || d.site ? ` (${esc([d.department, d.site].filter(Boolean).join(' · '))})` : ''}</span>
      <span>Windows user</span><span>${esc(d.windowsUser || '—')}</span>
      <span>IP address</span><span class="sel">${esc(d.localIps || '—')}</span>
      <span>Seen from</span><span class="sel">${esc(d.remoteIp || '—')}</span>
      <span>Windows</span><span>${esc(d.osVersion || '—')}</span>
      <span>Last active</span><span>${esc(ago(d.lastSeenAt))}</span></div>
      <div class="acts"><button class="p" data-a="remote" ${canRemote ? '' : 'disabled'} title="${d.online ? '' : 'The PC is offline'}">${IC('screen-share')}Remote help</button>
        <button data-a="view" ${canRemote ? '' : 'disabled'}>${IC('eye')}View only</button>
        <button data-a="copy">${IC('copy')}Copy IP</button></div>
      ${me.isAdmin ? `<div class="urow">${IC('shield-check')}<span class="t"><b>Unattended access</b><small>Connect without asking the user (kiosk, server). Admin only; every change is logged.</small></span><span class="sw ${d.unattended ? 'on' : ''}" data-a="ua"></span></div>` : ''}
      ${!me.canRemote && me.isAdmin === false ? '<div class="note">Remote help is switched off for your account. Ask an Administrator.</div>' : ''}
      <div class="lbl">Session log · ${esc(d.machineName)}</div><div id="log" class="note">Loading…</div>`;
    const q = s => $('#detail').querySelector(s);
    q('[data-a=copy]').onclick = () => { post({ type: 'copy', text: firstIp(d) }); app.snack('IP copied'); };
    q('[data-a=remote]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'control' });
    q('[data-a=view]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'view' });
    const ua = q('[data-a=ua]');
    if (ua) ua.onclick = async () => {
      const on = !ua.classList.contains('on');
      const r = await api('PUT', `api/notifier/remote/devices/${d.id}/unattended`, { unattended: on });
      if (r.ok) { ua.classList.toggle('on', on); d.unattended = on; app.snack(on ? 'Unattended access on' : 'Unattended access off'); load(); }
    };
    const log = await api('GET', `api/notifier/remote/log?deviceId=${d.id}`);
    const el = q('#log'); if (!el) return;
    const items = log.ok ? log.json : [];
    el.className = '';
    el.innerHTML = items.length ? `<table class="log"><tr><th>When</th><th>ICT</th><th>Length</th><th>Mode</th><th>Result</th><th>Files</th></tr>${items.map(x => `<tr>
      <td>${esc(new Date(x.requestedAt).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }))}</td>
      <td>${esc(x.helper)}</td><td>${x.status === 'active' ? esc(dur(x.startedAt)) + ' (live)' : esc(dur(x.startedAt, x.endedAt))}</td>
      <td><span class="pill">${x.mode === 'view' ? 'View only' : 'Control'}</span></td>
      <td>${esc({ ended: 'Done', active: 'Live', declined: 'Declined', expired: 'No answer' }[x.status] || x.status)}${x.unattended ? ' · unattended' : ''}</td>
      <td>${esc((x.files || '—').split('\n').join(', '))}</td></tr>`).join('')}</table>` : '<div class="note">No remote sessions on this PC yet.</div>';
  }

  app.ready();
  api('GET', 'api/notifier/me').then(r => { if (r.ok) me = r.json; load(); });
  setInterval(load, 30000);
})();
