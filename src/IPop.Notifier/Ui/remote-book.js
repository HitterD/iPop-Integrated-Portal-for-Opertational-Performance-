// Remote window (BIGIS #8 v3, mockup ipop-remote-v3 png/01, 27, 28, 29): connect by 9-digit ID / PC name / person;
// address book with favourites, recent, groups (tab + per-group list), status, "IN SESSION" and "EXCLUDED"; errors as
// a banner here (not a tray balloon); a PC someone else is helping asks "Join view-only" or Cancel. Session history
// is in remote-book-history.js. "Not installed" is phase 1b (Active Directory) and has no source yet.
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], recent = [], view = 'all', t, me = { isAdmin: false };
  $('[data-min]').innerHTML = ICON.min; $('[data-max]').innerHTML = ICON.max; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-close]').onclick = () => post({ type: 'close' });
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');
  const digits = s => s.replace(/[\s-]/g, '');
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' d ago'; };
  const label = d => d.alias || d.machineName;
  const groupOf = d => d.group || 'Other';

  async function load() {
    const r = await api('GET', 'api/notifier/remote/book');
    if (!r.ok) { $('#devs').innerHTML = `<div class="empty">${r.status === 403 ? 'Only ICT and Administrators can use Remote.' : 'Can\'t load computers.'}</div>`; return; }
    rows = r.json.rows || []; recent = r.json.recent || [];
    drawGroups(); draw();
  }

  function row(d) {
    const ex = d.excluded;
    return `<div class="dev${ex ? ' na' : ''}" data-id="${d.deviceId}"><span class="pc${ex ? ' na' : ''}">${IC(d.laptop ? 'laptop' : 'monitor')}<i class="dot${d.online ? '' : ' off'}"></i></span>
      <span class="m"><b>${esc(label(d))}${d.alias ? ` <small style="display:inline;color:var(--ink-3);font-weight:500">${esc(d.machineName)}</small>` : ''}${d.busyBy ? ` <span class="stt" title="Being helped by ${esc(d.busyBy)}">IN SESSION</span>` : ''}${d.unattended ? ' <span class="ua">UNATTENDED</span>' : ''}</b><small>${esc(d.userName)} · ${d.online ? esc(d.ip || '') : 'last seen ' + esc(ago(d.lastSeenAt))}</small></span>
      <span class="cn">${d.remoteId ? `<span class="did mono">${fmtId(d.remoteId)}</span><button title="Copy ID" class="wc" data-a="copy" style="width:30px;height:30px">${IC('copy')}</button>` : ''}<button title="Favourite" class="wc fav${d.favourite ? ' on' : ''}" data-a="fav" style="width:30px;height:30px">${IC('star')}</button>
      ${ex ? `<span class="stt" title="Excluded from remote help by an Administrator">EXCLUDED</span><button class="btnc dis" data-a="go">Connect</button>` : d.online ? `<button class="btnc" data-a="go">${IC('screen-share')}Connect</button>` : `<span class="stt">OFFLINE</span><button class="btnc dis" data-a="go">Connect</button>`}</span></div>`;
  }
  const grp = (ic, t, extra = '') => `<div class="grp">${IC(ic)}${t}${extra}</div>`;
  const groups = () => [...new Set(rows.map(groupOf))].sort((a, b) => a === 'Other' ? 1 : b === 'Other' ? -1 : a.localeCompare(b));

  function drawGroups() {
    $('#groups').innerHTML = groups().map(g => `<div class="nv" data-v="g:${esc(g)}">${IC('folder')}${esc(g)}<small>${rows.filter(d => groupOf(d) === g).length}</small></div>`).join('');
    bindNav();
  }

  function draw() {
    const qs = $('#q').value.trim().toLowerCase();
    const hit = d => !qs || [d.machineName, d.alias, d.userName, d.ip, d.remoteId, fmtId(d.remoteId)].some(v => (v || '').toLowerCase().includes(qs)) || (digits(qs).length > 2 && (d.remoteId || '').includes(digits(qs)));
    const list = rows.filter(hit);
    const favs = list.filter(d => d.favourite), rec = recent.map(id => list.find(d => d.machineId === id)).filter(d => d && !d.favourite);
    const on = rows.filter(d => d.online).length;
    $('#nAll').textContent = rows.length; $('#nFav').textContent = rows.filter(d => d.favourite).length; $('#nRecent').textContent = recent.length;
    $('#nOn').textContent = on; $('#nOff').textContent = rows.length - on; $('#nNa').textContent = 0; $('#nGrp').textContent = groups().length;
    $('#cnt').textContent = `${rows.length} computer${rows.length === 1 ? '' : 's'} · ${on} online`;
    const histView = view === 'hist';
    $('.srch').style.display = histView ? 'none' : ''; $('#cnt').style.display = histView ? 'none' : '';
    if (histView) return bookHistory.show($('#devs'), me);
    let html = '';
    if (view === 'all') {
      const rest = list.filter(d => !d.favourite && !rec.includes(d));
      if (favs.length) html += grp('star', 'Favourites') + favs.map(row).join('');
      if (rec.length) html += grp('history', 'Recent') + rec.map(row).join('');
      if (rest.length) html += (favs.length || rec.length ? grp('monitor', 'All PCs') : '') + rest.map(row).join('');
    }
    else if (view === 'grp') html = groups().map(g => { const l = list.filter(d => groupOf(d) === g); return l.length ? grp('folder', esc(g), ` <span class="stt">${l.length}</span>`) + l.map(row).join('') : ''; }).join('');
    else if (view.startsWith('g:')) html = list.filter(d => groupOf(d) === view.slice(2)).map(row).join('');
    else if (view === 'fav') html = favs.map(row).join('');
    else if (view === 'recent') html = recent.map(id => list.find(d => d.machineId === id)).filter(Boolean).map(row).join('');
    else if (view === 'online') html = list.filter(d => d.online).map(row).join('');
    else if (view === 'offline') html = list.filter(d => !d.online).map(row).join('');
    else if (view === 'na') html = '<div class="empty">Computers without the iPop Notifier appear here once Active Directory is connected (phase 1b).</div>';
    $('#devs').innerHTML = html || '<div class="empty">No computers found</div>';
    match();
  }

  // The box: what "Connect" would reach ("PC-QC-0024 · Diah Setia Rini").
  function find(v) {
    const d = digits(v);
    if (/^\d{9}$/.test(d)) return rows.find(r => r.remoteId === d);
    const u = v.trim().toUpperCase();
    return u ? rows.find(r => r.machineName.toUpperCase() === u || (r.alias || '').toUpperCase() === u) || rows.find(r => r.userName.toUpperCase() === u) : null;
  }
  function match() { const d = find($('#target').value); $('#match').textContent = d ? `${d.machineName} · ${d.userName}` : ''; }
  // Typed digits group like the ID is shown everywhere ("482 913 576"; a pasted "482-913-576" too).
  $('#target').oninput = e => { const v = e.target.value; if (/^[\d\s-]+$/.test(v)) e.target.value = digits(v).slice(0, 9).replace(/(\d{3})(?=\d)/g, '$1 '); match(); };
  const connect = (deviceId, target) => { banner(null); post(deviceId ? { type: 'connect', deviceId } : { type: 'connect', target }); };
  $('#go').onclick = () => { const v = $('#target').value; if (!v.trim()) return $('#target').focus(); connect(null, v.trim()); };
  $('#target').onkeydown = e => { if (e.key === 'Enter') $('#go').click(); };
  $('#q').oninput = () => { clearTimeout(t); t = setTimeout(draw, 120); };
  function bindNav() {
    document.querySelectorAll('.nv[data-v]').forEach(n => n.onclick = () => {
      view = n.dataset.v; document.querySelectorAll('.nv[data-v]').forEach(x => x.classList.toggle('on', x === n)); draw();
    });
  }
  $('#devs').onclick = async e => {
    const el = e.target.closest('.dev[data-id]'); if (!el) return;
    const d = rows.find(r => r.deviceId === el.dataset.id); if (!d) return;
    const a = e.target.closest('[data-a]')?.dataset.a;
    if (a === 'copy') { post({ type: 'copy', text: d.remoteId }); app.snack('ID copied'); return; }
    if (a === 'fav') {
      if (!d.machineId) return;
      const r = await api('PUT', `api/notifier/remote/machines/${d.machineId}/favourite`, { on: !d.favourite });
      if (r.ok) { d.favourite = !d.favourite; draw(); } else app.snack(r.status === 400 ? 'Favourites are full (40).' : 'Couldn\'t save the favourite');
      return;
    }
    if (a === 'go') { if (d.excluded) app.snack(label(d) + ' is excluded from remote help'); else if (!d.online) app.snack(label(d) + ' is offline'); else connect(d.deviceId); return; }
    $('#target').value = d.remoteId ? fmtId(d.remoteId) : d.machineName; match();
  };
  $('#devs').ondblclick = e => { const el = e.target.closest('.dev[data-id]'); const d = el && rows.find(r => r.deviceId === el.dataset.id); if (d?.online && !d.excluded) connect(d.deviceId); };

  // ── banner (errors that stop a connect before it starts) and the "being helped" dialog ──
  function banner(b) {
    const el = $('#banner');
    if (!b) { el.hidden = true; return; }
    el.hidden = false;
    el.innerHTML = `${IC(b.code === 'rate' ? 'clock' : 'ban')}<span><b>${esc(b.title)}</b><small>${esc(b.message || '')}</small></span><button class="wc" id="bnx" style="width:28px;height:28px">${IC('x')}</button>`;
    $('#bnx').onclick = () => (el.hidden = true);
  }
  app.on('banner', m => banner(m.b));
  app.on('busy', m => { const b = m.b;
    document.querySelectorAll('.dlg').forEach(x => x.remove());
    const d = document.createElement('div'); d.className = 'dlg';
    const first = (b.busyBy || '').split(' ')[0];
    d.innerHTML = `<div class="dlgbox"><div class="ic">${IC('users')}</div><h3>${esc(b.machine)} is being helped by ${esc(b.busyBy)}</h3><p>Two people cannot control the same PC. You cannot end ${esc(first)}'s session. You can watch along, view only, if the user agrees.</p><div class="acts"><button id="dx">Cancel</button><button class="p" id="dj">${IC('eye')}Join view-only</button></div></div>`;
    document.body.appendChild(d);
    d.querySelector('#dx').onclick = () => d.remove();
    d.querySelector('#dj').onclick = () => { d.remove(); post({ type: 'connect', deviceId: b.deviceId, target: b.target, joinViewOnly: true }); };
  });

  app.on('who', m => { me = m; $('#who').textContent = m.name ? `Signed in as ${m.name.split(' ')[0]} (${m.isAdmin ? 'Admin' : 'ICT'})` : ''; draw(); });
  app.on('refresh', load);
  app.on('toast', m => app.snack(m.text));
  window.book = { reload: load, get rows() { return rows; } };
  bindNav();
  app.ready();
  load();
  setInterval(() => { if (view !== 'hist') load(); }, 30000);
  $('#target').focus();
})();
