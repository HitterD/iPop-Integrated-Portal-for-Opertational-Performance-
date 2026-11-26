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
  let allowed = new Set(['control', 'clipboard']);
  function drawOpts(view) {
    $('#aOpts').innerHTML = PERMS.filter(([k]) => !(view && k === 'control')).map(([k, ic, t]) => `<span class="pr" data-k="${k}"><span class="cb${allowed.has(k) ? '' : ' off'}">${allowed.has(k) ? IC('check') : ''}</span>${IC(ic)}${t}</span>`).join('')
      + (view ? `<span>${IC('eye')}View only</span>` : '') + `<span>${IC('shield-check')}Logged in iPop · never recorded</span>`;
  }
  let viewOnly = false;
  $('#aOpts').onclick = e => { const r = e.target.closest('.pr'); if (!r) return; const k = r.dataset.k; allowed.has(k) ? allowed.delete(k) : allowed.add(k); drawOpts(viewOnly); };
  app.on('ask', m => {
    document.body.className = 'ask';
    viewOnly = m.mode === 'view';
    allowed = new Set(viewOnly ? ['clipboard'] : ['control', 'clipboard']);
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

  // Chat (not saved)
  function addMsg(text, me) { const d = document.createElement('div'); d.className = 'msg ' + (me ? 'me' : 'them'); d.textContent = text; $('#msgs').appendChild(d); d.scrollIntoView({ block: 'end' }); }
  app.on('chat', m => { addMsg(m.text, false); $('#chat').classList.add('on'); });
  $('#chatBtn').onclick = () => { $('#chat').classList.toggle('on'); if ($('#chat').classList.contains('on')) $('#chatIn').focus(); };
  $('#chatX').onclick = () => $('#chat').classList.remove('on');
  const send = () => { const v = $('#chatIn').value.trim(); if (!v) return; post({ type: 'chatSend', text: v }); addMsg(v, true); $('#chatIn').value = ''; };
  $('#chatSend').onclick = send;
  $('#chatIn').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); send(); } });
  $('#endBtn').onclick = () => post({ type: 'endSession' });
  app.ready();
})();
