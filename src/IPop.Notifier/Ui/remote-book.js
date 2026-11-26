// Remote window (1.8.0, mockup ipop-remote-v2 prop-remote): connect by 9-digit ID / PC name / person, and an address
// book of every PC with the iPop Notifier — favourites, recent, online/offline. "Not installed" (Active Directory)
// is phase 1b: the filter is there but has no source yet.
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let rows = [], recent = [], view = 'all', t;
  const notInstalled = []; // phase 1b
  $('[data-min]').innerHTML = ICON.min; $('[data-max]').innerHTML = ICON.max; $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-close]').onclick = () => post({ type: 'close' });
  const fmtId = id => id && id.length === 9 ? `${id.slice(0, 3)} ${id.slice(3, 6)} ${id.slice(6)}` : (id || '');
  const digits = s => s.replace(/[\s-]/g, '');
  const ago = iso => { const s = (Date.now() - new Date(iso)) / 1000; return s < 90 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' d ago'; };

  async function load() {
    const r = await api('GET', 'api/notifier/remote/book');
    if (!r.ok) { $('#devs').innerHTML = `<div class="empty">${r.status === 403 ? 'Only ICT and Administrators can use Remote.' : 'Can\'t load computers.'}</div>`; return; }
    rows = r.json.rows || []; recent = r.json.recent || [];
    draw();
  }

  function row(d) {
    const st = d.online ? 'on' : 'off';
    return `<div class="dev" data-id="${d.deviceId}"><span class="pc">${IC(d.laptop ? 'laptop' : 'monitor')}<i class="dot${d.online ? '' : ' off'}"></i></span>
      <span class="m"><b>${esc(d.machineName)}${d.unattended ? ' <span class="ua">UNATTENDED</span>' : ''}</b><small>${esc(d.userName)} · ${d.online ? esc(d.ip || '') : 'last seen ' + esc(ago(d.lastSeenAt))}</small></span>
      <span class="cn">${d.remoteId ? `<span class="did mono">${fmtId(d.remoteId)}</span><button title="Copy ID" class="wc" data-a="copy" style="width:30px;height:30px">${IC('copy')}</button>` : ''}<button title="Favourite" class="wc fav${d.favourite ? ' on' : ''}" data-a="fav" style="width:30px;height:30px">${IC('star')}</button>
      ${st === 'on' ? `<button class="btnc" data-a="go">${IC('screen-share')}Connect</button>` : `<span class="stt">OFFLINE</span><button class="btnc dis" data-a="go">Connect</button>`}</span></div>`;
  }
  const naRow = d => `<div class="dev na"><span class="pc na">${IC('monitor-off')}</span><span class="m"><b>${esc(d.machineName)}</b><small>Notifier not installed${d.ip ? ' · ' + esc(d.ip) : ''}</small></span>
      <span class="cn"><button class="wc" style="width:30px;height:30px;">${IC('star')}</button><span class="stt">NOT INSTALLED</span></span></div>`;
  const grp = (ic, t, extra = '') => `<div class="grp">${IC(ic)}${t}${extra}</div>`;

  function draw() {
    const q = $('#q').value.trim().toLowerCase();
    const hit = d => !q || [d.machineName, d.userName, d.ip, d.remoteId, fmtId(d.remoteId)].some(v => (v || '').toLowerCase().includes(q)) || (digits(q).length > 2 && (d.remoteId || '').includes(digits(q)));
    const list = rows.filter(hit);
    const favs = list.filter(d => d.favourite), rec = recent.map(id => list.find(d => d.machineId === id)).filter(d => d && !d.favourite);
    const on = rows.filter(d => d.online).length;
    $('#nAll').textContent = rows.length; $('#nFav').textContent = rows.filter(d => d.favourite).length; $('#nRecent').textContent = recent.length;
    $('#nOn').textContent = on; $('#nOff').textContent = rows.length - on; $('#nNa').textContent = notInstalled.length;
    $('#cnt').textContent = `${rows.length} computer${rows.length === 1 ? '' : 's'} · ${on} online`;
    let html = '';
    if (view === 'all') {
      const rest = list.filter(d => !d.favourite && !rec.includes(d));
      if (favs.length) html += grp('star', 'Favourites') + favs.map(row).join('');
      if (rec.length) html += grp('history', 'Recent') + rec.map(row).join('');
      if (rest.length) html += (favs.length || rec.length ? grp('monitor', 'All PCs') : '') + rest.map(row).join('');
      if (notInstalled.length) html += grp('package-x', 'Not installed', ' <span class="tag">PHASE 1B</span>') + notInstalled.map(naRow).join('');
    }
    else if (view === 'fav') html = favs.map(row).join('');
    else if (view === 'recent') html = recent.map(id => list.find(d => d.machineId === id)).filter(Boolean).map(row).join('');
    else if (view === 'online') html = list.filter(d => d.online).map(row).join('');
    else if (view === 'offline') html = list.filter(d => !d.online).map(row).join('');
    else if (view === 'na') html = notInstalled.map(naRow).join('') || '<div class="empty">Computers without the iPop Notifier appear here once Active Directory is connected (phase 1b).</div>';
    $('#devs').innerHTML = html || '<div class="empty">No computers found</div>';
    match();
  }

  // The box: what "Connect" would reach, shown on the right ("PC-ICT-014 · Andi Pratama").
  function find(v) {
    const d = digits(v);
    if (/^\d{9}$/.test(d)) return rows.find(r => r.remoteId === d);
    const u = v.trim().toUpperCase();
    return u ? rows.find(r => r.machineName.toUpperCase() === u) || rows.find(r => r.userName.toUpperCase() === u) : null;
  }
  function match() {
    const v = $('#target').value, d = find(v);
    $('#match').textContent = d ? `${d.machineName} · ${d.userName}` : '';
  }
  // Typing digits groups them "482 913 576" like the ID is shown everywhere.
  $('#target').oninput = e => {
    const el = e.target, v = el.value;
    if (/^[\d\s-]+$/.test(v)) { const d = digits(v).slice(0, 9); el.value = d.replace(/(\d{3})(?=\d)/g, '$1 '); }
    match();
  };
  const connect = target => { if (!target.trim()) return $('#target').focus(); post({ type: 'connect', target: target.trim() }); };
  $('#go').onclick = () => connect($('#target').value);
  $('#target').onkeydown = e => { if (e.key === 'Enter') connect($('#target').value); };

  $('#q').oninput = () => { clearTimeout(t); t = setTimeout(draw, 120); };
  document.querySelectorAll('.nv[data-v]').forEach(n => n.onclick = () => {
    view = n.dataset.v; document.querySelectorAll('.nv[data-v]').forEach(x => x.classList.toggle('on', x === n)); draw();
  });
  $('#devs').onclick = async e => {
    const el = e.target.closest('.dev[data-id]'); if (!el) return;
    const d = rows.find(r => r.deviceId === el.dataset.id); if (!d) return;
    const a = e.target.closest('[data-a]')?.dataset.a;
    if (a === 'copy') { post({ type: 'copy', text: d.remoteId }); app.snack('ID copied'); return; }
    if (a === 'fav') {
      if (!d.machineId) return;
      const on = !d.favourite;
      const r = await api('PUT', `api/notifier/remote/machines/${d.machineId}/favourite`, { on });
      if (r.ok) { d.favourite = on; draw(); } else app.snack(r.status === 400 ? 'Favourites are full (40).' : 'Couldn\'t save the favourite');
      return;
    }
    if (a === 'go') { if (d.online) post({ type: 'connect', deviceId: d.deviceId }); else app.snack(`${d.machineName} is offline`); return; }
    $('#target').value = d.remoteId ? fmtId(d.remoteId) : d.machineName; match();
  };
  $('#devs').ondblclick = e => { const el = e.target.closest('.dev[data-id]'); const d = el && rows.find(r => r.deviceId === el.dataset.id); if (d?.online) post({ type: 'connect', deviceId: d.deviceId }); };

  app.on('who', m => { $('#who').textContent = m.name ? `Signed in as ${m.name.split(' ')[0]} (ICT)` : ''; });
  app.on('refresh', load);
  app.ready();
  load();
  setInterval(load, 30000);
  $('#target').focus();
})();
