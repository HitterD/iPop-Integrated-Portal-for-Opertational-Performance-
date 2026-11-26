(function () {
  const { esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let S = null, saveT;
  document.querySelector('[data-min]').innerHTML = ICON.min;
  document.querySelector('[data-max]').innerHTML = ICON.max;
  document.querySelector('[data-close]').innerHTML = ICON.x;
  document.querySelector('[data-min]').onclick = () => post({ type: 'minimize' });
  document.querySelector('[data-close]').onclick = () => post({ type: 'close' });

  $('#nav').onclick = e => {
    const a = e.target.closest('a'); if (!a) return;
    document.querySelectorAll('#nav a').forEach(x => x.classList.toggle('on', x === a));
    document.querySelectorAll('.main section').forEach(s => s.classList.toggle('on', s.id === 's-' + a.dataset.s));
    thumb($('#nav'));
    if (a.dataset.s === 'categories') requestAnimationFrame(() => document.querySelectorAll('#modrows .tseg').forEach(sg => thumb(sg, true)));
  };
  const SOUNDS = [['mail', 'Mail · Envelope'], ['tickets', 'iDesk · Lifeline'], ['meeting', 'Meeting · Doorbell'], ['dm', 'Chat · Bubble'], ['group', 'Group chat · Pebble'],
    ['mention', 'Mention · Spark'], ['project', 'Projects · Tasker'], ['announce', 'News · Herald'], ['notes', 'Notes · Paper'], ['system', 'System · Ping']];

  // pc = this PC's own settings (Start with Windows), never synced to iPop.
  let PC = {};
  app.on('pc', m => { PC = m.pc || {}; drawPc(); });
  app.on('settings', m => { S = m.settings; PC = m.pc || {}; draw(m.account); loadLeaders(); requestAnimationFrame(() => thumb($('#nav'), true)); });
  app.on('saved', m => {
    S = m.settings;
    const el = $('#saved');
    el.querySelector('span').textContent = m.ok ? 'Saved · synced to iPop' : 'Saved on this PC — will sync when iPop is reachable';
    el.classList.toggle('warn', !m.ok);
    el.classList.add('on'); clearTimeout(saveT); saveT = setTimeout(() => el.classList.remove('on'), m.ok ? 1600 : 3200);
    // The app reloads leaders + VIP names after every save (NotifierApp settings.Changed); a VIP change shows them here.
    if (vipsChanged) { vipsChanged = false; setTimeout(loadLeaders, 600); }
  });
  // General › About → Check for updates (1.4.2): the answer shows in the row; "Update now" opens the usual update window.
  let checking = false;
  const UPD = {
    notInstalled: () => 'Updates work in the installed app only',
    upToDate: () => 'Up to date',
    ready: r => 'Version ' + r.version + ' is ready',
    failed: () => 'Couldn’t check right now',
  };
  $('#updCheck').onclick = () => {
    if (checking) return;
    checking = true;
    $('#updCheck').disabled = true; $('#updNow').hidden = true;
    $('#updS').textContent = 'Checking…';
    post({ type: 'checkUpdates' });
  };
  $('#updNow').onclick = () => post({ type: 'updateNow' });
  app.on('updateResult', m => {
    checking = false;
    const r = m.result || { state: 'failed' };
    $('#updCheck').disabled = false;
    $('#updS').textContent = (UPD[r.state] || UPD.failed)(r);
    $('#updNow').hidden = r.state !== 'ready';
  });
  app.ready();

  function draw(acc) {
    document.querySelectorAll('.sty').forEach(b => b.classList.toggle('on', b.dataset.style === S.style));
    const pop = S.popups === 'on' ? 'on' : S.popups === 'off' ? 'off' : (S.offUntil && new Date(S.offUntil) - Date.now() > 2 * 3600e3 ? 'tomorrow' : '1h');
    pill('#popups', pop); pill('#meet', S.duringMeetings); pill('#pos', S.position);
    $('#urgent').classList.toggle('on', S.urgentBreaksThrough);
    $('#vol').value = S.volume; $('#sound').classList.toggle('on', S.sound);
    drawPc();
    pill('#textsize', S.textSize || 'normal');
    pill('#anim', S.animations || 'on');
    document.querySelectorAll('#theme .th').forEach(b => b.classList.toggle('on', b.dataset.t === (S.theme || 'system')));
    drawCategories();
    $('#previews').innerHTML = SOUNDS.map(([k, n]) => `<div class="row"><span class="t">${esc(n)}</span><button class="btn" data-snd="${k}">${IC('play')}Play</button></div>`).join('');
    if (acc) {
      $('#devRow').hidden = !acc.ict;
      $('#who').innerHTML = `Signed in as ${esc(acc.name || '…')}<small>Stays signed in — renews itself; ends after 90 days unused</small>`;
      $('#updT').firstChild.textContent = 'iPop Notifier ' + acc.version;
      $('#about').innerHTML = `<span>Version</span><span>${esc(acc.version)}</span><span>Computer</span><span>${esc(acc.computer)}</span><span>Server</span><span>${esc(acc.server)}</span>`;
    }
  }
  function drawPc() {
    $('#autostart').classList.toggle('on', PC.startWithWindows !== false);
    $('#openipop').classList.toggle('on', PC.openIPopAtLogin !== false);
  }
  function pill(sel, v) { const p = typeof sel === 'string' ? $(sel) : sel; p.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.v === v)); }

  function save(patch) {
    S = Object.assign({}, S, patch);
    draw();
    post({ type: 'save', settings: S });
  }

  document.addEventListener('click', e => {
    const sty = e.target.closest('.sty'); if (sty) return save({ style: sty.dataset.style });
    // Appearance: the app applies it to every open Notifier window as soon as it is saved.
    const th = e.target.closest('#theme .th'); if (th) return save({ theme: th.dataset.t });
    const b = e.target.closest('.pill3 button'); if (b && !b.disabled) {
      const p = b.parentElement, v = b.dataset.v;
      if (p.id === 'popups') {
        if (v === 'on' || v === 'off') return save({ popups: v, offUntil: null });
        const until = v === '1h' ? new Date(Date.now() + 3600e3) : (() => { const d = new Date(); d.setDate(d.getDate() + 1); d.setHours(7, 0, 0, 0); return d; })();
        return save({ popups: 'until', offUntil: until.toISOString() });
      }
      if (p.id === 'meet') return save({ duringMeetings: v });
      if (p.id === 'pos') return save({ position: v });
      if (p.id === 'textsize') { document.documentElement.classList.toggle('large', v === 'large'); return save({ textSize: v }); }
      // The app sends it to every open window (theme message); this window follows at once.
      if (p.id === 'anim') { document.documentElement.classList.toggle('reduce-motion', v === 'reduced'); return save({ animations: v }); }
    }
    const sw = e.target.closest('.sw'); if (sw) {
      const on = !sw.classList.contains('on');
      if (sw.id === 'urgent' || sw.id === 'breakthrough') return save({ urgentBreaksThrough: on });
      if (sw.id === 'hidemsg') return save({ hideMessageToasts: on });
      if (sw.id === 'sound') return save({ sound: on });
      if (sw.id === 'openipop') { PC.openIPopAtLogin = on; drawPc(); return post({ type: 'pc', openIPopAtLogin: on }); }
      if (sw.id === 'autostart') { PC.startWithWindows = on; drawPc(); return post({ type: 'pc', startWithWindows: on }); }
    }
    const snd = e.target.closest('[data-snd]'); if (snd) post({ type: 'preview', sound: snd.dataset.snd, volume: +$('#vol').value });
  });
  $('#vol').onchange = () => { save({ volume: +$('#vol').value }); post({ type: 'preview', sound: 'system', volume: +$('#vol').value }); };
  $('#devices').onclick = () => post({ type: 'devices' });
  $('#signout').onclick = () => { if (confirm('Sign out of iPop Notifier on this PC?')) post({ type: 'signOut' }); };

  // ── Settings › Categories (1.5.0, docs/mockups/notifier/priority.html screen 6) ───────────────────────────
  // Every change saves at once through the app (save → saved), as every other setting here.
  const rm = () => window.reducedMotion(); // Settings › Animations "Reduced" (html.reduce-motion)
  const reflow = el => void el.offsetWidth;
  const T = window.tiers;
  // [kind, module (icon/colour), label, hint] — kinds and labels: docs/notifier/PRIORITY-150.md "Kinds".
  const KINDROWS = [
    ['approval', 'approval', 'Approvals', 'SPP and other approval requests'],
    ['chat', 'chat', 'Chat', 'From colleagues · leaders and VIPs are always Needs action'],
    ['mail', 'mail', 'Mail', 'Mail to you'],
    ['mailmass', 'mail', 'Mass mail & “All staff”', 'Broadcasts to many people'],
    ['ticketassigned', 'tickets', 'iDesk — assigned to me / reopened', ''],
    ['ticketchat', 'tickets', 'iDesk — replies', 'Ticket chat messages'],
    ['tickets', 'tickets', 'iDesk — status updates', 'Resolved, closed, visit changed…'],
    ['meetingsoon', 'calendar', 'Meeting starting soon', 'A few minutes before'],
    ['calendar', 'calendar', 'Invites & changes', ''],
    ['projects', 'projects', 'Comments, mentions & tasks for me', ''],
    ['projectwatch', 'projects', 'Project updates you watch', ''],
    ['notes', 'notes', 'Notes mentions', ''],
    ['calls', 'calls', 'Calls', 'Missed calls'],
    ['news', 'news', 'News & announcements', 'Mandatory memos stay Needs action'],
    ['system', 'system', 'System notices', 'EPop import, updates'],
  ];
  const TL = { act: 'Needs action', msg: 'Message', info: 'Info' };
  const TC = { act: 'var(--act)', msg: 'var(--unread)', info: 'var(--ink-4)' };
  const MODICON = { approval: 'stamp', chat: 'message-circle', mail: 'mail', calendar: 'calendar', projects: 'kanban', notes: 'notebook-pen', calls: 'phone', news: 'newspaper', system: 'bell' };
  const modIcon = m => m === 'tickets' ? '<img src="idesk-mark.png" alt="">' : IC(MODICON[m]);
  const tierOf = k => { const v = S.tiers && S.tiers[k]; return v === 'act' || v === 'msg' || v === 'info' ? v : T.defaultTier(k); };
  let leaders = [], vipPeople = [], vipsChanged = false;

  function thumb(c, instant) {
    const t = c.querySelector(':scope>.thumb'), b = c.querySelector(':scope>.on');
    if (!t || !b || !c.offsetParent) return;
    if (instant || rm()) t.style.transition = 'none';
    t.style.width = b.offsetWidth + 'px'; t.style.height = b.offsetHeight + 'px'; t.style.transform = `translate(${b.offsetLeft}px,${b.offsetTop}px)`;
    if (instant || rm()) { reflow(t); t.style.transition = ''; }
  }
  addEventListener('resize', () => document.querySelectorAll('.slider').forEach(c => thumb(c, true)));
  function setNum(el, v) { const s = String(v); if (el.textContent !== s) { el.textContent = s; el.classList.remove('tick'); reflow(el); if (v > 0) el.classList.add('tick'); } }

  function drawCategories() {
    if (!S) return;
    const box = $('#modrows');
    // Rows are drawn once; later changes move the thumb instead of redrawing (so it can slide).
    const first = !box.children.length;
    if (first) {
      box.innerHTML = KINDROWS.map(([k, m, n, hint]) => `<div class="srow m-${m}"><div class="ico">${modIcon(m)}</div><div class="tx"><div class="t1">${esc(n)}</div>${hint ? `<div class="t2">${esc(hint)}</div>` : ''}</div>`
        + `<div class="tseg slider" data-k="${k}" role="radiogroup" aria-label="${esc(n)}"><span class="thumb"></span>${['act', 'msg', 'info'].map(t => `<button data-t="${t}" role="radio"><i style="background:${TC[t]}"></i>${TL[t]}</button>`).join('')}</div></div>`).join('');
    }
    box.querySelectorAll('.tseg').forEach(sg => paintSeg(sg, tierOf(sg.dataset.k)));
    requestAnimationFrame(() => box.querySelectorAll('.tseg').forEach(sg => thumb(sg, first)));
    kinds();
    $('#breakthrough').classList.toggle('on', !!S.urgentBreaksThrough);
    $('#hidemsg').classList.toggle('on', !!S.hideMessageToasts);
    drawVips();
  }
  function paintSeg(sg, t) {
    sg.className = `tseg slider t-${t}`;
    sg.querySelectorAll('button').forEach(b => { const on = b.dataset.t === t; b.classList.toggle('on', on); b.setAttribute('aria-checked', on ? 'true' : 'false'); });
  }
  function kinds() { ['act', 'msg', 'info'].forEach(t => setNum($(`[data-kinds="${t}"]`), KINDROWS.filter(r => tierOf(r[0]) === t).length)); }

  $('#modrows').addEventListener('click', e => {
    const b = e.target.closest('.tseg button'); if (!b || b.classList.contains('on')) return;
    const sg = b.parentElement;
    paintSeg(sg, b.dataset.t); thumb(sg);
    save({ tiers: Object.assign({}, S.tiers, { [sg.dataset.k]: b.dataset.t }) });
  });
  $('#reset').onclick = () => {
    const next = {}; let n = 0;
    KINDROWS.forEach(([k]) => { next[k] = T.defaultTier(k); });
    document.querySelectorAll('#modrows .tseg').forEach(sg => {
      const d = next[sg.dataset.k];
      if (tierOf(sg.dataset.k) !== d) { n++; setTimeout(() => { paintSeg(sg, d); thumb(sg); }, rm() ? 0 : n * 60); }
    });
    if (!n) { flash('Already the defaults'); return; }
    S = Object.assign({}, S, { tiers: next });
    kinds();
    post({ type: 'save', settings: S });
  };
  function flash(text) {
    const el = $('#saved'); el.querySelector('span').textContent = text; el.classList.remove('warn');
    el.classList.add('on'); clearTimeout(saveT); saveT = setTimeout(() => el.classList.remove('on'), 1600);
  }

  // Leaders come from the org chart (GET api/notifier/leaders); VIPs are the settings' ids, named by the same answer.
  async function loadLeaders() {
    const r = await app.api('GET', 'api/notifier/leaders');
    if (!r.ok) { $('#leaders').innerHTML = `<div class="srow"><span class="none">Couldn't load your leaders right now.</span></div>`; return; }
    leaders = r.json.leaders || [];
    vipPeople = r.json.vips || [];
    drawLeaders(); drawVips();
  }
  const avatar = (name, bg) => `<div class="avatar" style="background:${bg || avatarColor(name)}">${esc(initials(name))}</div>`;
  const same = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
  function drawLeaders() {
    const el = $('#leaders');
    if (!leaders.length) { el.innerHTML = `<div class="srow"><span class="none">No leaders found in the org chart yet. Add people as VIPs below.</span></div>`; window.icons(); return; }
    // The approver chain one by one; the BOD / GM level folded into one group row when it is long. The group opens
    // (click, Enter or Space) into the full list, A–Z, and closes with "Show less" (above or below the list).
    const bod = leaders.filter(l => /BOD|GM level/i.test(l.reason || '')), chain = leaders.filter(l => !bod.includes(l));
    const one = l => `<div class="srow"><div class="pp">${avatar(l.name, '#A16207')}<div><div class="t1">${esc(l.name)}</div><div class="t2">${esc(l.department || '')}</div></div></div><span style="flex:1"></span><span class="auto">${IC('git-fork')}${esc(l.reason || 'Leader')} · org chart</span></div>`;
    let h = chain.map(one).join('');
    if (bod.length > 3) {
      const az = [...bod].sort((a, b) => a.name.localeCompare(b.name, 'en', { sensitivity: 'base' }));
      const member = l => `<div class="srow bodm"><div class="pp">${avatar(l.name)}<div><div class="t1">${esc(l.name)}</div><div class="t2">${esc(l.department || '')}</div></div></div><span style="flex:1"></span><span class="auto">${IC('building-2')}BOD / GM level</span></div>`;
      h += `<div class="srow bodg" id="bodg" role="button" tabindex="0" aria-expanded="${bodOpen}" aria-controls="bodlist"><div class="pp"><div class="avatar" style="background:var(--ink-3)">+${bod.length}</div><div><div class="t1">Board of Directors &amp; GMs (${bod.length})</div>`
        + `<div class="t2">${esc(az.slice(0, 2).map(b => b.name).join(', '))}, and ${bod.length - 2} more</div></div></div><span style="flex:1"></span>`
        + `<span class="bodt">${bodOpen ? 'Show less' : 'Show all'}${IC(bodOpen ? 'chevron-up' : 'chevron-down')}</span></div>`
        + `<div class="bodl" id="bodlist" ${bodOpen ? '' : 'hidden'}>${az.map(member).join('')}<div class="srow bodm"><button class="link" data-bodless>${IC('chevron-up')}Show less</button></div></div>`;
    } else h += bod.map(one).join('');
    el.innerHTML = h;
    window.icons();
  }
  let bodOpen = false;
  function toggleBod(open) {
    bodOpen = open;
    drawLeaders();
    const g = $('#bodg'); if (g) g.focus({ preventScroll: !open });
  }
  $('#leaders').addEventListener('click', e => {
    if (e.target.closest('[data-bodless]')) return toggleBod(false);
    if (e.target.closest('#bodg')) toggleBod(!bodOpen);
  });
  $('#leaders').addEventListener('keydown', e => {
    if (!e.target.matches('#bodg') || (e.key !== 'Enter' && e.key !== ' ')) return;
    e.preventDefault(); toggleBod(!bodOpen);
  });
  const vipName = id => vipPeople.find(x => same(x.id, id)) || { id, name: '…', department: '' };
  function vipRow(id, isNew) {
    const p = vipName(id);
    return `<div class="srow ${isNew ? 'new' : ''}" data-v="${esc(id)}"><div class="pp">${avatar(p.name)}<div><div class="t1">${esc(p.name)}</div><div class="t2">${esc(p.department || '')}</div></div></div><span style="flex:1"></span><button class="rm" data-rm title="Remove ${esc(p.name)}">${IC('x')}</button></div>`;
  }
  const vipCount = () => { $('#vipn').textContent = (S.vips || []).length ? `${S.vips.length} of 50` : ''; };
  function drawVips() {
    if (!S) return;
    const ids = S.vips || [];
    $('#viplist').innerHTML = ids.length ? ids.map(id => vipRow(id)).join('') : `<div class="srow"><span class="none">No VIPs yet. Anything a VIP sends always needs action.</span></div>`;
    vipCount();
    window.icons();
  }

  let hl = 0, found = [], searching = false, qSeq = 0, qT;
  const q = $('#vipq'), sugg = $('#sugg');
  function drawSugg() {
    if (q.value.trim().length < 2) { sugg.classList.remove('on'); return; }
    hl = Math.min(hl, Math.max(0, found.length - 1));
    const full = (S.vips || []).length >= 50;
    sugg.innerHTML = found.length ? found.map((p, i) => {
      const lead = leaders.find(l => same(l.id, p.id)), vip = (S.vips || []).some(v => same(v, p.id));
      const why = lead ? `Already a leader (${lead.reason || 'org chart'})` : vip ? 'Already a VIP' : full ? 'You have 50 VIPs — remove one first' : p.department || '';
      return `<button data-add="${esc(p.id)}" class="${i === hl ? 'hl' : ''}" ${lead || vip || full ? 'disabled' : ''} role="option">${avatar(p.name)}<div><div class="t1">${esc(p.name)}</div><div class="t2">${esc(why)}</div></div></button>`;
    }).join('') : `<div class="none">${searching ? 'Searching…' : 'No one found'}</div>`;
    sugg.classList.add('on');
  }
  q.addEventListener('input', () => {
    hl = 0; clearTimeout(qT);
    const text = q.value.trim();
    if (text.length < 2) { drawSugg(); return; }
    searching = true; found = []; drawSugg();
    qT = setTimeout(async () => {
      const seq = ++qSeq;
      const r = await app.api('GET', 'api/notifier/people?q=' + encodeURIComponent(text.slice(0, 80)));
      if (seq !== qSeq) return;
      searching = false;
      found = r.ok && Array.isArray(r.json) ? r.json : [];
      drawSugg();
    }, 200);
  });
  q.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { hl = Math.min(found.length - 1, hl + 1); drawSugg(); e.preventDefault(); }
    if (e.key === 'ArrowUp') { hl = Math.max(0, hl - 1); drawSugg(); e.preventDefault(); }
    if (e.key === 'Enter') { const b = sugg.querySelector('button.hl:not([disabled])') || sugg.querySelector('button:not([disabled])'); if (b) addVip(b.dataset.add); e.preventDefault(); }
    // Esc closes the suggestions, not the window.
    if (e.key === 'Escape' && sugg.classList.contains('on')) { e.stopPropagation(); sugg.classList.remove('on'); }
  });
  sugg.addEventListener('click', e => { const b = e.target.closest('[data-add]'); if (b && !b.disabled) addVip(b.dataset.add); });
  document.addEventListener('click', e => { if (!e.target.closest('.addp')) sugg.classList.remove('on'); });
  function addVip(id) {
    const ids = S.vips || [];
    if (!id || ids.some(v => same(v, id)) || ids.length >= 50) return;
    const p = found.find(x => same(x.id, id));
    if (p && !vipPeople.some(x => same(x.id, id))) vipPeople.push(p);
    if (!ids.length) $('#viplist').innerHTML = '';
    $('#viplist').insertAdjacentHTML('beforeend', vipRow(id, true));
    q.value = ''; sugg.classList.remove('on');
    vipsChanged = true;
    S = Object.assign({}, S, { vips: ids.concat([id]) });
    vipCount();
    window.icons();
    post({ type: 'save', settings: S });
  }
  $('#viplist').addEventListener('click', e => {
    const b = e.target.closest('[data-rm]'); if (!b) return;
    const r = b.closest('.srow'), id = r.dataset.v;
    vipsChanged = true;
    S = Object.assign({}, S, { vips: (S.vips || []).filter(v => !same(v, id)) });
    post({ type: 'save', settings: S });
    const done = () => { r.remove(); if (!S.vips.length) drawVips(); vipCount(); };
    if (rm()) return done();
    r.style.height = r.offsetHeight + 'px'; r.style.overflow = 'hidden'; reflow(r);
    r.style.transition = 'height .34s var(--spring),opacity .2s,padding .34s var(--spring),min-height .34s var(--spring)';
    r.style.height = '0px'; r.style.minHeight = '0px'; r.style.opacity = '0'; r.style.paddingTop = r.style.paddingBottom = '0px';
    setTimeout(done, 350);
  });
})();
