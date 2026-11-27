(function () {
  const { post, esc } = window.app;
  const $ = s => document.querySelector(s);
  document.body.dataset.esc = 'off';
  $('[data-min]').innerHTML = ICON.min;
  $('[data-min]').onclick = () => post({ type: 'mediaMin', min: !document.body.classList.contains('min') });
  let started = 0, timer, unattended = false, helperName = 'ICT';

  // The prompt lists what the helper may do; the user can untick clipboard or file transfer (and mouse/keyboard).
  // Only ticked items are allowed — the app refuses the rest on this PC (RemoteHost.Data.cs).
  const PERMS = [['control', 'mouse-pointer-2', 'Mouse and keyboard'], ['clipboard', 'clipboard-copy', 'Clipboard'], ['files', 'file-up', 'File transfer']];
  let allowed = new Set(['control', 'clipboard']), max = new Set(['control', 'clipboard', 'files']);
  // Ticks start from the company default (or the person's own, Settings › Remote); what an Administrator switched off
  // for this PC is greyed out ("not available on this PC").
  function drawOpts(view) {
    $('#aOpts').innerHTML = PERMS.filter(([k]) => !(view && k === 'control')).map(([k, ic, t]) => { const ok = max.has(k); return `<span class="pr${ok ? '' : ' dis'}" data-k="${k}"><span class="cb${allowed.has(k) ? '' : ' off'}">${allowed.has(k) ? IC('check') : ''}</span>${IC(ic)}${t}${ok ? '' : ' <small style="color:var(--ink-3)">· not available on this PC</small>'}</span>`; }).join('')
      + (view ? `<span>${IC('eye')}View only</span>` : '') + `<span>${IC('shield-check')}Logged in iPop · never recorded</span>`;
  }
  let viewOnly = false;
  $('#aOpts').onclick = e => { const r = e.target.closest('.pr'); if (!r || r.classList.contains('dis')) return; const k = r.dataset.k; allowed.has(k) ? allowed.delete(k) : allowed.add(k); drawOpts(viewOnly); };
  app.on('ask', m => {
    document.body.className = 'ask';
    viewOnly = m.mode === 'view';
    max = new Set((m.max || 'control,clipboard,files').split(',').filter(Boolean));
    allowed = new Set((m.defaults || 'control,clipboard').split(',').filter(k => max.has(k) && !(viewOnly && k === 'control')));
    $('#aAv').textContent = m.initials; $('#aTitle').textContent = m.joining ? `${m.helper} from ICT wants to watch` : `${m.helper} from ICT wants to help`;
    $('#aSub').textContent = m.joining ? 'Someone is already helping · view only' : 'Remote help · ' + (m.from ? 'from ' + m.from : m.machine);
    $('#aText').textContent = viewOnly ? `${m.helper.split(' ')[0]} will see your screen but can't click or type.` : `${m.helper.split(' ')[0]} will see your screen and can use the mouse and keyboard. You can end it any time.`;
    drawOpts(viewOnly);
    const c = $('#cnt'); c.style.transition = 'none'; c.style.width = '100%';
    requestAnimationFrame(() => requestAnimationFrame(() => { c.style.transition = `width ${m.seconds}s linear`; c.style.width = '0%'; }));
  });
  $('#yes').onclick = () => post({ type: 'answer', allow: true, perms: [...allowed] });
  $('#no').onclick = () => post({ type: 'answer', allow: false, perms: [] });

  // What ICT may do in this session, as chips above the footer (off ones dimmed).
  function chips(list, mode) {
    const p = new Set((list || '').split(',').filter(Boolean));
    const ch = (on, ic, t, icOff, tOff) => on ? `<span>${IC(ic)}${t}</span>` : `<span class="off">${IC(icOff)}${tOff}</span>`;
    $('#chips').innerHTML = `<span>${IC('eye')}Seeing your screen</span>`
      + ch(p.has('control') && mode !== 'view', 'mouse-pointer-2', 'Controlling', 'mouse-pointer-ban', 'Control: off')
      + ch(p.has('clipboard'), 'clipboard-copy', 'Clipboard', 'clipboard-x', 'Clipboard: off')
      + ch(p.has('files'), 'file-up', 'Files', 'file-x', 'Files: off');
  }

  app.on('help', m => {
    document.body.className = '';
    helperName = m.helper;
    $('#hName').textContent = '· ' + m.helper; $('#fName').textContent = m.helper + ' · ICT'; $('#cName').textContent = m.helper;
    $('#fAv').textContent = m.initials;
    unattended = !!m.unattended;
    document.body.classList.toggle('ua', unattended);
    $('#fSub').textContent = unattended ? 'connected unattended · you can keep working' : m.mode === 'view' ? 'is watching this PC · you can keep working' : 'is helping on this PC · you can keep working';
    chips(m.perms, m.mode);
    $('#fileBtn').hidden = !(m.perms || '').split(',').includes('files');
    $('#tlim').textContent = 'up to ' + lim(m.maxFileBytes || limit); if (m.maxFileBytes) limit = m.maxFileBytes;
    $('#msgs').innerHTML = ''; $('#tflist').innerHTML = ''; $('#tx').hidden = true; openChat(false);
    started = Date.now(); clearInterval(timer);
    timer = setInterval(() => { const s = Math.floor((Date.now() - started) / 1000); $('#tm').textContent = String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); }, 1000);
    show({ kind: 'default' });
  });
  app.on('min', m => document.body.classList.toggle('min', !!m.min));

  // Media from the helper: default animation, a picture or a video (muted unless the helper turns sound on).
  function show(m) {
    const el = $('#media');
    if (m.kind === 'default' || !m.url) {
      el.innerHTML = unattended
        ? `<div class="def"><div class="lg"><img src="ipop-mark.png" alt=""></div><h1>Unattended access in use</h1><p>${esc(helperName)} connected without asking, as set by an Administrator. This is logged.</p><div class="dots"><i></i><i></i><i></i></div></div>`
        : `<div class="def"><div class="lg"><img src="ipop-mark.png" alt=""></div><h1>ICT is helping you</h1><p>${esc(helperName)} is working on this PC. You can keep watching.</p><div class="dots"><i></i><i></i><i></i></div></div>`;
    } else if (m.kind === 'video') {
      el.innerHTML = `<video src="${m.url}" autoplay loop playsinline ${m.sound ? '' : 'muted'}></video>`;
    } else {
      el.innerHTML = `<img src="${m.url}" alt="">`;
    }
  }
  app.on('media', show);

  // Chat: kept while the session lasts (never stored). A message from ICT opens the chat and beeps once.
  let chatOn = false;
  const openChat = on => { chatOn = on; $('#chat').classList.toggle('on', on); if (on) $('#tdot').hidden = true; };
  function addMsg(text, me) { const d = document.createElement('div'); d.className = 'msg ' + (me ? 'me' : 'them'); d.textContent = text; $('#msgs').appendChild(d); d.scrollIntoView({ block: 'end' }); }
  app.on('chat', m => { addMsg(m.text, false); if (!chatOn) { $('#tdot').hidden = false; openChat(true); } });
  $('#chatBtn').onclick = () => { openChat(!chatOn); if (chatOn) $('#chatIn').focus(); };
  $('#chatX').onclick = () => openChat(false);
  const send = () => { const v = $('#chatIn').value.trim(); if (!v) return; post({ type: 'chatSend', text: v }); addMsg(v, true); $('#chatIn').value = ''; };
  $('#chatSend').onclick = send;
  $('#chatIn').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); send(); } });

  // Files both ways (mockup png/32): rows from the app; "Send a file to ICT" opens the file picker on this PC.
  const fmtSize = b => b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB' : b >= 1048576 ? (b / 1048576).toFixed(b >= 10485760 ? 0 : 1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  const lim = b => { const mb = Math.round(b / 1048576); return mb >= 1024 ? mb / 1024 + ' GB' : mb + ' MB'; };
  let limit = 2048 * 1048576;
  const showFiles = () => { $('#tx').hidden = false; openChat(true); };
  $('#fileBtn').onclick = showFiles;
  $('#tsend').onclick = () => post({ type: 'sendFile' });
  app.on('xfer', t => {
    if (t.limit) limit = t.limit;
    if (t.dir === 'up') showFiles();
    let el = document.querySelector(`#tflist .file[data-t="${t.id}"]`);
    if (!el) { el = document.createElement('div'); el.className = 'file'; el.dataset.t = t.id; $('#tflist').prepend(el); }
    const lab = t.dir === 'up' ? 'From ICT' : 'To ICT', pct = Math.round((t.p || 0) * 100);
    let sub, act = '';
    if (t.state === 'run') { sub = `${lab} · ${pct}% · ${fmtSize(t.size * (t.p || 0))} of ${fmtSize(t.size)}`; act = `<button class="fx" data-a="cancel">${IC('x')}Cancel</button>`; }
    else if (t.state === 'done') sub = `${lab} · ${t.dir === 'up' ? 'saved to Downloads' : 'saved to Downloads'} · ${fmtSize(t.size)}`;
    else if (t.state === 'cancel') sub = `${lab} · cancelled`;
    else if (t.state === 'big') sub = `${lab} · Too large: the limit is ${lim(limit)} per file`;
    else sub = `${lab} · ${t.reason === 'busy' ? 'Two files are already on the way. Try again when one is done' : t.reason === 'space' ? 'Not enough free disk space on this PC' : t.state === 'notallowed' ? 'File transfer is not allowed in this session' : 'The connection was interrupted'}`;
    el.className = 'file' + (t.state === 'fail' || t.state === 'big' ? ' err' : t.state === 'cancel' ? ' off' : t.state === 'done' ? ' ok' : '');
    el.innerHTML = `<span class="fi">${IC(t.dir === 'up' ? 'file-down' : 'file-up')}</span><span class="m"><b>${esc(t.name)}</b><small>${esc(sub)}</small>${t.state === 'run' ? `<div class="bar"><i style="width:${pct}%"></i></div>` : ''}</span>${act}`;
    el.onclick = e => { if (e.target.closest('[data-a=cancel]')) post({ type: 'xferAction', id: t.id, action: 'cancel' }); };
  });
  app.on('limit', m => { limit = m.bytes || limit; $('#tlim').textContent = 'up to ' + lim(limit); });

  $('#endBtn').onclick = () => post({ type: 'endSession' });
  app.ready();
})();
