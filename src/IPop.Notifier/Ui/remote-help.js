(function () {
  const { post, esc } = window.app;
  const $ = s => document.querySelector(s);
  document.body.dataset.esc = 'off';
  $('[data-min]').innerHTML = ICON.min;
  $('[data-min]').onclick = () => post({ type: 'mediaMin', min: !document.body.classList.contains('min') });
  let started = 0, timer;

  app.on('ask', m => {
    document.body.className = 'ask';
    $('#aAv').textContent = m.initials; $('#aTitle').textContent = m.joining ? `${m.helper} from ICT wants to watch` : `${m.helper} from ICT wants to help`;
    $('#aSub').textContent = m.joining ? 'Someone is already helping · view only' : 'Remote help · ' + m.machine;
    $('#aText').textContent = m.mode === 'view' ? `${m.helper.split(' ')[0]} will see your screen but can't click or type.` : `${m.helper.split(' ')[0]} will see your screen and can use the mouse and keyboard. You can end it any time.`;
    $('#aOpts').innerHTML = (m.mode === 'view' ? `<span>${IC('eye')}View only</span>` : `<span>${IC('mouse-pointer-2')}Mouse and keyboard</span><span>${IC('clipboard-copy')}Copy and paste, files</span>`)
      + `<span>${IC('shield-check')}Logged in iPop · never recorded</span>`;
    const c = $('#cnt'); c.style.transition = 'none'; c.style.width = '100%';
    requestAnimationFrame(() => requestAnimationFrame(() => { c.style.transition = `width ${m.seconds}s linear`; c.style.width = '0%'; }));
  });
  $('#yes').onclick = () => post({ type: 'answer', allow: true });
  $('#no').onclick = () => post({ type: 'answer', allow: false });

  app.on('help', m => {
    document.body.className = '';
    $('#hName').textContent = '· ' + m.helper; $('#fName').textContent = m.helper + ' · ICT'; $('#cName').textContent = m.helper;
    $('#fAv').textContent = m.initials;
    $('#fSub').textContent = m.mode === 'view' ? 'is watching this PC · you can keep working' : 'is helping on this PC · you can keep working';
    started = Date.now(); clearInterval(timer);
    timer = setInterval(() => { const s = Math.floor((Date.now() - started) / 1000); $('#tm').textContent = String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); }, 1000);
    show({ kind: 'default' });
  });
  app.on('min', m => document.body.classList.toggle('min', !!m.min));

  // Media from the helper: default animation, a picture or a video (muted unless the helper turns sound on).
  function show(m) {
    const el = $('#media');
    if (m.kind === 'default' || !m.url) {
      el.innerHTML = `<div class="def"><div class="lg"><img src="ipop-mark.webp" alt=""></div><h1>ICT is helping you</h1><p>${esc(($('#fName').textContent || 'ICT').replace(' · ICT', ''))} is working on this PC. You can keep watching.</p><div class="dots"><i></i><i></i><i></i></div></div>`;
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
