// ICT / Admin: signed-in PCs, remote help, unattended switch (Admin), session log (mockup remote.html §4).
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], sel = null, t, me = { isAdmin: false, canRemote: false };
  // Phase 1b (Active Directory) fills this; until then there is no source, so the list never shows such rows.
  const notInstalled = [];
  $('[data-min]').innerHTML = ICON.min; $('[data-max]').innerHTML = ICON.max; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-close]').onclick = () => post({ type: 'close' });
  $('#q').oninput = () => { clearTimeout(t); t = setTimeout(load, 250); };
  $('#openRemote').onclick = () => post({ type: 'openRemote' });
  // 9-digit remote ID per PC (1.8.0), shown "482 913 576".
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');

  async function load() {
    const q = $('#q').value.trim();
    const r = await api('GET', 'api/notifier/devices' + (q ? '?q=' + encodeURIComponent(q.slice(0, 80)) : ''));
    if (!r.ok) { $('#devs').innerHTML = `<div class="empty">${r.status === 403 ? 'Only ICT and Administrators can see devices.' : 'Can\'t load devices.'}</div>`; return; }
    rows = r.json || [];
    const online = rows.filter(d => d.online).length;
    $('#cnt').textContent = `${rows.length} computer${rows.length === 1 ? '' : 's'} · ${online} online`;
    $('#devs').innerHTML = rows.map((d, i) => `<div class="dev ${sel === d.id ? 'on' : ''}" data-i="${i}"><span class="pc">${IC((d.osVersion || '').includes('Laptop') ? 'laptop' : 'monitor')}<i class="dot ${d.online ? '' : 'off'}"></i></span>
      <span class="m"><b>${esc(d.machineName)}</b><small>${esc(d.userName)} · ${d.online ? esc(firstIp(d)) : 'last seen ' + esc(ago(d.lastSeenAt))}${d.remoteId ? ` · <span class="mono">${fmtId(d.remoteId)}</span>` : ''}</small></span>${d.unattended ? '<span class="ua">UNATTENDED</span>' : ''}</div>`).join('')
      + notInstalled.map(d => `<div class="dev na"><span class="pc na">${IC('monitor-off')}</span><span class="m"><b>${esc(d.machineName)}</b><small>Notifier not installed${d.ip ? ' · ' + esc(d.ip) : ''}</small></span><span class="stt">NOT INSTALLED</span></div>`).join('')
      || '<div class="empty">No computers found</div>';
    const cur = rows.find(d => d.id === sel); if (cur) show(cur);
  }
  const firstIp = d => (d.localIps || '').split(',')[0] || d.remoteIp || '';
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : new Date(iso).toLocaleDateString(); };
  const dur = (a, b) => { if (!a) return '—'; const s = Math.max(0, ((b ? new Date(b) : new Date()) - new Date(a)) / 1000); return s < 60 ? Math.round(s) + ' s' : Math.round(s / 60) + ' min'; };

  $('#devs').onclick = e => { const el = e.target.closest('.dev'); if (!el) return; const d = rows[+el.dataset.i]; sel = d.id; document.querySelectorAll('.dev').forEach(x => x.classList.toggle('on', x === el)); show(d); };

  async function show(d) {
    const canRemote = me.canRemote && d.online;
    $('#detail').innerHTML = `<h3>${esc(d.machineName)}</h3><div class="sub">${d.online ? 'Online · iPop Notifier ' + esc(d.appVersion || '') : 'Offline · last seen ' + esc(ago(d.lastSeenAt))}</div>
      <div class="kv">${d.remoteId ? `<span>Remote ID</span><span class="sel"><span class="mono" style="font-weight:650">${fmtId(d.remoteId)}</span> <span class="stt" style="margin-left:6px">fixed</span></span>` : ''}<span>User</span><span>${esc(d.userName)}${d.department || d.site ? ` (${esc([d.department, d.site].filter(Boolean).join(' · '))})` : ''}</span>
      <span>Windows user</span><span>${esc(d.windowsUser || '—')}</span>
      <span>IP address</span><span class="sel">${esc(d.localIps || '—')}</span>
      <span>Seen from</span><span class="sel">${esc(d.remoteIp || '—')}</span>
      <span>Windows</span><span>${esc(d.osVersion || '—')}</span>
      <span>Last active</span><span>${esc(ago(d.lastSeenAt))}</span></div>
      <div class="acts"><button class="p" data-a="remote" ${canRemote ? '' : 'disabled'} title="${d.online ? '' : 'The PC is offline'}">${IC('screen-share')}Remote help</button>
        <button data-a="view" ${canRemote ? '' : 'disabled'}>${IC('eye')}View only</button>
        <button data-a="copy">${IC('copy')}Copy IP</button>${d.remoteId ? `<button data-a="cid">${IC('copy')}Copy ID</button>` : ''}</div>
      ${me.isAdmin && d.machineId ? uaCard(d) : ''}
      ${!me.canRemote && me.isAdmin === false ? '<div class="note">Remote help is switched off for your account. Ask an Administrator.</div>' : ''}
      <div class="lbl">Session log · ${esc(d.machineName)}</div><div id="log" class="note">Loading…</div>`;
    const q = s => $('#detail').querySelector(s);
    q('[data-a=copy]').onclick = () => { post({ type: 'copy', text: firstIp(d) }); app.snack('IP copied'); };
    const cid = q('[data-a=cid]'); if (cid) cid.onclick = () => { post({ type: 'copy', text: d.remoteId }); app.snack('ID copied'); };
    q('[data-a=remote]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'control' });
    q('[data-a=view]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'view' });
    if (q('.card2')) wireUa(d, q);
    const log = await api('GET', `api/notifier/remote/log?deviceId=${d.id}`);
    const el = q('#log'); if (!el) return;
    const items = log.ok ? log.json : [];
    el.className = '';
    el.innerHTML = items.length ? `<table class="log"><tr><th>When</th><th>ICT</th><th>Length</th><th>Mode</th><th>Result</th><th>Files</th></tr>${items.map(x => `<tr>
      <td>${esc(new Date(x.requestedAt).toLocaleString([], { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }))}</td>
      <td>${esc(x.helper)}</td><td>${x.status === 'active' ? esc(dur(x.startedAt)) + ' (live)' : esc(dur(x.startedAt, x.endedAt))}</td>
      <td><span class="pill">${x.mode === 'view' ? 'View only' : 'Control'}</span></td>
      <td>${esc({ ended: 'Done', active: 'Live', declined: 'Declined', expired: 'No answer', denied: 'Wrong password', locked: 'Locked' }[x.status] || x.status)}${x.unattended ? ' · unattended' : ''}</td>
      <td>${esc((x.files || '—').split('\n').join(', '))}</td></tr>`).join('')}</table>` : '<div class="note">No remote sessions on this PC yet.</div>';
  }

  // ── Unattended access (Admin): the PC's switch + password. The password goes to the server once and is stored
  // there as a hash only; nothing here can read it back. Saving a new one also unlocks a locked PC.
  function uaCard(d) {
    const locked = d.lockedUntil && new Date(d.lockedUntil) > new Date();
    const until = locked ? new Date(d.lockedUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }) : '';
    return `<div class="card2"><h4>${IC('shield-check')}Unattended access<span class="tag" style="margin-left:auto">ADMIN</span></h4>
      <div class="r"><span class="sw ${d.unattendedSwitch ? 'on' : ''}" data-a="ua"></span><span>Connect without asking the user<br><small>Kiosk, server. Off by default: the user gets an Allow prompt.</small></span></div>
      <label class="fld" style="margin:8px 0">${IC('lock')}<input type="password" id="uaPw" maxlength="128" autocomplete="new-password" placeholder="${d.hasPassword ? '••••••••••••' : 'New password'}"><button type="button" data-a="gen" title="Make a strong password">${IC('refresh-cw')}</button></label>
      <div class="r"><small>Min. 10 characters · stored as a hash on the server · never shown again</small></div>
      <div class="r">${IC('shield-alert')}<span>${locked ? `Locked until ${esc(until)} after wrong passwords · saving a new password unlocks it` : 'Locks for 15 min after <b>5</b> wrong passwords · every attempt is logged'}</span></div>
      <div class="b"><button class="p" data-a="save">Save password</button><button data-a="remove" ${d.hasPassword || d.unattendedSwitch ? '' : 'disabled'}>Remove access</button></div></div>`;
  }
  function wireUa(d, q) {
    const pw = q('#uaPw'), fld = pw.closest('.fld');
    pw.onfocus = () => fld.classList.add('f'); pw.onblur = () => fld.classList.remove('f');
    q('[data-a=ua]').onclick = async e => {
      const el = e.currentTarget, on = !el.classList.contains('on');
      const r = await api('PUT', `api/notifier/remote/devices/${d.id}/unattended`, { unattended: on });
      if (!r.ok) return app.snack("Couldn't change unattended access");
      el.classList.toggle('on', on); d.unattendedSwitch = on;
      app.snack(on ? (d.hasPassword ? 'Unattended access on' : 'Unattended access on — set a password to use it') : 'Unattended access off');
      load();
    };
    // A strong random password, shown once so the Administrator can copy it before saving.
    q('[data-a=gen]').onclick = () => {
      const abc = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789', b = crypto.getRandomValues(new Uint8Array(14));
      pw.value = Array.from(b, x => abc[x % abc.length]).join(''); pw.type = 'text'; pw.focus(); pw.select();
    };
    q('[data-a=save]').onclick = async () => {
      if (pw.value.length < 10) { fld.classList.add('e'); app.snack('Use at least 10 characters.'); return; }
      fld.classList.remove('e');
      const r = await api('PUT', `api/notifier/remote/machines/${d.machineId}/password`, { password: pw.value });
      pw.value = '';
      if (!r.ok) return app.snack(r.json?.message || "Couldn't save the password");
      app.snack('Password saved · running unattended sessions were ended'); load();
    };
    q('[data-a=remove]').onclick = async () => {
      const r = await api('DELETE', `api/notifier/remote/machines/${d.machineId}/password`);
      if (!r.ok) return app.snack("Couldn't remove access");
      app.snack('Unattended access removed'); load();
    };
  }

  app.ready();
  api('GET', 'api/notifier/me').then(r => { if (r.ok) me = r.json; load(); });
  setInterval(load, 30000);
})();
