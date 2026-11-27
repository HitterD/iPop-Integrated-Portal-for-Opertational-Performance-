// Devices › Administrator cards (BIGIS #8 v3, mockup png/20, 21, 33): unattended switch + password (minimum length
// from the company policy), Remove access… (confirmation), Unlock now, "Always ask the user, even when unattended",
// what ICT may do unattended (within the PC's maximum), what ICT may do on this PC, Exclude this PC; display name and
// group. Every change is saved on the server, audited there, and shown in the session log below.
(function () {
  const { api, esc } = window.app;
  let P = { minPassword: 10, lockAttempts: 5, lockMinutes: 15, allowUnattended: true };
  const PERMS = [['control', 'mouse-pointer-2', 'Mouse and keyboard', 'Control the PC'], ['clipboard', 'clipboard-copy', 'Clipboard', 'Copy and paste between PCs'], ['files', 'file-up', 'File transfer', 'Send and receive files']];
  const has = (list, k) => list == null || list.split(',').includes(k);
  const hhmm = iso => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });

  function cards(d) {
    const locked = d.lockedUntil && new Date(d.lockedUntil) > new Date();
    const lockTxt = locked ? `Locked until ${hhmm(d.lockedUntil)} after wrong passwords · saving a new password unlocks it` : `Locks for ${P.lockMinutes} min after <b>${P.lockAttempts}</b> wrong passwords · every attempt is logged`;
    const ua = `<div class="card2"><h4>${IC('shield-check')}Unattended access<span class="tag" style="margin-left:auto">ADMIN</span></h4>
<div class="r"><span class="sw ${d.unattendedSwitch ? 'on' : ''}" data-a="ua"></span><span>Connect without asking the user<br><small>Kiosk, server. Off by default: the user gets an Allow prompt.</small></span></div>
${P.allowUnattended ? '' : `<div class="r" style="color:var(--cal)">${IC('triangle-alert')}<span>Unattended access is switched off for the whole company (Settings › Remote). This switch has no effect.</span></div>`}
<label class="fld" style="margin:8px 0">${IC('lock')}<input type="password" id="uaPw" maxlength="128" autocomplete="new-password" placeholder="${d.hasPassword ? '••••••••••••' : 'New password'}"><button type="button" data-a="gen" title="Generate">${IC('refresh-cw')}</button></label>
<div class="r"><small>Min. ${P.minPassword} characters · stored as a hash on the server · never shown again</small></div>
<div class="r">${IC('shield-alert')}<span>${lockTxt}</span></div>
<div class="b"><button class="p" data-a="save">Save password</button><button data-a="remove" ${d.hasPassword || d.unattendedSwitch ? '' : 'disabled'}>Remove access…</button>${locked ? '<button data-a="unlock">Unlock now</button>' : ''}</div>
<div class="r" style="border-top:1px solid var(--sep);padding-top:12px;margin-top:10px"><span class="sw ${d.alwaysAsk ? 'on' : ''}" data-a="ask"></span><span>Always ask the user, even when unattended<br><small>The password is still needed, but the user gets an Allow prompt each time.</small></span></div>
<div class="r" style="align-items:flex-start"><span style="min-width:170px">What ICT may do unattended<br><small>Can be narrower than the PC's maximum.</small></span><span style="display:flex;flex-direction:column;gap:7px">${PERMS.map(([k, ic, t]) => { const max = has(d.allowedPermissions, k), on = max && has(d.unattendedPermissions, k); return `<span class="upr" data-k="${k}" style="display:flex;align-items:center;gap:9px;cursor:pointer"><span class="cb${on ? '' : ' off'}">${on ? IC('check') : ''}</span>${IC(ic)}${t}${max ? '' : ' <small style="color:var(--ink-3)">· off for this PC</small>'}</span>`; }).join('')}</span></div></div>`;
    const perm = `<div class="card2"><h4>${IC('sliders-horizontal')}What ICT may do on this PC<span class="tag" style="margin-left:auto">ADMIN</span></h4>
${PERMS.map(([k, ic, t, s]) => { const on = has(d.allowedPermissions, k); return `<div class="r pr" data-k="${k}" style="cursor:pointer"><span class="cb${on ? '' : ' off'}">${on ? IC('check') : ''}</span>${IC(ic)}<span>${t}<br><small>${s}</small></span></div>`; }).join('')}
<div class="r"><small>These are the most the user can allow in the prompt. Unticked ones are greyed out for ICT.</small></div>
<div class="r" style="border-top:1px solid var(--sep);padding-top:12px;margin-top:10px"><span class="sw ${d.excluded ? 'on' : ''}" data-a="ex"></span><span>Exclude this PC from remote help<br><small>For example executive PCs or PCs with sensitive data. Nobody can connect, not even Admins.</small></span></div></div>`;
    return ua + perm;
  }

  const toggle = (list, k) => { const s = new Set((list ?? 'control,clipboard,files').split(',').filter(Boolean)); s.has(k) ? s.delete(k) : s.add(k); return [...s].join(','); };

  function wire(d, q, reload) {
    const settings = async (body, ok) => {
      const r = await api('PUT', `api/notifier/remote/machines/${d.machineId}/settings`, body);
      if (!r.ok) return app.snack('Couldn\'t save');
      Object.assign(d, { alias: r.json.alias, group: r.json.group, alwaysAsk: r.json.alwaysAsk, unattendedPermissions: r.json.unattendedPermissions, allowedPermissions: r.json.allowedPermissions, excluded: r.json.excluded });
      if (ok) app.snack(ok);
      reload();
    };
    const pw = q('#uaPw'), fld = pw.closest('.fld');
    pw.onfocus = () => fld.classList.add('f'); pw.onblur = () => fld.classList.remove('f');
    q('[data-a=ua]').onclick = async () => {
      const on = !d.unattendedSwitch;
      const r = await api('PUT', `api/notifier/remote/devices/${d.id}/unattended`, { unattended: on });
      if (!r.ok) return app.snack('Couldn\'t change unattended access');
      d.unattendedSwitch = on; app.snack(on ? (d.hasPassword ? 'Unattended access on' : 'Unattended access on — set a password to use it') : 'Unattended access off'); reload();
    };
    q('[data-a=gen]').onclick = () => {
      const abc = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789', b = crypto.getRandomValues(new Uint8Array(14));
      pw.value = Array.from(b, x => abc[x % abc.length]).join(''); pw.type = 'text'; pw.focus(); pw.select();
    };
    q('[data-a=save]').onclick = async () => {
      if (pw.value.length < P.minPassword) { fld.classList.add('e'); app.snack(`Use at least ${P.minPassword} characters.`); return; }
      fld.classList.remove('e');
      const r = await api('PUT', `api/notifier/remote/machines/${d.machineId}/password`, { password: pw.value });
      pw.value = '';
      if (!r.ok) return app.snack(r.json?.message || 'Couldn\'t save the password');
      app.snack('Password saved · running unattended sessions were ended'); reload();
    };
    q('[data-a=remove]').onclick = () => {
      document.querySelectorAll('.dlg').forEach(x => x.remove());
      const dg = document.createElement('div'); dg.className = 'dlg';
      dg.innerHTML = `<div class="dlgbox"><div class="ic" style="background:var(--danger)">${IC('trash-2')}</div><h3>Remove unattended access?</h3><p><b>${esc(d.alias || d.machineName)}</b> will go back to asking the user for permission every time. The password is deleted and running unattended sessions end. This is written to the session log.</p><div class="acts"><button id="dx">Keep access</button><button class="p" style="background:var(--danger)" id="dok">Remove access</button></div></div>`;
      document.body.appendChild(dg);
      dg.querySelector('#dx').onclick = () => dg.remove();
      dg.querySelector('#dok').onclick = async () => {
        dg.remove();
        const r = await api('DELETE', `api/notifier/remote/machines/${d.machineId}/password`);
        if (!r.ok) return app.snack('Couldn\'t remove access');
        app.snack('Unattended access removed'); reload();
      };
    };
    const un = q('[data-a=unlock]');
    if (un) un.onclick = async () => { const r = await api('POST', `api/notifier/remote/machines/${d.machineId}/unlock`); if (r.ok) { app.snack((d.alias || d.machineName) + ' unlocked'); reload(); } };
    q('[data-a=ask]').onclick = () => settings({ alwaysAsk: !d.alwaysAsk });
    document.querySelectorAll('#detail .upr').forEach(r => r.onclick = () => { if (!has(d.allowedPermissions, r.dataset.k)) return; settings({ unattendedPermissions: toggle(d.unattendedPermissions, r.dataset.k) }); });
    document.querySelectorAll('#detail .pr').forEach(r => r.onclick = () => settings({ allowedPermissions: toggle(d.allowedPermissions, r.dataset.k) }));
    q('[data-a=ex]').onclick = () => settings({ excluded: !d.excluded }, !d.excluded ? (d.alias || d.machineName) + ' is now excluded from remote help' : (d.alias || d.machineName) + ' can be reached again');
    q('[data-a=alias]').onclick = () => settings({ alias: q('#alias').value.trim() }, 'Saved');
    q('#alias').onkeydown = e => { if (e.key === 'Enter') q('[data-a=alias]').click(); };
    q('#grp').onchange = () => {
      let g = q('#grp').value;
      if (g !== '__new') return settings({ group: g }, 'Group saved');
      q('#grp').value = d.group || '';
      const dg = document.createElement('div'); dg.className = 'dlg';
      dg.innerHTML = `<div class="dlgbox"><div class="ic">${IC('folder-plus')}</div><h3>New group</h3><p>Shown as a tab in Remote. ${esc(d.alias || d.machineName)} moves into it.</p><label class="fld f"><input id="ng" maxlength="60" placeholder="Group name"></label><div class="acts"><button id="dx">Cancel</button><button class="p" id="dok">Create</button></div></div>`;
      document.body.appendChild(dg); dg.querySelector('#ng').focus();
      const go = () => { const v = dg.querySelector('#ng').value.trim(); if (!v) return; dg.remove(); settings({ group: v }, 'Group saved'); };
      dg.querySelector('#dx').onclick = () => dg.remove(); dg.querySelector('#dok').onclick = go;
      dg.querySelector('#ng').onkeydown = e => { if (e.key === 'Enter') go(); };
    };
  }

  /// Session log for one PC: sessions and the Admin changes, newest first (history API filtered to this PC).
  async function log(d, el) {
    const r = await api('GET', 'api/notifier/remote/history');
    if (!el.isConnected) return;
    const name = d.alias || d.machineName;
    const items = (r.ok ? r.json : []).filter(x => x.pc === name || x.pc === d.machineName).slice(0, 12);
    const RES = { done: 'Done', declined: 'Declined', expired: 'No answer', denied: 'Wrong password', locked: 'Locked', dropped: 'Connection lost' };
    const when = iso => new Date(iso).toLocaleString('en-GB', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    const dur = s => s == null ? '—' : s < 60 ? Math.round(s) + ' s' : Math.round(s / 60) + ' min';
    el.className = '';
    el.innerHTML = items.length ? `<table class="log"><tr><th>When</th><th>ICT</th><th>Length</th><th>Mode</th><th>Result</th><th>Files</th></tr>${items.map(x => x.result === 'cfg'
      ? `<tr><td>${when(x.at)}</td><td>${esc(x.who)}</td><td colspan="4" style="color:var(--ink-3)">${IC('settings', 'lucide')} ${esc(x.what)}</td></tr>`
      : `<tr><td>${when(x.at)}</td><td>${esc(x.who)}</td><td>${dur(x.seconds)}</td><td><span class="pill">${x.mode === 'view' ? 'View only' : 'Control'}</span></td><td>${RES[x.result] || esc(x.result)}${x.ua ? ' · unattended' : ''}</td><td>${esc((x.files || '').split('\n').filter(Boolean).join(', ') || '—')}</td></tr>`).join('')}</table>` : '<div class="note">No remote sessions on this PC yet.</div>';
  }

  window.devicesAdmin = { cards, wire, log, setPolicy: p => { P = Object.assign(P, p); } };
})();
