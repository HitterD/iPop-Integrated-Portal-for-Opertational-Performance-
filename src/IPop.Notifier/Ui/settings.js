(function () {
  const { esc, post } = window.app;
  const $ = s => document.querySelector(s);
  let S = null, saveT;
  document.querySelector('[data-min]').innerHTML = ICON.min;
  document.querySelector('[data-close]').innerHTML = ICON.x;
  document.querySelector('[data-min]').onclick = () => post({ type: 'minimize' });
  document.querySelector('[data-close]').onclick = () => post({ type: 'close' });

  $('#nav').onclick = e => {
    const a = e.target.closest('a'); if (!a) return;
    document.querySelectorAll('#nav a').forEach(x => x.classList.toggle('on', x === a));
    document.querySelectorAll('.main section').forEach(s => s.classList.toggle('on', s.id === 's-' + a.dataset.s));
  };

  const MODS = [['mail', '✉', 'Mail'], ['tickets', '🎫', 'iDesk tickets'], ['calendar', '📅', 'Calendar & meetings'], ['chat', '💬', 'Chat'],
    ['projects', '✓', 'Projects'], ['news', '📣', 'News & memos'], ['notes', '📝', 'Notes'], ['calls', '📞', 'Calls'], ['system', '⚙', 'System']];
  const SOUNDS = [['mail', 'Mail · Envelope'], ['tickets', 'iDesk · Lifeline'], ['meeting', 'Meeting · Doorbell'], ['dm', 'Chat · Bubble'], ['group', 'Group chat · Pebble'],
    ['mention', 'Mention · Spark'], ['project', 'Projects · Tasker'], ['announce', 'News · Herald'], ['notes', 'Notes · Paper'], ['system', 'System · Ping']];

  app.on('settings', m => { S = m.settings; draw(m.account); });
  app.on('saved', m => {
    S = m.settings;
    const el = $('#saved'); el.textContent = m.ok ? 'Saved' : 'Saved on this PC — will sync when iPop is reachable';
    el.classList.add('on'); clearTimeout(saveT); saveT = setTimeout(() => el.classList.remove('on'), 1800);
  });
  app.ready();

  function draw(acc) {
    document.querySelectorAll('.sty').forEach(b => b.classList.toggle('on', b.dataset.style === S.style));
    const pop = S.popups === 'on' ? 'on' : S.popups === 'off' ? 'off' : (S.offUntil && new Date(S.offUntil) - Date.now() > 2 * 3600e3 ? 'tomorrow' : '1h');
    pill('#popups', pop); pill('#meet', S.duringMeetings); pill('#pos', S.position);
    $('#urgent').classList.toggle('on', S.urgentBreaksThrough);
    $('#max').value = String(S.maxVisible);
    if (![...$('#max').options].some(o => o.value === String(S.maxVisible))) $('#max').add(new Option(S.maxVisible, S.maxVisible, true, true));
    $('#maxHint').textContent = S.style === 'stack' ? 'Others wait behind “+N more”' : 'Older windows minimise into the taskbar group';
    $('#vol').value = S.volume; $('#sound').classList.toggle('on', S.sound);
    $('#autostart').classList.toggle('on', S.startWithWindows);
    $('#mods').innerHTML = MODS.map(([k, ic, n]) => `<div class="row"><span class="ic2" style="background:${MOD[k].c}">${ic}</span><span class="t">${n}${k === 'chat' ? '<small>Always a Windows toast</small>' : ''}</span>
      <div class="pill3" data-mod="${k}"><button data-v="popup" ${k === 'chat' ? 'disabled' : ''}>Popup</button><button data-v="toast">Toast</button><button data-v="off">Off</button></div></div>`).join('');
    document.querySelectorAll('[data-mod]').forEach(p => pill(p, S.modules[p.dataset.mod]));
    $('#previews').innerHTML = SOUNDS.map(([k, n]) => `<div class="row"><span class="t">${esc(n)}</span><button class="btn" data-snd="${k}">▶ Play</button></div>`).join('');
    if (acc) {
      $('#who').innerHTML = `Signed in as ${esc(acc.name || '…')}<small>Stays signed in — renews itself; ends after 90 days unused</small>`;
      $('#about').innerHTML = `<span>Version</span><span>${esc(acc.version)}</span><span>Computer</span><span>${esc(acc.computer)}</span><span>Server</span><span>${esc(acc.server)}</span>`;
    }
  }
  function pill(sel, v) { const p = typeof sel === 'string' ? $(sel) : sel; p.querySelectorAll('button').forEach(b => b.classList.toggle('on', b.dataset.v === v)); }

  function save(patch) {
    S = Object.assign({}, S, patch);
    draw();
    post({ type: 'save', settings: S });
  }

  document.addEventListener('click', e => {
    const sty = e.target.closest('.sty'); if (sty) return save({ style: sty.dataset.style });
    const b = e.target.closest('.pill3 button'); if (b && !b.disabled) {
      const p = b.parentElement, v = b.dataset.v;
      if (p.id === 'popups') {
        if (v === 'on' || v === 'off') return save({ popups: v, offUntil: null });
        const until = v === '1h' ? new Date(Date.now() + 3600e3) : (() => { const d = new Date(); d.setDate(d.getDate() + 1); d.setHours(7, 0, 0, 0); return d; })();
        return save({ popups: 'until', offUntil: until.toISOString() });
      }
      if (p.id === 'meet') return save({ duringMeetings: v });
      if (p.id === 'pos') return save({ position: v });
      if (p.dataset.mod) return save({ modules: Object.assign({}, S.modules, { [p.dataset.mod]: v }) });
    }
    const sw = e.target.closest('.sw'); if (sw) {
      const on = !sw.classList.contains('on');
      if (sw.id === 'urgent') return save({ urgentBreaksThrough: on });
      if (sw.id === 'sound') return save({ sound: on });
      if (sw.id === 'autostart') return save({ startWithWindows: on });
    }
    const snd = e.target.closest('[data-snd]'); if (snd) post({ type: 'preview', sound: snd.dataset.snd, volume: +$('#vol').value });
  });
  $('#max').onchange = () => save({ maxVisible: +$('#max').value });
  $('#vol').onchange = () => { save({ volume: +$('#vol').value }); post({ type: 'preview', sound: 'system', volume: +$('#vol').value }); };
  $('#signout').onclick = () => { if (confirm('Sign out of iPop Notifier on this PC?')) post({ type: 'signOut' }); };
})();
