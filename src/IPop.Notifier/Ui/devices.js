// ICT / Admin: signed-in PCs (BIGIS #8 v3, mockup ipop-remote-v3 png/20, 21, 33): remote ID, monitors, display name
// and group (Admin), Remote help / View only, Admin cards (devices-admin.js), session log with Admin changes.
// While an Admin types in a field the detail is not redrawn (the 30 s reload only refreshes the list on the left).
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], sel = null, t, me = { isAdmin: false, canRemote: false }, groups = [];
  $('[data-min]').innerHTML = ICON.min; $('[data-max]').innerHTML = ICON.max; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-close]').onclick = () => post({ type: 'close' });
  $('#q').oninput = () => { clearTimeout(t); t = setTimeout(load, 250); };
  $('#openRemote').onclick = () => post({ type: 'openRemote' });
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');
  const firstIp = d => (d.localIps || '').split(',')[0] || d.remoteIp || '';
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' d ago'; };
  const label = d => d.alias || d.machineName;
  const locked = d => d.lockedUntil && new Date(d.lockedUntil) > new Date();
  const typing = () => document.activeElement && $('#detail').contains(document.activeElement) && document.activeElement.matches('input, select');

  const badge = d => d.conflict ? '<span class="ua" title="Conflicting reports for this PC">CHECK</span>'
    : d.excluded ? '<span class="ua" style="color:var(--danger);background:var(--danger-soft)">EXCLUDED</span>'
    : locked(d) ? '<span class="ua" style="color:var(--danger);background:var(--danger-soft)">LOCKED</span>'
    : d.unattended ? '<span class="ua">UNATTENDED</span>' : '';
  const row = (d, i) => `<div class="dev${sel === d.id ? ' on' : ''}${d.excluded ? ' na' : ''}" data-i="${i}"><span class="pc${d.excluded ? ' na' : ''}">${IC((d.osVersion || '').includes('Laptop') ? 'laptop' : 'monitor')}<i class="dot ${d.online ? '' : 'off'}"></i></span>
      <span class="m"><b>${esc(label(d))}</b><small>${esc(d.userName)} · ${d.online ? esc(firstIp(d)) : 'last seen ' + esc(ago(d.lastSeenAt))}${d.remoteId ? ` · <span class="mono">${fmtId(d.remoteId)}</span>` : ''}</small></span>${badge(d)}</div>`;

  async function load() {
    const q = $('#q').value.trim();
    const r = await api('GET', 'api/notifier/devices' + (q ? '?q=' + encodeURIComponent(q.slice(0, 80)) : ''));
    if (!r.ok) { $('#devs').innerHTML = `<div class="empty">${r.status === 403 ? 'Only ICT and Administrators can see devices.' : 'Can\'t load devices.'}</div>`; return; }
    rows = r.json || [];
    groups = [...new Set(rows.map(d => d.group).filter(Boolean))].sort();
    const online = rows.filter(d => d.online).length;
    $('#cnt').textContent = `${rows.length} computer${rows.length === 1 ? '' : 's'} · ${online} online`;
    $('#devs').innerHTML = rows.map(row).join('') || '<div class="empty">No computers found</div>';
    const cur = rows.find(d => d.id === sel);
    if (cur && !typing() && !document.querySelector('.dlg')) show(cur);
  }

  $('#devs').onclick = e => { const el = e.target.closest('.dev'); if (!el) return; const d = rows[+el.dataset.i]; sel = d.id; document.querySelectorAll('.dev').forEach(x => x.classList.toggle('on', x === el)); show(d); };

  async function show(d) {
    const canRemote = me.canRemote && d.online && !d.excluded;
    const deptLine = d.department ? ` (${esc(d.department)})` : '';
    const adminKv = me.isAdmin && d.machineId
      ? `<span>Display name</span><span><label class="fld" style="height:34px;max-width:300px;font-size:14px"><input id="alias" maxlength="40" placeholder="${esc(d.machineName)}" value="${esc(d.alias || '')}"><button type="button" data-a="alias" title="Save">${IC('check')}</button></label></span>
         <span>Group</span><span><select id="grp" style="height:32px;border-radius:8px;border:.5px solid var(--sep);background:var(--card);color:var(--ink);padding:0 8px;font-size:13.5px">${[...new Set([d.group, ...groups].filter(Boolean))].map(g => `<option${g === d.group ? ' selected' : ''}>${esc(g)}</option>`).join('')}<option value="__new">New group…</option></select> <small style="color:var(--ink-3)">Shown as a tab in Remote</small></span>`
      : `<span>Group</span><span>${esc(d.group || '—')}</span>`;
    $('#detail').innerHTML = `<h3>${esc(label(d))}</h3><div class="sub">${d.alias ? esc(d.machineName) + ' · ' : ''}${d.online ? 'Online · iPop Notifier ' + esc(d.appVersion || '') : 'Offline · last seen ' + esc(ago(d.lastSeenAt))}</div>
      ${d.conflict ? `<div class="urow" style="margin:0 0 14px;color:var(--cal)">${IC('triangle-alert')}<span class="t"><b>Conflicting reports for this PC</b><small>Another Notifier reports the same computer name with a different Windows ID, or this PC is seen from two addresses. Connect by name is blocked until it is checked.</small></span></div>` : ''}
      <div class="kv">${d.remoteId ? `<span>Remote ID</span><span class="sel"><span class="mono" style="font-weight:650">${fmtId(d.remoteId)}</span> <span class="stt" style="margin-left:6px">fixed</span></span>` : ''}<span>User</span><span>${esc(d.userName)}${deptLine}</span>
      <span>IP address</span><span class="sel">${esc(firstIp(d) || '—')}</span>
      <span>Windows</span><span>${esc(d.osVersion || '—')}</span>
      <span>Monitors</span><span>${d.monitors ? esc(d.monitors.split(' + ').length + ' · ' + d.monitors) : '—'}</span>${adminKv}</div>
      <div class="acts"><button class="p" data-a="remote" ${canRemote ? '' : 'disabled'} title="${d.online ? '' : 'The PC is offline'}">${IC('screen-share')}Remote help</button>
        <button data-a="view" ${canRemote ? '' : 'disabled'}>${IC('eye')}View only</button>
        <button data-a="copy">${IC('copy')}Copy IP</button>${d.remoteId ? `<button data-a="cid">${IC('copy')}Copy ID</button>` : ''}</div>
      ${me.isAdmin && d.machineId ? devicesAdmin.cards(d) : `<div class="note">${IC('lock', 'lucide')} Unattended access, allowed actions and exclusions are managed by Administrators.</div>`}
      ${!me.canRemote && me.isAdmin === false ? '<div class="note">Remote help is switched off for your account. Ask an Administrator.</div>' : ''}
      <div class="lbl">Session log · ${esc(d.machineName)}</div><div id="log" class="note">Loading…</div>`;
    const q = s => $('#detail').querySelector(s);
    q('[data-a=copy]').onclick = () => { post({ type: 'copy', text: firstIp(d) }); app.snack('IP copied'); };
    const cid = q('[data-a=cid]'); if (cid) cid.onclick = () => { post({ type: 'copy', text: d.remoteId }); app.snack('ID copied'); };
    q('[data-a=remote]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'control' });
    q('[data-a=view]').onclick = () => post({ type: 'remote', deviceId: d.id, mode: 'view' });
    if (me.isAdmin && d.machineId) devicesAdmin.wire(d, q, load);
    devicesAdmin.log(d, q('#log'));
  }

  app.ready();
  api('GET', 'api/notifier/me').then(r => { if (r.ok) me = r.json; load(); });
  api('GET', 'api/notifier/remote/policy').then(r => { if (r.ok) devicesAdmin.setPolicy(r.json.policy); });
  setInterval(load, 30000);
})();
