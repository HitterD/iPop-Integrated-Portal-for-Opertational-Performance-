// Tray inbox: every notification (iPop + iDesk), synced with Notifications in iPop. Held ones show "MISSED".
(function () {
  const { api, esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let ctx = { held: [] }, filter = 'all', items = [], page = 1, total = 0, loading = false;

  $('#readAll').innerHTML = ICON.readAll;
  $('#gear').innerHTML = ICON.gear;
  document.querySelector('[data-max]').innerHTML = ICON.max;
  $('#gear').onclick = () => post({ type: 'settings' });
  $('#devices').onclick = () => post({ type: 'devices' });
  $('#open').onclick = () => post({ type: 'openIPop', path: '/notifications' });
  $('#sw').onclick = () => { $('#sw').classList.toggle('on'); post({ type: 'popups' }); };
  $('#readAll').onclick = async () => {
    const r = await api('POST', 'api/notifications/v1/inbox/read-all');
    if (r.ok) { items.forEach(i => i.isRead = true); draw(); app.snack('All caught up'); }
  };
  $('#seg').onclick = e => {
    const b = e.target.closest('button'); if (!b) return;
    document.querySelectorAll('#seg button').forEach(x => x.classList.toggle('on', x === b));
    filter = b.dataset.f; page = 1; items = []; load();
  };

  app.on('context', m => {
    const first = !ctx.name;
    ctx = m.ctx;
    $('#sub').textContent = `${ctx.name ? ctx.name.split(' ')[0] + ' · ' : ''}${ctx.unread ? ctx.unread + ' unread' : 'all caught up'}`;
    $('#seg [data-f=unread]').innerHTML = ctx.unread ? `Unread <span class="n">${ctx.unread > 99 ? '99+' : ctx.unread}</span>` : 'Unread';
    $('#sw').classList.toggle('on', !!ctx.popups);
    $('#swLabel').textContent = ctx.popups ? 'Popups on' : 'Popups paused';
    $('#devices').hidden = !ctx.ict;
    connection(!!ctx.connected);
    if (first || document.visibilityState === 'visible') { page = 1; load(); }
  });
  // Short blips are not worth a warning: the strip shows only after ~10 s offline, then "Connected again" briefly.
  let offTimer = null, okTimer = null, shownOff = false;
  function connection(up) {
    const el = $('#conn'), txt = $('#connText');
    clearTimeout(okTimer);
    if (!up) {
      if (shownOff || offTimer) return;
      offTimer = setTimeout(() => { offTimer = null; shownOff = true; el.className = 'strip warn on'; txt.textContent = 'Reconnecting to iPop… your notifications are safe'; }, 10000);
      return;
    }
    clearTimeout(offTimer); offTimer = null;
    if (!shownOff) { el.className = 'strip'; return; }
    shownOff = false;
    el.className = 'strip ok on'; txt.textContent = 'Connected again';
    okTimer = setTimeout(() => { el.className = 'strip'; }, 3000);
  }
  app.on('connection', m => connection(!!m.connected));
  app.ready();

  async function load(more) {
    if (loading) return; loading = true;
    if (!more && !items.length) $('#list').innerHTML = skeleton();
    const status = filter === 'unread' ? '&status=unread' : '';
    const type = filter === 'mail' ? '&type=mail' : '';
    const r = await api('GET', `api/notifications/v1/inbox?page=${page}&pageSize=40${status}${type}`);
    loading = false;
    if (!r.ok) { if (!items.length) $('#list').innerHTML = '<div class="empty"><b>Can\'t load notifications</b>Check the connection — they\'ll appear when iPop is reachable.</div>'; return; }
    let rows = r.json.items || [];
    if (filter === 'tickets') rows = rows.filter(i => moduleOf(i) === 'tickets');
    items = more ? items.concat(rows) : rows;
    total = r.json.totalCount;
    draw();
  }

  function moduleOf(i) {
    const u = i.targetUrl || '';
    if (u.startsWith('/tickets') || u.startsWith('/idesk/tickets')) return 'tickets';
    if (u.startsWith('/calendar') || u.includes('tab=scheduled') || u.includes('join=')) return 'calendar';
    if (u.startsWith('/calls')) return 'calls';
    if (u.startsWith('/notes')) return 'notes';
    return { mail: 'mail', chat: 'chat', news: 'news', project: 'projects' }[i.type] || 'system';
  }

  function draw() {
    const list = $('#list');
    if (!items.length) { list.innerHTML = `<div class="empty"><b>${filter === 'unread' ? 'All caught up' : 'Nothing here yet'}</b>New notifications from iPop and iDesk appear here.</div>`; return; }
    const today = new Date().toDateString(), yest = new Date(Date.now() - 864e5).toDateString();
    let last = '', html = '';
    items.forEach((i, idx) => {
      const d = new Date(i.at), key = d.toDateString();
      const g = key === today ? 'Today' : key === yest ? 'Yesterday' : d.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'short' });
      if (g !== last) { html += `<div class="grp">${esc(g)}</div>`; last = g; }
      const mod = moduleOf(i), m = MOD[mod];
      const missed = ctx.held && ctx.held.includes(i.id) && !i.isRead;
      html += `<div class="it ${i.isRead ? 'read' : ''}" data-i="${idx}" style="--m:${m.c};animation-delay:${Math.min(idx, 12) * 18}ms">${i.isRead ? '' : '<i class="dot"></i>'}
        <span class="ico${mod === 'tickets' ? ' idesk' : ''}">${mod === 'tickets' ? '<img src="idesk-mark.png" alt="iDesk">' : (ICON[mod] || ICON.system)}</span>
        <span class="m"><b><span class="t">${esc(i.title)}</span>${missed ? '<span class="missed">MISSED</span>' : ''}<small>${esc(timeShort(i.at))}</small></b><span>${esc(i.body)}</span></span>
        <span class="acts"><button data-a="read" title="${i.isRead ? 'Mark unread' : 'Mark read'}">${i.isRead ? IC('circle-dot') : ICON.check}</button><button data-a="web" title="Open in iPop">${IC('arrow-up-right')}</button></span></div>`;
    });
    if (items.length < total && filter !== 'tickets') html += '<button class="loadmore" id="more">Show more</button>';
    list.innerHTML = html;
    const more = $('#more'); if (more) more.onclick = () => { page++; load(true); };
  }

  $('#list').addEventListener('click', async e => {
    const row = e.target.closest('.it'); if (!row) return;
    const i = items[+row.dataset.i];
    const a = e.target.closest('[data-a]')?.dataset.a;
    if (a === 'read') {
      const r = await api('POST', `api/notifications/v1/notifications/${i.id}/${i.isRead ? 'unread' : 'read'}`);
      if (r.ok) { i.isRead = !i.isRead; draw(); }
      return;
    }
    if (a === 'web') { post({ type: 'openIPop', path: i.targetUrl || '/notifications' }); return; }
    // Click: the notification opens again as its own window, with its actions.
    post({ type: 'openNotice', notice: i });
  });

  function skeleton() {
    return Array.from({ length: 6 }, () => '<div class="it" style="cursor:default"><span class="ico skel" style="background:none"></span><span class="m"><span class="skel" style="height:12px;width:60%;margin:4px 0 8px"></span><span class="skel" style="height:10px;width:85%"></span></span></div>').join('');
  }
})();
