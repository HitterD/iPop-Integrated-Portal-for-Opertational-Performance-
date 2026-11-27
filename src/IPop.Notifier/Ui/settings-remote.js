// Settings › Remote (BIGIS #8 v3, mockup png/22, 23): company policy (Administrators; saved on and enforced by the
// server), "My viewer" (every ICT person) and "On the user's PC" (everyone) — the last two are the person's own
// ui-prefs "remote". The section shows only what this person may change; the nav item is hidden for nobody: users see
// their own default ticks. Defaults = the approved v3 README.
(function () {
  const { api, post } = window.app;
  const $ = s => document.querySelector(s);
  const host = $('#s-remote');
  let P = null, people = [], isAdmin = false, isIct = false, U = null;
  const DEF = { control: true, clipboard: true, files: false };

  const on = v => (v ? ' on' : '');
  const pill = (id, opts, val) => `<div class="pill3" id="${id}">${opts.map(([v, t]) => `<button data-v="${v}" class="${String(val) === String(v) ? 'on' : ''}">${t}</button>`).join('')}</div>`;
  const sel = (id, opts, val) => `<select id="${id}">${opts.map(([v, t]) => `<option value="${v}"${String(val) === String(v) ? ' selected' : ''}>${t}</option>`).join('')}</select>`;
  const row = (t, s, ctl) => `<div class="row"><span class="t">${t}<small>${s}</small></span>${ctl}</div>`;
  const sw = (id, v) => `<span class="sw${on(v)}" id="${id}"></span>`;
  const perm = (id, ic, t, v) => `<div class="row"><span class="t">${IC(ic, 'lucide')} ${t}</span>${sw(id, v)}</div>`;
  const ini = n => String(n || '?').split(/\s+/).filter(Boolean).slice(0, 2).map(w => w[0]).join('').toUpperCase();

  async function load() {
    const [pr, ur, me] = await Promise.all([api('GET', 'api/notifier/remote/policy'), api('GET', 'api/me/ui-prefs/remote'), api('GET', 'api/notifier/me')]);
    isIct = pr.ok; isAdmin = pr.ok && pr.json.isAdmin;
    P = pr.ok ? pr.json.policy : null; people = pr.ok ? (pr.json.people || []) : [];
    U = ur.ok && ur.json ? ur.json : { quality: 'auto', fit: 'fit', autoHide: false, chatSound: true, saveDir: null, keys: { cad: 'Ctrl+Alt+End', fs: 'F11', toolbar: 'Ctrl+Shift+T' }, userDefaults: null };
    draw();
  }

  function adminHtml() {
    const d = P.defaultPerms;
    return `<div class="lbl">Company policy · Administrators only <span class="tag" style="margin-left:6px">ADMIN</span></div>
<div class="box"><div class="bh">${IC('power')}Remote help in the company</div>
${row('Remote help is on', 'Turn it off to stop every new connection at once. Running sessions are not cut.', sw('poff', P.remoteOn))}</div>
<div class="box"><div class="bh">${IC('users')}Who can use Remote <span>· ICT role is enough by default</span></div>
${row('Allowed people', 'Everyone with the ICT or Administrator role, or only the people you tick below.', pill('pmode', [['role', 'ICT role'], ['list', 'Only listed']], P.mode))}
<div id="plist">${people.map(p => `<div class="srow" data-p="${p.id}"><span class="avatar" style="background:#4f6b8f">${ini(p.name)}</span><div class="tx"><div class="t1">${p.name}</div><div class="t2">${p.role}${p.allowed ? '' : ' · blocked'}</div></div>${p.allowed ? '' : '<span class="stt" style="color:var(--danger);background:var(--danger-soft)">BLOCKED</span>'}${sw('pp-' + p.id, p.allowed)}</div>`).join('')}</div></div>
<div class="box"><div class="bh">${IC('hand')}Asking the user</div>
${row('Always ask for permission', 'The user gets an Allow prompt before anyone sees their screen. Turn off only for managed PCs.', sw('preq', P.requirePermission))}
${row('Minimum password length', 'For unattended access passwords set in Devices.', sel('pminpw', [[8, '8 characters'], [10, '10 characters'], [12, '12 characters'], [16, '16 characters']], P.minPassword))}
${row('Time to answer', 'After this the request counts as “No answer”.', sel('pask', [[15, '15 seconds'], [30, '30 seconds'], [60, '60 seconds'], [120, '2 minutes']], P.askSeconds))}
${row('Allow unattended access', 'Per-PC switch + password set in Devices. Off turns it off everywhere.', sw('pua', P.allowUnattended))}
<div class="bh" style="border-top:1px solid var(--sep)">${IC('check-square')}Default ticks in the user's prompt <span>· the user can still change them</span></div>
${perm('pd-control', 'mouse-pointer-2', 'Mouse and keyboard', d.control)}${perm('pd-clipboard', 'clipboard-copy', 'Clipboard', d.clipboard)}${perm('pd-files', 'file-up', 'File transfer', d.files)}</div>
<div class="box"><div class="bh">${IC('gauge')}Quality and files</div>
${row('Default quality', 'Auto adapts to the network.', pill('pq', [['auto', 'Auto'], ['smooth', 'Smooth'], ['quality', 'Sharp']], P.quality))}
${row('Largest file', 'Per file, both directions. Files stream straight between the two PCs, so even 2 GB does not use memory.', sel('pmax', [[200, '200 MB'], [1024, '1 GB'], [2048, '2 GB'], [5120, '5 GB']], P.maxFileMB))}
${row('If the connection drops', 'ICT\'s viewer tries to reconnect by itself, then ends the session.', `<span style="display:flex;gap:8px">${sel('prn', [[3, '3 tries'], [5, '5 tries'], [6, '6 tries'], [8, '8 tries']], P.reconnectAttempts)}${sel('prs', [[15, '15 s'], [30, '30 s'], [60, '60 s']], P.reconnectSeconds)}</span>`)}</div>
<div class="box"><div class="bh">${IC('shield-alert')}Security and history</div>
${row('Wrong password limit', 'The PC locks, an Administrator can unlock it in Devices.', `<span style="display:flex;gap:8px">${sel('plk', [[3, '3 wrong'], [5, '5 wrong'], [10, '10 wrong']], P.lockAttempts)}${sel('plm', [[5, '5 min'], [15, '15 min'], [60, '1 hour']], P.lockMinutes)}</span>`)}
${row('Connection limit per person', 'Sessions one ICT person can start per hour.', sel('prate', [[5, '5 per hour'], [10, '10 per hour'], [20, '20 per hour'], [50, '50 per hour']], P.ratePerHour))}
${row('Tell Administrators when a PC locks', 'A message in the Inbox of every Administrator after too many wrong passwords.', sw('pnote', P.notifyAdminOnLock))}
${row('Keep session history', 'Who, which PC, when, how long and the result. Chat is never stored.', sel('pret', [[90, '90 days'], [180, '180 days'], [365, '1 year'], [730, '2 years']], P.retentionDays))}</div>
<div class="row" style="padding:0 4px 8px"><button class="btn" id="preset">Reset to defaults</button></div>`;
  }

  function viewerHtml() {
    return `<div class="lbl">My viewer · every ICT person <span class="tag" style="margin-left:6px">NEW</span></div>
<div class="box"><div class="bh">${IC('monitor')}Picture</div>
${row('Quality when I connect', 'Starts the viewer with this. You can still change it in the toolbar.', pill('uq', [['auto', 'Auto'], ['smooth', 'Smooth'], ['quality', 'Sharp']], U.quality))}
${row('Screen size', 'Fit shows the whole screen; Actual shows pixels 1:1 with scrollbars; Fill covers the window.', pill('ufit', [['fit', 'Fit'], ['actual', 'Actual'], ['fill', 'Fill']], U.fit))}
${row('Hide the toolbar automatically', 'Slides away after a few seconds; move the mouse to the top to bring it back.', sw('uhide', U.autoHide))}</div>
<div class="box"><div class="bh">${IC('keyboard')}Shortcuts</div>
${row('Ctrl + Alt + Del', 'Windows keeps the real keys for itself, so the viewer sends the command for you.', `<button class="btn kbtn" data-k="cad">${U.keys.cad}</button>`)}
${row('Full screen', '', `<button class="btn kbtn" data-k="fs">${U.keys.fs}</button>`)}
${row('Show / hide toolbar', '', `<button class="btn kbtn" data-k="toolbar">${U.keys.toolbar}</button>`)}</div>
<div class="box"><div class="bh">${IC('folder-down')}Files and chat</div>
${row('Save received files in', 'Files sent by the user are saved here.', `<span style="display:flex;gap:8px;align-items:center"><span class="mono" style="font-size:12.5px;color:var(--ink-2)" id="udir">${U.saveDir || 'Downloads\\iPop Remote'}</span><button class="btn" id="uchg">Change…</button></span>`)}
${row('Sound for new chat messages', 'A short sound when the user writes while you look at the screen.', sw('usnd', U.chatSound))}</div>`;
  }

  function userHtml() {
    const d = U.userDefaults || (P ? P.defaultPerms : DEF);
    return `<div class="lbl">On the user's PC <span class="tag" style="margin-left:6px">NEW</span></div>
<div class="box"><div class="bh">${IC('user-round')}Remote help <span>· everyone, no admin needed</span></div>
${row('Ticked by default when ICT asks', 'Only what you leave ticked is allowed. You can still change it in each prompt.', '<span></span>')}
${perm('ud-control', 'mouse-pointer-2', 'Mouse and keyboard', d.control)}${perm('ud-clipboard', 'clipboard-copy', 'Clipboard', d.clipboard)}${perm('ud-files', 'file-up', 'File transfer', d.files)}
<div class="srow"><div class="tx"><div class="t2">${U.userDefaults ? 'You changed this. <a style="color:var(--accent-ink);cursor:pointer" id="udreset">Use the company default</a>' : 'Following the company default. '}</div></div></div></div>`;
  }

  function draw() {
    host.innerHTML = `<h3>Remote</h3>
<p class="lead">Remote help for ICT: who may connect, what the user is asked, and how sessions behave.</p>
${isAdmin ? adminHtml() : isIct ? `<div class="note" style="margin:0 0 18px;padding:12px 14px;border-radius:12px;background:var(--fill);display:flex;gap:10px;align-items:center">${IC('lock')}<span>Company policy (who can use Remote, wait time, lockout, file limits) is set by Administrators.</span></div>` : ''}
${isIct ? viewerHtml() : ''}${userHtml()}`;
    wire();
  }

  const flash = () => { const el = $('#saved'); el.querySelector('span').textContent = 'Saved · synced to iPop'; el.classList.add('on'); clearTimeout(flash.t); flash.t = setTimeout(() => el.classList.remove('on'), 1600); };
  async function savePolicy() { const r = await api('PUT', 'api/notifier/remote/policy', P); if (r.ok) { P = r.json; flash(); } else app.snack('Couldn\'t save the policy'); draw(); }
  async function savePrefs() { const r = await api('PUT', 'api/me/ui-prefs/remote', U); if (r.ok || r.status === 204) { flash(); post({ type: 'remotePrefs', prefs: U }); } else app.snack('Couldn\'t save'); draw(); }

  function wire() {
    const pv = id => host.querySelector('#' + id);
    const togg = (id, fn, save) => { const e = pv(id); if (e) e.onclick = () => { fn(); save(); }; };
    const segs = (id, fn, save) => { const e = pv(id); if (e) e.onclick = ev => { const b = ev.target.closest('button'); if (b) { fn(b.dataset.v); save(); } }; };
    const sels = (id, fn, save) => { const e = pv(id); if (e) e.onchange = () => { fn(e.value); save(); }; };
    if (isAdmin) {
      const S = savePolicy;
      segs('pmode', v => { P.mode = v; if (v === 'list') people.forEach(p => { if (P.people[p.id] === undefined) P.people[p.id] = true; }); }, S);
      people.forEach(p => togg('pp-' + p.id, () => { P.people[p.id] = !p.allowed; if (P.mode !== 'list' && !p.allowed) P.people[p.id] = true; }, S));
      togg('poff', () => { P.remoteOn = !P.remoteOn; }, S); togg('pnote', () => { P.notifyAdminOnLock = !P.notifyAdminOnLock; }, S);
      togg('preq', () => { P.requirePermission = !P.requirePermission; }, S); togg('pua', () => { P.allowUnattended = !P.allowUnattended; }, S);
      ['control', 'clipboard', 'files'].forEach(k => togg('pd-' + k, () => { P.defaultPerms[k] = !P.defaultPerms[k]; }, S));
      sels('pminpw', v => { P.minPassword = +v; }, S); sels('pask', v => { P.askSeconds = +v; }, S); sels('prate', v => { P.ratePerHour = +v; }, S);
      segs('pq', v => { P.quality = v; }, S); sels('pmax', v => { P.maxFileMB = +v; }, S); sels('prn', v => { P.reconnectAttempts = +v; }, S); sels('prs', v => { P.reconnectSeconds = +v; }, S);
      sels('plk', v => { P.lockAttempts = +v; }, S); sels('plm', v => { P.lockMinutes = +v; }, S); sels('pret', v => { P.retentionDays = +v; }, S);
      const rs = pv('preset'); if (rs) rs.onclick = () => { P = { remoteOn: true, mode: 'role', people: {}, requirePermission: true, askSeconds: 30, allowUnattended: true, minPassword: 10, defaultPerms: { ...DEF }, quality: 'auto', maxFileMB: 2048, reconnectAttempts: 6, reconnectSeconds: 60, lockAttempts: 5, lockMinutes: 15, ratePerHour: 10, notifyAdminOnLock: true, retentionDays: 365 }; savePolicy(); };
    }
    const W = savePrefs;
    segs('uq', v => { U.quality = v; }, W); segs('ufit', v => { U.fit = v; }, W); togg('uhide', () => { U.autoHide = !U.autoHide; }, W); togg('usnd', () => { U.chatSound = !U.chatSound; }, W);
    host.querySelectorAll('.kbtn').forEach(b => b.onclick = () => {
      b.textContent = 'Press keys…'; b.classList.add('on');
      const h = e => { e.preventDefault(); if (['Control', 'Alt', 'Shift', 'Meta'].includes(e.key)) return; U.keys[b.dataset.k] = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift'].filter(Boolean).concat(e.key.length === 1 ? e.key.toUpperCase() : e.key).join('+'); document.removeEventListener('keydown', h, true); savePrefs(); };
      document.addEventListener('keydown', h, true);
    });
    const c = pv('uchg'); if (c) c.onclick = () => post({ type: 'pickFolder', current: U.saveDir || '' });
    ['control', 'clipboard', 'files'].forEach(k => togg('ud-' + k, () => { U.userDefaults = U.userDefaults || { ...(P ? P.defaultPerms : DEF) }; U.userDefaults[k] = !U.userDefaults[k]; }, W));
    const r = pv('udreset'); if (r) r.onclick = () => { U.userDefaults = null; savePrefs(); };
  }
  app.on('folderPicked', m => { if (m.path) { U.saveDir = m.path; savePrefs(); } });

  // Built when the section is first opened (the policy and prefs are fetched then).
  document.querySelector('#nav a[data-s=remote]').addEventListener('click', () => { if (!P && !U) load(); });
  window.settingsRemote = { load };
})();
