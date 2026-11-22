// Inbox (Notifier 1.5.0, docs/notifier/PRIORITY-150.md; look + motion: docs/mockups/notifier/priority.html screens 1–2).
// Every notification (iPop + iDesk), synced with Notifications in iPop. Rows are grouped per thread (inbox-model.js) and
// sorted into tiers with tiers.js + the app's context. Small = the tray flyout; html.max = three panes + Table view.
(function () {
  const { api, esc, post } = window.app;
  const M = window.inboxModel, F = window.files;
  const $ = s => document.querySelector(s), $$ = s => [...document.querySelectorAll(s)];
  const rm = () => window.reducedMotion(); // Settings › Animations "Reduced" (html.reduce-motion)
  const reflow = el => void el.offsetWidth;
  const G = '[0-9a-fA-F-]{36}';
  const CHECK = F.CHECK;
  const isMax = () => document.documentElement.classList.contains('max');

  const MODS = {
    approval: ['stamp', 'Approvals'], mail: ['mail', 'Mail'], chat: ['message-circle', 'Chat'], tickets: [null, 'iDesk'],
    calendar: ['calendar', 'Calendar'], projects: ['kanban', 'Projects'], notes: ['notebook-pen', 'Notes'], calls: ['phone', 'Calls'],
    news: ['newspaper', 'News & memos'], system: ['bell', 'iPop'],
  };
  const TIER = { act: ['Needs action', 'var(--act)', 'Needs action'], msg: ['Messages', 'var(--unread)', 'Message'], info: ['For your info', 'var(--ink-4)', 'Info'] };
  const VIEWS = [['act', 'Needs action', 'zap', 'var(--act)', 'act'], ['lead', 'From leaders', 'crown', 'var(--lead)', ''], ['appr', 'Approvals', 'stamp', 'var(--appr)', ''],
    ['msg', 'Messages', 'message-square', 'var(--unread)', ''], ['info', 'For your info', 'info', 'var(--ink-3)', 'g'], ['all', 'All', 'inbox', 'var(--ink-3)', 'g']];
  const TSTATUS = { new: 'New', rep: 'Replied', res: 'Resolved' };
  const SIDE_MODS = ['mail', 'chat', 'tickets', 'calendar', 'projects', 'notes', 'calls', 'news', 'system'];
  const modIcon = m => m === 'tickets' ? '<img src="idesk-mark.png" alt="iDesk">' : IC((MODS[m] || MODS.system)[0]);
  const modName = m => (MODS[m] || MODS.system)[1];
  const PAGE = 100;

  let ctx = { held: [], leaders: [], vips: [], tiers: {} };
  let rows = [], total = 0, page = 1, loading = false, loaded = false, failed = false;
  let threads = [];
  const known = new Set();
  const st = { f: 'all', q: '', wf: 'act', wq: '', sel: null, view: 'list', tsort: 'desc', col: {} };

  // ── sliding thumbs + ticking counts ────────────────────────────────────────
  function placeThumb(c, instant) {
    const t = c.querySelector(':scope>.thumb'), b = c.querySelector(':scope>button.on');
    if (!t) return;
    if (!b || !c.offsetParent) { t.style.width = '0'; return; }
    if (instant || rm()) t.style.transition = 'none';
    t.style.width = b.offsetWidth + 'px'; t.style.height = b.offsetHeight + 'px'; t.style.transform = `translate(${b.offsetLeft}px,${b.offsetTop}px)`;
    if (instant || rm()) { reflow(t); t.style.transition = ''; }
  }
  const placeAll = instant => $$('.slider').forEach(c => placeThumb(c, instant));
  function setOn(c, btn) { c.querySelectorAll(':scope>button').forEach(b => b.classList.toggle('on', b === btn)); placeThumb(c); }
  function setNum(el, v) {
    const s = String(v > 99 ? '99+' : v);
    if (el.textContent !== s) { el.textContent = s; el.classList.remove('tick'); reflow(el); if (v > 0) el.classList.add('tick'); }
    if (el.classList.contains('badge') || el.tagName === 'EM') el.classList.toggle('zero', !v);
  }
  addEventListener('resize', () => { placeAll(true); placeSearch(); });
  // The search button sits just left of the header tool group (mark all, settings | full screen).
  function placeSearch() {
    const tools = $('#ihead .tools');
    if (tools.offsetWidth) $('#sbox').style.setProperty('--r', (tools.offsetWidth + 16 + 8) + 'px');
  }

  // ── chrome: header, footer, window buttons ────────────────────────────────
  $$('[data-gear]').forEach(b => b.onclick = () => post({ type: 'settings' }));
  $$('[data-devices]').forEach(b => b.onclick = () => { closeMenu(); post({ type: 'devices' }); });
  // Full screen (1.6.0): a visible header button instead of the ⋯ menu; hidden when the window can't maximize.
  function closeMenu() {}
  $('[data-maxitem]').onclick = () => post({ type: 'maximize' });
  function syncMaxItem() { const wm = $('#wide [data-max]'); $('[data-maxitem]').hidden = !!wm && wm.hidden; }
  $$('[data-min]').forEach(b => b.onclick = () => post({ type: 'minimize' }));
  $$('[data-close]').forEach(b => b.onclick = () => post({ type: 'close' }));
  $('[data-open]').onclick = () => post({ type: 'openIPop', path: '/notifications' });
  $$('[data-pt]').forEach(b => b.onclick = () => { const on = !b.classList.contains('on'); popupsUi(on); post({ type: 'popups' }); });
  function popupsUi(on) {
    $$('[data-pt]').forEach(t => t.classList.toggle('on', on));
    $$('[data-pt-label]').forEach(l => { l.textContent = on ? 'Popups on' : 'Popups paused'; l.classList.toggle('paused', !on); });
  }

  // ── context from the app ──────────────────────────────────────────────────
  app.on('context', m => {
    const first = !loaded && !loading;
    ctx = Object.assign({ held: [], leaders: [], vips: [], tiers: {} }, m.ctx);
    const who = ctx.name ? ctx.name.split(' ')[0] + ' · ' : '';
    $$('[data-unread-text]').forEach(el => el.textContent = who + (ctx.unread ? ctx.unread + ' unread' : 'All caught up'));
    popupsUi(!!ctx.popups);
    $$('[data-devices]').forEach(b => b.hidden = !ctx.ict);
    syncMaxItem();
    placeSearch();
    connection(!!ctx.connected);
    if (first || document.visibilityState === 'visible') load(false); else regroup();
  });

  // Short blips are not worth a warning: the strip shows only after ~10 s offline, then "Connected again" briefly.
  let offTimer = null, okTimer = null, shownOff = false;
  function strip(cls, text) { $$('[data-conn]').forEach(el => { el.className = 'strip' + (cls ? ' ' + cls : ''); if (text) el.querySelector('span').textContent = text; }); }
  function connection(up) {
    clearTimeout(okTimer);
    if (!up) {
      if (shownOff || offTimer) return;
      offTimer = setTimeout(() => { offTimer = null; shownOff = true; strip('warn on', 'Reconnecting to iPop… your notifications are safe'); }, 10000);
      return;
    }
    clearTimeout(offTimer); offTimer = null;
    if (!shownOff) { strip(''); return; }
    shownOff = false;
    strip('ok on', 'Connected again');
    okTimer = setTimeout(() => strip(''), 3000);
  }
  app.on('connection', m => connection(!!m.connected));

  // Maximize / restore: the other layout draws fresh, thumbs find their buttons again.
  new MutationObserver(() => { if (loaded) drawAll({ still: true }); requestAnimationFrame(() => placeAll(true)); })
    .observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
  app.ready();

  // ── data ───────────────────────────────────────────────────────────────────
  // Page 1 again on every context (new notification, read elsewhere…); older pages already loaded stay.
  async function load(more) {
    if (loading) return; loading = true;
    if (!loaded) skeleton();
    const p = more ? page + 1 : 1;
    const r = await api('GET', `api/notifications/v1/inbox?page=${p}&pageSize=${PAGE}`);
    loading = false;
    if (!r.ok) {
      if (!loaded) { failed = true; drawAll(); }
      return;
    }
    failed = false;
    const got = r.json.items || [];
    total = r.json.totalCount || 0;
    let fresh = [];
    if (more) { page = p; const ids = new Set(rows.map(x => x.id)); rows = rows.concat(got.filter(x => !ids.has(x.id))); }
    else {
      const ids = new Set(got.map(x => x.id));
      const oldest = got.length ? got[got.length - 1].at : null;
      if (loaded) fresh = got.filter(x => !known.has(x.id)).map(x => x.id);
      rows = got.concat(rows.filter(x => !ids.has(x.id) && oldest && x.at < oldest && page > 1));
      if (page > 1 && got.length < PAGE) page = 1;
    }
    rows.forEach(x => known.add(x.id));
    const firstDraw = !loaded;
    loaded = true;
    regroup(firstDraw ? {} : { still: true, fresh });
  }

  function regroup(o) {
    threads = M.group(rows, ctx);
    drawAll(o || { still: true });
  }
  const byKey = k => threads.find(t => t.key === k);

  // ── shared rows ────────────────────────────────────────────────────────────
  function chipsFor(t) {
    let s = '';
    if (t.approval) s += `<span class="chip appr">${IC('stamp')}Approval</span>`;
    if (t.leader) s += t.leader === 'VIP' ? `<span class="chip lead">${IC('star')}VIP</span>` : `<span class="chip lead">${IC('crown')}Your leader</span>`;
    if (t.assigned) s += `<span class="chip act">${IC('user-check')}Assigned to you</span>`;
    if (t.soon) s += `<span class="chip act">${IC('alarm-clock')}Starts soon</span>`;
    if (t.unread && (ctx.held || []).some(id => t.ids.includes(id))) s += `<span class="chip miss">Missed</span>`;
    if (t.items.length > 1) s += `<span class="chip thr" data-x>${t.items.length} updates${IC('chevron-down')}</span>`;
    if (t.status) s += `<span class="tchip ${t.status}"><i></i>${TSTATUS[t.status]}</span>`;
    if (t.line.tag) s += `<span class="chip mod">${esc(t.line.tag)}</span>`;
    return s ? `<div class="chipsr">${s}</div>` : '';
  }
  function row(t, i, o) {
    const info = t.tier === 'info', who = t.line.who ? `<b>${esc(t.line.who)}</b> · ` : '';
    const anim = o.fresh && t.ids.some(id => o.fresh.includes(id)) ? 'new' : o.still ? 'still' : '';
    const xp = t.items.length > 1 ? `<div class="xp"><div>${t.items.slice(1, 8).map(x => `<div><b>${esc(timeShort(x.at))}</b> ${esc(M.lineOf(x).text || x.title)}</div>`).join('')}${t.items.length > 8 ? `<div>+${t.items.length - 8} more</div>` : ''}</div></div>` : '';
    return `<div class="it m-${t.mod} t-${t.tier} ${t.unread ? '' : 'read'} ${info ? 'info' : ''} ${o.sel === t.key ? 'sel' : ''} ${anim}" data-k="${esc(t.key)}" style="--i:${i}" tabindex="-1">
      <span class="dot"></span><div class="ico ${info && t.mod !== 'tickets' ? 'muted' : ''}">${modIcon(t.mod)}</div>
      <div class="main"><div class="l1"><div class="tl">${esc(t.head.title)}</div><div class="tm" title="${esc(new Date(t.at).toLocaleString())}">${esc(M.relTime(t.at))}</div></div><div class="who">${who}${esc(t.line.text)}</div>${info && !t.items[1] ? '' : chipsFor(t)}${xp}</div>
      <div class="qa"><button data-a="read" title="${t.unread ? 'Mark as read' : 'Mark as unread'}">${IC(t.unread ? 'check' : 'mail')}</button><button data-a="snooze" title="Snooze 1 hour">${IC('alarm-clock')}</button></div></div>`;
  }
  const emptyHtml = (t, s, err) => `<div class="empty ${err ? 'err' : ''}"><svg viewBox="0 0 52 52"><circle cx="26" cy="26" r="23"/>${err ? '<path d="M26 15v14M26 36v1"/>' : '<path d="M15 27l7 7 15-16"/>'}</svg><b>${esc(t)}</b><span>${esc(s)}</span></div>`;
  const moreHtml = () => rows.length < total ? `<div class="more"><button data-more>${IC('history')}Show older</button></div>` : '';
  function skeleton() {
    const h = Array.from({ length: 6 }, () => '<div class="skrow"><span class="skel"></span><span style="flex:1"><span class="skel" style="display:block;height:12px;width:60%;margin:4px 0 8px"></span><span class="skel" style="display:block;height:10px;width:85%"></span></span></div>').join('');
    $('#smallList').innerHTML = h; $('#wideList').innerHTML = h;
  }
  function failHtml() { return emptyHtml("Can't load notifications", "Check the connection — they'll appear when iPop is reachable.", true); }

  function drawAll(o) {
    o = o || {};
    if (isMax()) { drawSide(); if (st.view === 'list') { drawList(o); ensureSel(); } else drawTable(o); }
    else drawSmall(o);
    counts();
    window.icons();
  }

  // ── counts: chips, sidebar, summary, list header ───────────────────────────
  function counts() {
    const c = M.counts(threads);
    $$('[data-count]').forEach(el => { const k = el.dataset.count; setNum(el, k.startsWith('mod:') ? c.mod[k.slice(4)] || 0 : c[k] || 0); });
    // 1.6.0: three plain counters (Act / Messages / Info), no sparkle; zero ones fade back.
    const sum = $('#summary');
    sum.hidden = !loaded || failed;
    for (const k of ['act', 'msg', 'info']) {
      const b = sum.querySelector(`[data-sum="${k}"]`); setNum(b, c[k] || 0);
      b.closest('button').classList.toggle('zero', !c[k]);
    }
    if (isMax()) {
      const xs = wideItems();
      $('#lCount').textContent = `${xs.filter(t => t.unread).length} unread · ${xs.length} total`;
    }
    window.icons();
  }
  $('#summary').addEventListener('click', e => { const b = e.target.closest('[data-jump]'); if (b) $(`#chips>button[data-f="${b.dataset.jump}"]`).click(); });

  // ── small inbox ────────────────────────────────────────────────────────────
  function drawSmall(o) {
    const el = $('#smallList');
    if (failed && !rows.length) { el.innerHTML = failHtml(); return; }
    const xs = threads.filter(t => M.matches(t, st.q)), f = st.f;
    let h = '', i = 0;
    const R = t => row(t, i++, o);
    const act = xs.filter(t => t.tier === 'act');
    if ((f === 'all' || f === 'act') && act.length) h += `<div class="grp act">${IC('zap')}Needs action<span class="ln"></span></div><div class="pinbox">${act.map(R).join('')}</div>`;
    const rest = xs.filter(t => t.tier !== 'act' && (f === 'all' || f === t.tier));
    h += byDay(rest, R);
    if (!h) h = st.q ? emptyHtml('No matches', `Nothing contains “${st.q}”.`) : emptyHtml('You’re all caught up', f === 'all' ? 'New notifications from iPop and iDesk appear here.' : 'Nothing waiting in this category.');
    if (!st.q) h += moreHtml();
    el.innerHTML = h;
  }
  function byDay(list, R) {
    let h = '', last = '';
    for (const t of list) {
      const d = M.dayLabel(t.at);
      if (d !== last) { h += `<div class="grp">${esc(d)}<span class="ln"></span></div>`; last = d; }
      h += R(t);
    }
    return h;
  }
  $('#chips').addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b) return;
    st.f = b.dataset.f; setOn($('#chips'), b); drawSmall({}); window.icons(); $('#smallList').scrollTop = 0;
  });
  (() => {
    const box = $('#sbox'), inp = $('#sq'), head = $('#ihead');
    const open = () => { box.classList.add('open'); head.classList.add('searching'); setTimeout(() => inp.focus(), 120); };
    const close = () => { box.classList.remove('open'); head.classList.remove('searching'); inp.value = ''; if (st.q) { st.q = ''; drawSmall({ still: true }); window.icons(); } inp.blur(); };
    box.querySelector('.sbtn').onclick = () => box.classList.contains('open') ? inp.focus() : open();
    box.querySelector('.sx').onclick = close;
    inp.addEventListener('input', () => { st.q = inp.value.trim(); drawSmall({ still: true }); window.icons(); });
    // Esc closes the search first (the window's own Esc handler would hide the Inbox).
    inp.addEventListener('keydown', e => { if (e.key === 'Escape') { e.stopPropagation(); close(); } });
  })();

  // Row clicks in both lists.
  function bindRows(el, where) {
    el.addEventListener('click', e => {
      if (e.target.closest('[data-more]')) { e.target.closest('[data-more]').innerHTML = '<span class="spin"></span>'; load(true); return; }
      const r = e.target.closest('.it'); if (!r) return;
      const t = byKey(r.dataset.k); if (!t) return;
      const a = e.target.closest('[data-a]');
      if (a) { e.stopPropagation(); if (a.dataset.a === 'read') toggleRead(t); else snooze(t); return; }
      if (e.target.closest('[data-x]')) { r.classList.toggle('open'); return; }
      if (where === 'wide') select(t.key);
      else post({ type: 'openNotice', notice: plain(t.head) }); // the notification opens again as its own window
    });
  }
  bindRows($('#smallList'), 'small');
  bindRows($('#wideList'), 'wide');
  const plain = i => ({ id: i.id, type: i.type, title: i.title, body: i.body, source: i.source, at: i.at, isRead: i.isRead, priority: i.priority,
    targetUrl: i.targetUrl, snoozedUntil: i.snoozedUntil, actorId: i.actorId, actorName: i.actorName, eventType: i.eventType });

  // ── read / unread / snooze (with Undo) ─────────────────────────────────────
  // Optimistic: the dot shrinks at once; a failed call puts it back.
  function paintRead(t) {
    $$(`.it[data-k="${CSS.escape(t.key)}"]`).forEach(r => {
      r.classList.toggle('read', !t.unread);
      const b = r.querySelector('[data-a="read"]'); if (b) { b.innerHTML = IC(t.unread ? 'check' : 'mail'); b.title = t.unread ? 'Mark as read' : 'Mark as unread'; }
    });
    if (isMax() && st.view === 'table') drawTable({ still: true });
    const rb = $('#rpane [data-ra="read"]');
    if (rb && st.sel === t.key) rb.innerHTML = IC(t.unread ? 'check' : 'mail') + (t.unread ? 'Mark as read' : 'Mark as unread');
    counts(); window.icons();
  }
  async function setRead(t, read, quiet) {
    const ids = read ? t.items.filter(x => !x.isRead).map(x => x.id) : [t.head.id];
    if (!ids.length) return [];
    const apply = v => { t.items.forEach(x => { if (ids.includes(x.id)) x.isRead = v; }); t.unread = t.items.some(x => !x.isRead); paintRead(t); };
    apply(read);
    const res = await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/${read ? 'read' : 'unread'}`)));
    if (res.some(r => !r.ok)) { apply(!read); if (!quiet) F.snack(`${IC('circle-alert')}<span>Couldn't reach iPop — try again</span>`); return []; }
    return ids;
  }
  async function toggleRead(t) {
    const read = t.unread;
    const ids = await setRead(t, read);
    if (!ids.length) return;
    F.snack(read ? `${CHECK}<span>Marked as read</span>` : '<span>Marked as unread</span>', [['Undo', () => undoRead(t, ids, read)]]);
  }
  async function undoRead(t, ids, wasRead) {
    t.items.forEach(x => { if (ids.includes(x.id)) x.isRead = !wasRead; });
    t.unread = t.items.some(x => !x.isRead); paintRead(t);
    await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/${wasRead ? 'unread' : 'read'}`)));
  }
  function collapse(el) {
    return new Promise(res => {
      if (rm() || !el.offsetHeight) { el.remove(); return res(); }
      el.style.height = el.offsetHeight + 'px'; el.style.overflow = 'hidden'; reflow(el);
      el.style.transition = 'height .34s var(--spring),opacity .2s,padding .34s var(--spring)';
      el.style.height = '0px'; el.style.opacity = '0'; el.style.paddingTop = el.style.paddingBottom = '0px';
      setTimeout(() => { el.remove(); res(); }, 350);
    });
  }
  async function snooze(t) {
    const ids = t.ids;
    const res = await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/snooze`, { hours: 1 })));
    if (res.every(r => !r.ok)) { F.snack(`${IC('circle-alert')}<span>Couldn't snooze — try again</span>`); return; }
    const next = isMax() && st.sel === t.key ? neighbour(t.key) : null;
    const gone = rows.filter(x => ids.includes(x.id));
    await Promise.all([...$$(`.it[data-k="${CSS.escape(t.key)}"]`), ...$$(`tr.r[data-k="${CSS.escape(t.key)}"]`)].map(collapse));
    rows = rows.filter(x => !ids.includes(x.id)); total = Math.max(0, total - ids.length);
    if (next !== null) st.sel = next;
    regroup({ still: true });
    if (isMax() && st.view === 'list') drawRead();
    const until = new Date(Date.now() + 3600e3).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    F.snack(`${IC('alarm-clock')}<span>Snoozed until <b>${until}</b> — it will pop up again</span>`, [['Undo', async () => {
      await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/unsnooze`)));
      rows = rows.concat(gone).sort((a, b) => (a.at < b.at ? 1 : -1)); total += ids.length;
      regroup({ still: true, fresh: ids });
    }]]);
  }
  // Mark all read: rows tick off one by one, then one Undo for all of them.
  async function markAll() {
    const list = isMax() ? (st.view === 'table' ? tableItems() : wideItems()) : threads.filter(t => M.matches(t, st.q) && M.inView(t, st.f));
    const todo = list.filter(t => t.unread);
    if (!todo.length) { F.snack(`${CHECK}<span>Nothing unread here</span>`); return; }
    const everything = todo.length === threads.filter(t => t.unread).length && rows.length >= total;
    const before = new Map(todo.map(t => [t, t.items.filter(x => !x.isRead).map(x => x.id)]));
    todo.forEach((t, k) => setTimeout(() => { t.items.forEach(x => x.isRead = true); t.unread = false; paintRead(t); }, rm() ? 0 : Math.min(k, 20) * 55));
    const ids = [...before.values()].flat();
    const ok = everything ? (await api('POST', 'api/notifications/v1/inbox/read-all')).ok
      : (await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/read`)))).every(r => r.ok);
    setTimeout(() => {
      if (!ok) { F.snack(`${IC('circle-alert')}<span>Some couldn't be marked — try again</span>`); load(false); return; }
      F.snack(`${CHECK}<span><b>${todo.length}</b> marked as read</span>`, [['Undo', async () => {
        before.forEach((xs, t) => { t.items.forEach(x => { if (xs.includes(x.id)) x.isRead = false; }); t.unread = true; paintRead(t); });
        await Promise.all(ids.map(id => api('POST', `api/notifications/v1/notifications/${id}/unread`)));
      }]]);
    }, rm() ? 0 : Math.min(todo.length, 20) * 55 + 120);
  }
  $$('[data-all]').forEach(b => b.onclick = markAll);

  // ── maximized: sidebar ─────────────────────────────────────────────────────
  (function initSide() {
    $('#navlist').insertAdjacentHTML('beforeend', VIEWS.map(v => `<button class="nv ${st.wf === v[0] ? 'on' : ''}" data-f="${v[0]}"><span class="sq" style="--m:${v[3]}">${IC(v[2])}</span>${v[1]}<span class="badge ${v[4]} zero" data-count="${v[0]}"></span></button>`).join('')
      + `<div class="sh">Modules</div>` + SIDE_MODS.map(k => `<button class="nv m-${k}" data-f="${k}"><span class="sq">${modIcon(k)}</span>${esc(modName(k))}<span class="badge g zero" data-count="mod:${k}"></span></button>`).join(''));
    $('#navlist').addEventListener('click', e => { const b = e.target.closest('button.nv'); if (b) setFilter(b.dataset.f); });
  })();
  function drawSide() { placeThumb($('#navlist'), true); }
  function setFilter(f, quiet) {
    st.wf = f; setOn($('#navlist'), $(`#navlist>button[data-f="${f}"]`));
    if (st.view === 'list') { drawList(quiet ? { still: true } : {}); ensureSel(); } else drawTable({});
    counts(); window.icons();
  }
  const viewName = f => (VIEWS.find(v => v[0] === f) || [f, modName(f)])[1];
  const wideItems = () => threads.filter(t => M.inView(t, st.wf) && M.matches(t, st.wq));

  // ── maximized: list ────────────────────────────────────────────────────────
  function drawList(o) {
    $('#lTitle').textContent = viewName(st.wf);
    const el = $('#wideList');
    if (failed && !rows.length) { el.innerHTML = failHtml(); return; }
    const xs = wideItems();
    let h = '', i = 0;
    const R = t => row(t, i++, Object.assign({}, o, { sel: st.sel }));
    if (st.wf === 'all') ['act', 'msg', 'info'].forEach(tr => { const g = xs.filter(t => t.tier === tr); if (g.length) h += `<div class="grp ${tr === 'act' ? 'act' : ''}">${TIER[tr][0]}<span class="ln"></span></div>` + g.map(R).join(''); });
    else h += byDay(xs, R);
    el.innerHTML = (h || emptyHtml(st.wq ? 'No matches' : 'You’re all caught up', st.wq ? 'Try another word.' : 'Nothing here right now.')) + (st.wq ? '' : moreHtml());
  }
  function ensureSel() {
    const xs = wideItems();
    if (!xs.find(t => t.key === st.sel)) { st.sel = xs[0] ? xs[0].key : null; drawRead(); }
    else if (!$('#rpane .rin') || $('#rpane').dataset.k !== st.sel) drawRead();
    markSel();
  }
  function markSel() { $$('#wideList .it').forEach(r => r.classList.toggle('sel', r.dataset.k === st.sel)); }
  function neighbour(key) {
    const ks = $$('#wideList .it').map(r => r.dataset.k), i = ks.indexOf(key);
    return ks[i + 1] || ks[i - 1] || null;
  }
  let readT;
  function select(key) {
    if (!key) return;
    const changed = st.sel !== key;
    st.sel = key; markSel();
    if (changed || !$('#rpane .rin')) drawRead();
    const r = $(`#wideList .it[data-k="${CSS.escape(key)}"]`);
    if (r) r.scrollIntoView({ block: 'nearest', behavior: rm() ? 'auto' : 'smooth' });
    // Shown ~1 s = read (as in Mail).
    clearTimeout(readT);
    const t = byKey(key);
    if (t && t.unread) readT = setTimeout(() => { const n = byKey(key); if (st.sel === key && n && n.unread) setRead(n, true, true); }, 1100);
  }
  $('#wq').addEventListener('input', e => { st.wq = e.target.value.trim(); $('#tq').value = e.target.value; drawList({ still: true }); ensureSel(); counts(); window.icons(); });
  $('#tq').addEventListener('input', e => { st.wq = e.target.value.trim(); $('#wq').value = e.target.value; drawTable({ still: true }); counts(); window.icons(); });

  // ── maximized: reading pane ────────────────────────────────────────────────
  let readSeq = 0;
  const avatar = (name, cls) => `<div class="avatar ${cls || ''}" style="background:${avatarColor(name)}">${esc(initials(name))}</div>`;
  const personOf = id => {
    if (!id) return null;
    const l = String(id).toLowerCase();
    return (ctx.leaders || []).find(x => String(x.id).toLowerCase() === l) || (ctx.vips || []).find(x => String(x.id).toLowerCase() === l) || null;
  };
  const idIn = (re, s) => { const m = new RegExp(re).exec(s || ''); return m ? m[1] : null; };
  function drawRead() {
    const rp = $('#rpane'), t = byKey(st.sel), seq = ++readSeq;
    rp.dataset.k = st.sel || '';
    if (!t) { rp.innerHTML = `<div class="rscroll">${emptyHtml('Nothing selected', 'Pick a notification on the left to read it here.')}</div>`; window.icons(); return; }
    const h = t.head, line = t.line;
    let k = 0; const I = () => `style="--i:${k++}"`;
    const p = personOf(h.actorId);
    const who = h.actorName || line.who || (t.mod === 'chat' ? h.title : null);
    const rel = t.leader ? `<span class="chip lead rel">${IC(t.leader === 'VIP' ? 'star' : 'crown')}${esc(t.leader === 'VIP' ? 'Your VIP' : 'Your leader · ' + t.leader)}</span>`
      : t.approval ? `<span class="chip appr rel">${IC('stamp')}Waiting for you</span>` : '';
    let html = `<div class="rscroll m-${t.mod}"><div class="rin">`
      + `<div class="rmeta" ${I()}><div class="ico sm">${modIcon(t.mod)}</div><span class="mn">${esc(modName(t.mod))}</span>${line.tag ? `<span class="chip mod">${esc(line.tag)}</span>` : ''}`
      + `<span class="chip" style="background:color-mix(in srgb,${TIER[t.tier][1]} 14%,transparent);color:${TIER[t.tier][1]}">${TIER[t.tier][0]}</span><span class="when">${esc(M.dayLabel(h.at))} · ${esc(M.clock(h.at))}</span></div>`
      + `<div class="rtitle sel" ${I()}>${esc(h.title)}</div>`
      + (t.approval ? `<div class="duebar" ${I()}>${IC('alarm-clock')}Waiting for your approval<span>Open it to approve or reject</span></div>` : '')
      + (who ? `<div class="person" id="rperson" ${I()}>${avatar(who)}<div><div class="nm">${esc(who)}</div><div class="rl">${esc(p && p.department || line.whoSub || modName(t.mod))}</div></div>${rel}</div>` : '')
      + `<div id="rdetail" ${I()}><div class="rbody"><span class="skel" style="display:block;height:14px;width:80%;margin:6px 0 12px"></span><span class="skel" style="display:block;height:14px;width:65%"></span></div></div>`
      + `</div></div><div class="ract m-${t.mod}" id="ract"></div>`;
    rp.innerHTML = html;
    rp.classList.remove('enter'); reflow(rp); rp.classList.add('enter');
    window.icons();
    detail(t).then(d => { if (seq === readSeq) fillDetail(t, d); });
  }

  // What the reading pane shows below the person, per module. Never raw mail HTML in this page: sandboxed iframe.
  async function detail(t) {
    const h = t.head, u = h.targetUrl || '';
    const d = { body: '', conv: null, files: [], reply: null, primary: null };
    if (t.mod === 'mail') {
      const mid = idIn('mailId=(' + G + ')', u);
      const r = mid && await api('GET', `api/mail/${mid}`);
      if (r && r.ok) {
        const m = r.json;
        d.mail = m;
        d.to = (m.recipients || []).filter(x => x.recipientType === 'to').map(x => x.name);
        d.files = (m.attachments || []).map(a => ({ name: a.fileName, path: `api/mail/attachments/${a.id}/download`, size: a.fileSize, from: 'Mail · ' + (m.senderName || ''),
          thumb: (a.mimeType || '').startsWith('image/') ? `api/mail/attachments/${a.id}/download?inline=true` : null }));
      }
      d.primary = { label: 'Reply', icon: 'reply', run: () => post({ type: 'openNotice', notice: plain(h) }) };
    } else if (t.mod === 'chat') {
      const room = idIn('^/chat/(' + G + ')', u);
      if (room) {
        const r = await api('GET', `api/chat/rooms/${room}/messages?page=1&pageSize=20`);
        const items = ((r.ok && (r.json?.items || r.json?.Items)) || []).filter(x => !x.isDeleted && x.type !== 'system');
        items.sort((a, b) => (a.createdAt < b.createdAt ? -1 : 1));
        d.conv = items.map(x => ({ name: x.senderName, me: !!ctx.nik && x.senderNik === ctx.nik, text: app.plainChat(x.content), at: x.createdAt, atts: (x.attachments || []).map(a => a.fileName) }));
        d.files = items.flatMap(x => (x.attachments || []).map(a => ({ name: a.fileName, path: `api/chat/attachments/${a.id}/download`, size: a.fileSize, from: 'Chat · ' + (x.senderName || ''),
          thumb: (a.mimeType || a.contentType || '').startsWith('image/') ? `api/chat/attachments/${a.id}/thumbnail` : null }))).reverse();
        d.reply = {
          to: (items.filter(x => x.senderNik !== ctx.nik).slice(-1)[0] || {}).senderName || h.title,
          send: async text => {
            const res = await api('POST', `api/chat/rooms/${room}/messages`, { content: text, idempotencyKey: crypto.randomUUID() });
            if (res.ok) api('POST', 'api/notifications/v1/read-by-target', { kind: 'chat', id: room });
            return res.ok ? null : (res.json?.message || "Couldn't send — try again");
          },
        };
        d.open = 'Open chat';
      }
    } else if (t.mod === 'tickets') {
      const own = idIn('^/tickets/(' + G + ')', u), agent = idIn('^/idesk/tickets/(' + G + ')', u);
      const ref = own || agent;
      if (own) {
        const [r, m] = await Promise.all([api('GET', `api/tickets/${own}`), api('GET', `api/tickets/${own}/messages?peek=1`)]);
        if (r.ok) d.ticket = r.json;
        const msgs = (m.ok && m.json?.messages) || [];
        if (msgs.length) d.conv = msgs.slice(-20).map(x => ({ name: x.mine ? 'You' : x.sender || 'ICT', me: x.mine, text: x.content, at: x.createdAt, atts: x.attachments || [] }));
      }
      if (ref) d.reply = {
        to: t.line.who || 'the ticket chat',
        send: async text => { const res = await api('POST', `api/tickets/${ref}/reply`, { content: text }); return res.ok ? null : (res.json?.message || "Couldn't send — open the ticket in iDesk to try again"); },
      };
      d.open = 'Open in iDesk';
    }
    return d;
  }
  function fillDetail(t, d) {
    const box = $('#rdetail'); if (!box) return;
    const h = t.head;
    let html = '';
    if (d.mail) {
      const m = d.mail;
      const rl = $('#rperson .rl'); if (rl && d.to.length) rl.textContent = 'to ' + d.to.slice(0, 3).join('; ') + (d.to.length > 3 ? ` +${d.to.length - 3}` : '');
      html += `<div class="rbody"><iframe data-mail sandbox="allow-same-origin" title="Message"></iframe></div>`;
      d.mailHtml = m.isHtml ? m.body : `<p style="white-space:pre-wrap">${esc(m.bodyText || m.body)}</p>`;
      if (m.signature) d.mailHtml += `<div style="margin-top:12px;color:#6e6e73">${m.signature}</div>`;
    } else if (d.ticket && !d.conv) {
      html += `<div class="rbody sel"><p>${esc(d.ticket.description || t.line.text)}</p></div>`;
    } else if (!d.conv) {
      html += `<div class="rbody sel">${String(h.body || '').split(/\n{2,}/).map(p => `<p>${esc(p)}</p>`).join('')}</div>`;
    }
    if (d.conv && d.conv.length) html += `<div class="sect">${IC('messages-square')}Conversation · <span id="hcount">${d.conv.length}</span> messages</div><div class="hist" id="hist">${d.conv.map(bubble).join('')}</div>`;
    else if (d.conv) html += `<div class="rbody sel"><p>${esc(t.line.text)}</p></div><div class="hist" id="hist"></div>`;
    if (t.items.length > 1 && !d.conv) html += `<div class="sect">${IC('history')}Updates · ${t.items.length}</div><div class="upd">${t.items.map(x => `<div><small>${esc(timeShort(x.at))}</small>${esc(x.title)} — ${esc(M.lineOf(x).text)}</div>`).join('')}</div>`;
    if (d.files.length) html += `<div class="sect">${IC('paperclip')}Files · ${d.files.length}${d.files.length > 1 ? `<button class="lnk" data-dlall>${IC('download')}Download all</button>` : ''}</div><div class="files">${d.files.map(F.card).join('')}</div>`;
    box.innerHTML = html;
    const frame = box.querySelector('iframe[data-mail]'); if (frame) mailFrame(frame, d.mailHtml);
    F.thumbs(box);
    box.onclick = e => {
      const p = e.target.closest('[data-fp]'); if (p) return F.preview(d.files, +p.dataset.fp);
      const dl = e.target.closest('[data-fd]'); if (dl) return F.download(d.files[+dl.dataset.fd], dl, { labeled: true });
      if (e.target.closest('[data-dlall]')) d.files.forEach((f, i) => setTimeout(() => F.download(f, box.querySelector(`[data-fd="${i}"]`), { labeled: true, quiet: i > 0 }), i * 160));
    };
    actions(t, d);
    window.icons();
    const sc = $('#rpane .rscroll');
    if (d.conv && sc) requestAnimationFrame(() => { sc.scrollTop = sc.scrollHeight; });
  }
  function bubble(x, cls) {
    const atts = (x.atts || []).map(a => `<span class="att">${IC('paperclip')}${esc(a)}</span>`).join('');
    return `<div class="msg ${x.me ? 'me' : ''} ${cls || ''}">${avatar(x.me ? (ctx.name || 'You') : x.name)}<div class="bub"><b>${esc(x.me ? 'You' : x.name)}${x.at ? ' · ' + esc(timeShort(x.at)) : ''}</b>${esc(x.text || '')}${atts}</div></div>`;
  }
  // Action bar: inline reply for chat + iDesk, Reply (popup) for mail, Open, Mark read/unread, Snooze.
  function actions(t, d) {
    const bar = $('#ract'); if (!bar) return;
    const h = t.head;
    const openLabel = d.open || 'Open in iPop';
    const readBtn = `<button class="btn" data-ra="read">${IC(t.unread ? 'check' : 'mail')}${t.unread ? 'Mark as read' : 'Mark as unread'}</button>`;
    const snz = `<button class="btn" data-ra="snooze" title="Snooze 1 hour">${IC('alarm-clock')}Snooze</button>`;
    let html;
    if (d.reply) html = `<div class="composer"><input id="rinp" placeholder="Reply to ${esc(String(d.reply.to).split(' ')[0])}…" aria-label="Reply"><button class="btn p" id="rsend" disabled>${IC('send')}Send</button></div><button class="btn" data-ra="open">${IC('arrow-up-right')}${esc(openLabel)}</button>${readBtn}${snz}`;
    else if (d.primary) html = `<button class="btn p" data-ra="primary">${IC(d.primary.icon)}${esc(d.primary.label)}</button><button class="btn" data-ra="open">${IC('arrow-up-right')}${esc(openLabel)}</button><span class="sp"></span>${readBtn}${snz}`;
    else html = `<button class="btn p" data-ra="open">${IC(t.approval ? 'external-link' : 'arrow-up-right')}${esc(t.approval ? 'Open to approve' : openLabel)}</button><span class="sp"></span>${readBtn}${snz}`;
    bar.innerHTML = html;
    bar.onclick = e => {
      const b = e.target.closest('[data-ra]'); if (!b) return;
      const a = b.dataset.ra;
      if (a === 'read') return toggleRead(byKey(t.key) || t);
      if (a === 'snooze') return snooze(byKey(t.key) || t);
      if (a === 'primary') return d.primary.run();
      if (a === 'open') {
        const o = b.innerHTML; b.classList.add('loading'); b.innerHTML = '<span class="spin"></span>';
        post({ type: 'openIPop', path: h.targetUrl || '/notifications' });
        if (t.unread) setRead(t, true, true);
        setTimeout(() => { b.innerHTML = o; b.classList.remove('loading'); window.icons(); }, rm() ? 0 : 650);
      }
    };
    const inp = $('#rinp');
    if (inp) {
      const send = $('#rsend');
      const go = async () => {
        const text = inp.value.trim(); if (!text || send.classList.contains('loading')) return;
        send.classList.add('loading'); const o = send.innerHTML; send.innerHTML = '<span class="spin"></span>';
        const fail = await d.reply.send(text);
        send.classList.remove('loading'); send.innerHTML = o; window.icons();
        if (fail) { F.snack(`${IC('circle-alert')}<span>${esc(fail)}</span>`); return; }
        inp.value = ''; send.disabled = true;
        const hist = $('#hist');
        if (hist) {
          hist.insertAdjacentHTML('beforeend', bubble({ me: true, text, at: new Date().toISOString() }, 'in'));
          const c = $('#hcount'); if (c) c.textContent = hist.children.length;
          const sc = $('#rpane .rscroll'); sc.scrollTo({ top: sc.scrollHeight, behavior: rm() ? 'auto' : 'smooth' });
        }
        const n = byKey(t.key) || t; if (n.unread) setRead(n, true, true);
        F.snack(`${CHECK}<span>Sent to <b>${esc(String(d.reply.to).split(' ')[0])}</b></span>`);
        window.icons();
      };
      inp.oninput = () => send.disabled = !inp.value.trim();
      inp.onkeydown = e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); go(); } if (e.key === 'Escape') { e.stopPropagation(); inp.blur(); } };
      send.onclick = go;
    }
    window.icons();
  }

  // Mail HTML: isolated (no scripts, no remote loads), as the popups do it (popup.js mailFrame).
  function mailFrame(frame, html) {
    const dark = document.documentElement.classList.contains('dark'), large = document.documentElement.classList.contains('large');
    frame.srcdoc = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'"><base target="_blank"><style>
      html,body{margin:0;padding:0;background:transparent;color:${dark ? '#d1d1d6' : '#3c3c43'};font:${large ? 19 : 16.5}px/1.65 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;word-break:break-word;overflow:hidden}
      p{margin:0 0 12px} img{max-width:100%;height:auto;border-radius:8px} a{color:${dark ? '#5ee08a' : '#1c7a44'}} table{max-width:100%}
      blockquote{margin:8px 0;padding-left:10px;border-left:3px solid rgba(120,120,128,.35);color:#8e8e93}
      </style></head><body>${html || ''}</body></html>`;
    frame.onload = async () => {
      const doc = frame.contentDocument; if (!doc) return;
      const fit = () => { frame.style.height = doc.documentElement.scrollHeight + 'px'; };
      for (const img of doc.querySelectorAll('img')) {
        const src = img.getAttribute('src') || '';
        const m = src.match(/\/?(api\/attachments\/[0-9a-fA-F-]{36})/);
        if (m) { const url = await app.image(m[1]); if (url) img.src = url; else img.remove(); }
        else if (!src.startsWith('data:')) img.remove();
        img.onload = fit;
      }
      doc.addEventListener('click', e => {
        const a = e.target.closest('a'); if (!a) return;
        e.preventDefault();
        const href = a.getAttribute('href') || '';
        if (/^https?:|^mailto:/i.test(href)) post({ type: 'openUrl', url: href });
        else if (href.startsWith('/')) post({ type: 'openIPop', path: href });
      });
      fit(); setTimeout(fit, 300);
    };
  }

  // ── maximized: table ───────────────────────────────────────────────────────
  const tableItems = () => {
    const scope = ['act', 'msg', 'info', 'all'].includes(st.wf) ? 'all' : st.wf;
    let xs = threads.filter(t => M.inView(t, scope) && M.matches(t, st.wq));
    if (st.tsort === 'asc') xs = [...xs].reverse();
    return xs;
  };
  function drawTable(o) {
    o = o || {};
    const xs = tableItems();
    let h = `<thead><tr><th style="width:26px"></th><th>Priority</th><th>From</th><th style="width:42%">Subject</th><th>Module</th><th><span class="s ${st.tsort === 'asc' ? 'asc' : ''}" id="tsort" title="Sort by time">Time ${IC('arrow-down')}</span></th><th>Status</th><th></th></tr></thead><tbody>`;
    ['act', 'msg', 'info'].forEach(tr => {
      const g = xs.filter(t => t.tier === tr); if (!g.length) return;
      h += `<tr class="g ${tr === 'act' ? 'act' : ''} ${st.col[tr] ? 'col' : ''}" data-g="${tr}"><td colspan="8"><span class="gi"><i data-lucide="chevron-down" class="chev"></i><span class="tierdot" style="background:${TIER[tr][1]}"></span>${TIER[tr][0]} <span class="badge g">${g.length}</span></span></td></tr>`;
      if (st.col[tr]) return;
      h += g.map(t => {
        const person = t.head.actorName || t.line.who || (t.mod === 'chat' ? t.head.title : null);
        // No person behind it (reminders, system, news): the module stands in, never made-up initials.
        const from = person ? `${avatar(person)}${esc(person)}` : `<span class="mc m-${t.mod}"><span class="sq">${modIcon(t.mod)}</span>${esc(modName(t.mod))}</span>`;
        const pr = t.approval ? 'Approval' : t.leader ? (t.leader === 'VIP' ? 'VIP' : 'Leader') : TIER[tr][2];
        const fresh = o.fresh && t.ids.some(id => o.fresh.includes(id));
        return `<tr class="r ${t.unread ? 'unread' : ''} ${fresh ? 'new' : ''}" data-k="${esc(t.key)}"><td>${t.unread ? '<span class="dot"></span>' : ''}</td>
          <td><span class="pr" style="color:${TIER[tr][1]}"><i style="background:${TIER[tr][1]}"></i>${pr}</span></td>
          <td><span class="from">${from}</span></td>
          <td class="subj">${esc(t.head.title)}${t.items.length > 1 ? ` <span class="chip thr">${t.items.length}</span>` : ''}<span style="color:var(--ink-3);font-weight:400"> — ${esc(t.line.text)}</span></td>
          <td><span class="mc m-${t.mod}"><span class="sq">${modIcon(t.mod)}</span>${esc(modName(t.mod))}</span></td>
          <td style="color:var(--ink-3)">${esc(M.relTime(t.at))}</td><td><span class="st ${t.unread ? 'u' : ''}">${t.unread ? 'Unread' : 'Read'}</span></td>
          <td class="qa2"><button data-a="read" title="${t.unread ? 'Mark as read' : 'Mark as unread'}">${IC(t.unread ? 'check' : 'mail')}</button><button data-a="snooze" title="Snooze 1 hour">${IC('alarm-clock')}</button></td></tr>`;
      }).join('');
    });
    if (!xs.length) h += `<tr><td colspan="8" style="border:0">${emptyHtml(st.wq ? 'No matches' : 'You’re all caught up', st.wq ? 'Try another word.' : 'Nothing here right now.')}</td></tr>`;
    else if (rows.length < total && !st.wq) h += `<tr><td colspan="8" style="border:0">${moreHtml()}</td></tr>`;
    $('#ntable').innerHTML = h + '</tbody>';
    window.icons();
  }
  $('#ntable').addEventListener('click', e => {
    if (e.target.closest('[data-more]')) { load(true); return; }
    if (e.target.closest('#tsort')) { st.tsort = st.tsort === 'desc' ? 'asc' : 'desc'; drawTable(); return; }
    const g = e.target.closest('tr.g'); if (g) { st.col[g.dataset.g] = !st.col[g.dataset.g]; drawTable(); return; }
    const r = e.target.closest('tr.r'); if (!r) return;
    const t = byKey(r.dataset.k); if (!t) return;
    const a = e.target.closest('[data-a]');
    if (a) { if (a.dataset.a === 'read') toggleRead(t); else snooze(t); return; }
    // Row → back to the list with that one open.
    if (!M.inView(t, st.wf)) st.wf = t.tier;
    st.sel = t.key;
    setView('list');
    setFilter(st.wf, true);
    select(t.key);
  });
  function setView(v) {
    st.view = v;
    $('#wgrid').classList.toggle('table', v === 'table');
    $('#lpane').hidden = v !== 'list'; $('#rpane').hidden = v !== 'list'; $('#tpane').hidden = v !== 'table';
    $$('[data-seg]').forEach(s => { s.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.v === v)); placeThumb(s, true); });
    if (v === 'list') { const lp = $('#lpane'); lp.classList.remove('enter'); reflow(lp); lp.classList.add('enter'); drawList({ still: true }); ensureSel(); }
    else drawTable({});
    counts(); window.icons();
    requestAnimationFrame(() => $$('[data-seg]').forEach(s => placeThumb(s)));
  }
  $$('[data-seg]').forEach(s => s.addEventListener('click', e => {
    const b = e.target.closest('button'); if (!b || b.classList.contains('on')) return;
    setOn(s, b); setTimeout(() => setView(b.dataset.v), rm() ? 0 : 180);
  }));

  // ── keyboard (maximized list): ↑↓ / j k move, E read/unread, S snooze ─────
  document.addEventListener('keydown', e => {
    if (!isMax() || st.view !== 'list' || e.target.matches('input,textarea') || e.ctrlKey || e.altKey || e.metaKey) return;
    const ks = $$('#wideList .it').map(r => r.dataset.k), i = ks.indexOf(st.sel);
    if (e.key === 'ArrowDown' || e.key === 'j') { select(ks[Math.min(ks.length - 1, i + 1)]); e.preventDefault(); }
    else if (e.key === 'ArrowUp' || e.key === 'k') { select(ks[Math.max(0, i - 1)]); e.preventDefault(); }
    else if ((e.key === 'e' || e.key === 'E') && byKey(st.sel)) toggleRead(byKey(st.sel));
    else if ((e.key === 's' || e.key === 'S') && byKey(st.sel)) snooze(byKey(st.sel));
  });

  requestAnimationFrame(() => placeAll(true));
  if (document.fonts) document.fonts.ready.then(() => placeAll(true));
})();
