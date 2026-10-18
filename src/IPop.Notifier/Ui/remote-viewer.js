// ICT side of remote help: the user's screen, input capture, toolbar, media / chat / files panel.
(function () {
  const R = window.remote, { api, post, esc, snack } = window.app;
  const $ = s => document.querySelector(s);
  const stage = $('#stage');
  const log = text => post({ type: 'log', text: String(text).slice(0, 400) });
  window.addEventListener('error', e => log('error: ' + e.message));
  window.addEventListener('unhandledrejection', e => log('rejection: ' + (e.reason && (e.reason.name + ': ' + e.reason.message) || e.reason)));
  let video = null, sessionId = null, ended = false, media = [], mediaOn = null, clipOn = true, minimised = false, files = [];
  document.body.dataset.esc = 'off';
  $('[data-min]').innerHTML = ICON.min; $('[data-max]').innerHTML = IC('maximize-2'); $('[data-close]').innerHTML = ICON.x;
  $('[data-min]').onclick = () => post({ type: 'minimize' });
  $('[data-max]').onclick = () => post({ type: 'maximize' });
  document.querySelectorAll('[data-close]').forEach(b => b.onclick = () => end());

  // ── session ────────────────────────────────────────────────────────────
  app.on('start', async m => {
    sessionId = m.sessionId;
    $('#pc').textContent = m.machine; $('#who').textContent = '· ' + m.user;
    if (m.status === 'active') return join();
    $('#waitTxt').textContent = 'Waiting for ' + m.user.split(' ')[0] + ' to allow…';
    $('#waitSub').textContent = 'They have 30 seconds to answer.';
    // The answer also arrives over the hub; polling covers the case where it came before this page was ready.
    for (let i = 0; i < 40 && !joining && !ended; i++) {
      await new Promise(r => setTimeout(r, 1000));
      const s = await api('GET', `api/notifier/remote/sessions/${sessionId}`);
      const st = s.ok && s.json.status;
      if (st === 'active') return join();
      if (st === 'declined' || st === 'expired' || st === 'ended') return answer(st);
    }
  });
  function answer(status) {
    if (status === 'active') return join();
    if (joining || ended) return;
    showEnded(status === 'declined' ? 'The user declined' : 'No answer', status === 'declined' ? 'They chose not to share their screen.' : 'The request timed out after 30 seconds.');
  }
  app.on('answer', m => answer(m.status));
  app.on('remoteEnded', m => showEnded('Remote help ended', m.endedBy === 'user' ? 'The user ended the session.' : ''));
  app.on('clipboard', m => { if (clipOn && R.room) R.sendReliable({ t: 'clip', kind: m.kind, text: m.text, data: m.data }, 'clip'); });
  app.on('clipFile', async m => {
    if (!clipOn || !R.room) return;
    const bin = Uint8Array.from(atob(m.data), c => c.charCodeAt(0));
    await R.sendFile(new File([bin], m.name, { type: m.mime }), 'clip-file');
    addFile(m.name, m.data.length * 0.75, 'Copied to the user · ready to paste', 1);
  });

  let joining = false;
  async function join() {
    if (joining) return;
    joining = true;
    $('#waitTxt').textContent = 'Connecting…'; $('#waitSub').textContent = '';
    const r = await api('GET', `api/notifier/remote/sessions/${sessionId}/join`);
    log('join ' + r.status + ' ' + (r.json ? r.json.url + ' ' + r.json.room : ''));
    if (!r.ok) return showEnded('Couldn\'t connect', r.json?.message || 'Try again in a moment.');
    R.onTrack = track => {
      if (track.kind !== 'video') return;
      video = track.attach(); video.muted = true; video.playsInline = true;
      stage.querySelectorAll('video').forEach(v => v.remove());
      stage.prepend(video); $('#wait').style.display = 'none'; $('#ghost').hidden = mediaOn === null && !$('#showMedia').classList.contains('on');
      live('Controlling', '');
      stage.focus();
    };
    R.onData = (m, topic) => {
      if (topic === 'chat') addMsg(m.text, false);
      if (topic === 'clip' && clipOn) post({ type: 'setClipboard', kind: m.kind, text: m.text, data: m.data });
      if (m.t === 'screen') $('#res').textContent = `${m.w}×${m.h} · H.264`;
      if (m.t === 'bye') showEnded('Remote help ended', 'The user ended the session.');
      if (m.t === 'inStat') inputStat(m);
    };
    R.onFile = async reader => { const b = await R.readAll(reader); post({ type: 'saveFile', name: reader.info.name, data: R.b64(b) }); addFile(reader.info.name, b.length, 'From the user · saved to Downloads', 1); };
    R.onClipFile = async reader => { const b = await R.readAll(reader); post({ type: 'setClipboardFile', name: reader.info.name, data: R.b64(b) }); snack('File copied — paste it here'); };
    R.onReconnecting = on => live(on ? 'Reconnecting' : 'Controlling', on ? 'warn' : '');
    R.onDisconnected = () => { if (!ended) showEnded('Connection lost', 'The user\'s PC went offline or the network dropped.'); };
    R.onPeer = (p, joined) => { if (!joined && p.identity.startsWith('host:') && !ended) showEnded('Remote help ended', 'The user\'s PC left the session.'); };
    try { await R.join(r.json); log('connected; peers ' + R.room.remoteParticipants.size); } catch (e) { log('connect failed ' + (e.name + ': ' + e.message)); return showEnded('Couldn\'t connect', String(e.message || e)); }
    setMode(r.json.mode);
    sendMedia();
    statsLoop();
  }

  let warned = false;
  function inputStat(m) {
    log(`input played=${m.played} refused=${m.refused} err=${m.err} fg=${m.fg} desk=${m.desk}`);
    const locked = m.desk !== undefined && m.desk !== 'Default';
    $('#locked').classList.toggle('on', locked);
    if (locked) { $('#inp').innerHTML = IC('lock') + '<b class="mid">locked</b>'; $('#inp').title = 'The user\'s PC is locked or showing a UAC prompt'; return; }
    const el = $('#inp');
    if (m.refused > 0 && m.err) {
      el.innerHTML = IC('mouse-pointer-ban') + '<b class="bad">blocked</b>';
      if (!warned) { warned = true; snack(m.err === 5 ? 'Windows on the user\'s PC refused mouse and keyboard — an administrator window or the lock screen is in front.' : 'Mouse and keyboard were refused on the user\'s PC (code ' + m.err + ').'); }
    } else {
      el.innerHTML = IC('mouse-pointer-2') + '<b class="ok">' + m.played + '</b>';
    }
    el.title = 'Played ' + m.played + ', refused ' + m.refused + (m.fg ? ' · in front: ' + m.fg : '');
  }

  function live(text, cls) { $('#liveTxt').textContent = text; $('#live').className = 'live ' + (cls || ''); }

  async function statsLoop() {
    while (R.room && !ended) {
      const s = await R.stats();
      const f = $('#fps'), p = $('#ping');
      f.textContent = s.fps || '–'; f.className = s.fps >= 50 ? 'ok' : s.fps >= 25 ? 'mid' : 'bad';
      p.textContent = s.rtt || '–'; p.className = s.rtt <= 30 ? 'ok' : s.rtt <= 80 ? 'mid' : 'bad';
      if (s.w) $('#res').textContent = `${s.w}×${s.h}${s.codec ? ' · ' + s.codec.toUpperCase() : ''}`;
      await new Promise(r => setTimeout(r, 1000));
    }
  }

  async function end() {
    if (!ended && sessionId) {
      ended = true;
      try { R.sendReliable({ t: 'bye' }); } catch { }
      await api('POST', `api/notifier/remote/sessions/${sessionId}/end`, { files: files.join('\n') });
      await R.leave();
    }
    post({ type: 'close' });
  }
  $('#end').onclick = end;
  function showEnded(title, sub) {
    ended = true; R.leave(); live('Ended', 'off');
    $('#wait').style.display = 'none'; $('#endedTxt').textContent = title; $('#endedSub').textContent = sub || ''; $('#ended').classList.add('on');
  }

  // ── input ──────────────────────────────────────────────────────────────
  let mode = 'control';
  function setMode(m) {
    mode = m; stage.classList.toggle('view', m === 'view');
    document.querySelectorAll('#mode button').forEach(b => b.classList.toggle('on', b.dataset.m === m));
    if (R.room) R.sendReliable({ t: 'mode', mode: m });
    live(m === 'view' ? 'Viewing' : 'Controlling', '');
  }
  $('#mode').onclick = e => { const b = e.target.closest('button'); if (b) setMode(b.dataset.m); };
  $('#quality').onclick = e => {
    const b = e.target.closest('button'); if (!b) return;
    document.querySelectorAll('#quality button').forEach(x => x.classList.toggle('on', x === b));
    R.sendReliable({ t: 'quality', preset: b.dataset.q });
  };

  // Position inside the video picture (letterboxed), normalised 0..1.
  function norm(e) {
    if (!video) return null;
    const r = video.getBoundingClientRect();
    const x = (e.clientX - r.left) / r.width, y = (e.clientY - r.top) / r.height;
    return x < 0 || y < 0 || x > 1 || y > 1 ? null : { x: +x.toFixed(5), y: +y.toFixed(5) };
  }
  let lastMove = 0, pending = null;
  stage.addEventListener('mousemove', e => {
    if (mode !== 'control') return;
    const p = norm(e); if (!p) return;
    pending = p;
    const now = performance.now();
    if (now - lastMove >= 8) { lastMove = now; R.input({ t: 'm', ...pending }); pending = null; } // ~120 Hz cap
  });
  stage.addEventListener('mousedown', e => { if (mode !== 'control') return; const p = norm(e); if (!p) return; e.preventDefault(); stage.focus(); R.inputReliable({ t: 'b', ...p, b: e.button, d: 1 }); });
  stage.addEventListener('mouseup', e => { if (mode !== 'control') return; const p = norm(e); if (!p) return; R.inputReliable({ t: 'b', ...p, b: e.button, d: 0 }); });
  stage.addEventListener('contextmenu', e => e.preventDefault());
  stage.addEventListener('wheel', e => { if (mode !== 'control') return; e.preventDefault(); R.input({ t: 'w', dx: e.deltaX, dy: e.deltaY }); }, { passive: false });
  stage.addEventListener('dblclick', e => e.preventDefault());

  // Keys go as Windows virtual-key codes (from KeyboardEvent.code) so shortcuts work on any layout.
  const VK = { Backspace: 8, Tab: 9, Enter: 13, ShiftLeft: 16, ShiftRight: 16, ControlLeft: 17, ControlRight: 17, AltLeft: 18, AltRight: 18, Pause: 19, CapsLock: 20,
    Escape: 27, Space: 32, PageUp: 33, PageDown: 34, End: 35, Home: 36, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, PrintScreen: 44, Insert: 45, Delete: 46,
    MetaLeft: 91, MetaRight: 92, ContextMenu: 93, NumpadMultiply: 106, NumpadAdd: 107, NumpadSubtract: 109, NumpadDecimal: 110, NumpadDivide: 111, NumLock: 144, ScrollLock: 145,
    Semicolon: 186, Equal: 187, Comma: 188, Minus: 189, Period: 190, Slash: 191, Backquote: 192, BracketLeft: 219, Backslash: 220, BracketRight: 221, Quote: 222, NumpadEnter: 13 };
  const EXT = new Set(['ControlRight', 'AltRight', 'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'ArrowLeft', 'ArrowUp', 'ArrowRight', 'ArrowDown', 'NumpadDivide', 'NumpadEnter', 'MetaLeft', 'MetaRight', 'ContextMenu']);
  function vkOf(code) {
    if (VK[code]) return VK[code];
    let m;
    if ((m = code.match(/^Key([A-Z])$/))) return m[1].charCodeAt(0);
    if ((m = code.match(/^Digit(\d)$/))) return 48 + +m[1];
    if ((m = code.match(/^Numpad(\d)$/))) return 96 + +m[1];
    if ((m = code.match(/^F(\d{1,2})$/))) return 111 + +m[1];
    return 0;
  }
  const key = (e, d) => {
    if (mode !== 'control' || document.activeElement !== stage) return;
    const vk = vkOf(e.code); if (!vk) return;
    e.preventDefault();
    // Ctrl+V while clipboard sync is on: our clipboard is already on the user's PC, so their Ctrl+V pastes it.
    R.inputReliable({ t: 'k', vk, d, x: EXT.has(e.code) ? 1 : 0 });
  };
  stage.addEventListener('keydown', e => key(e, 1));
  stage.addEventListener('keyup', e => key(e, 0));
  stage.addEventListener('blur', () => R.room && R.inputReliable({ t: 'release' }));

  $('#keys').onclick = e => {
    document.querySelectorAll('.menu').forEach(m => m.remove());
    const r = e.currentTarget.getBoundingClientRect();
    const m = document.createElement('div'); m.className = 'menu'; m.style.left = r.left + 'px'; m.style.top = (r.bottom + 6) + 'px';
    m.innerHTML = [['cad', 'keyboard', 'Ctrl + Alt + Del'], ['win', 'layout-grid', 'Windows key'], ['alttab', 'app-window', 'Alt + Tab'], ['taskmgr', 'activity', 'Task Manager'], ['lock', 'lock', 'Lock the PC']]
      .map(([k, i, n]) => `<button data-k="${k}">${IC(i)}${n}</button>`).join('');
    document.body.appendChild(m);
    m.onclick = ev => { const k = ev.target.closest('button')?.dataset.k; m.remove(); if (k) R.inputReliable({ t: 'combo', k }); stage.focus(); };
    setTimeout(() => document.addEventListener('click', () => m.remove(), { once: true }), 0);
  };

  // ── clipboard ──────────────────────────────────────────────────────────
  $('#clip').onclick = () => { clipOn = !clipOn; $('#clip').classList.toggle('on', clipOn); R.sendReliable({ t: 'clipSync', on: clipOn }); post({ type: 'clipSync', on: clipOn }); };

  // ── side panel ─────────────────────────────────────────────────────────
  let panel = 'media';
  function showPanel(p) {
    panel = p;
    $('#side').classList.add('on');
    document.querySelectorAll('.tabs button').forEach(b => b.classList.toggle('on', b.dataset.p === p));
    document.querySelectorAll('.tbar [data-p]').forEach(b => b.classList.toggle('on', b.dataset.p === p));
    document.querySelectorAll('.pane').forEach(x => x.classList.toggle('on', x.id === 'p-' + p));
    $('#compose').classList.toggle('on', p === 'chat');
    const t = { media: ['Media', 'Shown only on the user\'s screen'], chat: ['Chat', 'Not saved'], files: ['Files', 'Both ways · saved to Downloads'] }[p];
    $('#sideTitle').textContent = t[0]; $('#sideNote').textContent = t[1];
    if (p === 'chat') setTimeout(() => $('#chatIn').focus(), 50);
  }
  document.querySelectorAll('[data-p]').forEach(b => b.onclick = () => {
    if (b.closest('.tbar') && panel === b.dataset.p && $('#side').classList.contains('on')) { $('#side').classList.remove('on'); b.classList.remove('on'); return; }
    showPanel(b.dataset.p);
  });

  // Chat (never stored)
  function addMsg(text, me) {
    const d = document.createElement('div'); d.className = 'msg ' + (me ? 'me' : 'them'); d.textContent = text;
    $('#msgs').appendChild(d); d.scrollIntoView({ block: 'end' });
    if (!me && panel !== 'chat') { $('.tbar [data-p=chat]').classList.add('on'); snack('Message from the user'); }
  }
  const sendChat = () => { const v = $('#chatIn').value.trim(); if (!v || !R.room) return; R.sendReliable({ text: v }, 'chat'); addMsg(v, true); $('#chatIn').value = ''; };
  $('#chatSend').onclick = sendChat;
  $('#chatIn').addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); sendChat(); } e.stopPropagation(); });

  // Files
  function addFile(name, size, sub, progress) {
    const el = document.createElement('div'); el.className = 'file';
    el.innerHTML = `<span class="fi">${IC('file')}</span><span class="m"><b>${esc(name)}</b><small>${esc(sub)} · ${fmt(size)}</small><div class="bar"><i style="width:${(progress || 0) * 100}%"></i></div></span>`;
    $('#flist').prepend(el); return el;
  }
  const fmt = b => b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  async function sendFiles(list) {
    if (!R.room) return;
    for (const f of list) {
      const el = addFile(f.name, f.size, 'To the user', 0);
      try { await R.sendFile(f, 'file', p => el.querySelector('.bar i').style.width = (p * 100) + '%'); el.querySelector('small').textContent = 'To the user · saved to their Downloads · ' + fmt(f.size); files.push(f.name); }
      catch { el.querySelector('small').textContent = 'Not sent — try again'; }
    }
  }
  $('#pick').onchange = e => { sendFiles([...e.target.files]); e.target.value = ''; };
  const drop = $('#drop');
  ['dragenter', 'dragover'].forEach(t => drop.addEventListener(t, e => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach(t => drop.addEventListener(t, e => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', e => sendFiles([...e.dataTransfer.files]));
  stage.addEventListener('dragover', e => e.preventDefault());
  stage.addEventListener('drop', e => { e.preventDefault(); showPanel('files'); sendFiles([...e.dataTransfer.files]); });

  // Media (My media gallery; shown only on the user's screen)
  async function loadMedia() {
    const r = await api('GET', 'api/notifier/remote/media');
    media = r.ok ? r.json.items : [];
    drawMedia();
  }
  function drawMedia() {
    const g = $('#mgrid');
    g.innerHTML = `<div class="mt ${mediaOn === null ? 'on' : ''}" data-id=""><div class="def"><img src="ipop-mark.webp" alt=""></div><span class="k">${IC('sparkles')}iPop (default)</span></div>`
      + media.map(m => `<div class="mt ${mediaOn === m.id ? 'on' : ''}" data-id="${m.id}" data-att="${m.attachmentId}"><span class="k">${IC(m.contentType.startsWith('video') ? 'play' : 'image')}${esc(m.name)}</span></div>`).join('')
      + `<label class="mt add"><span>${IC('upload')}Upload</span></label>`;
    g.querySelectorAll('[data-att]').forEach(async el => {
      const m = media.find(x => x.id === el.dataset.id);
      if (m && m.contentType.startsWith('image')) { const u = await app.image('api/attachments/' + el.dataset.att); if (u) el.style.backgroundImage = `url("${u}")`; }
      else el.style.background = 'linear-gradient(135deg,#2563eb,#1f8fa6)';
    });
  }
  $('#mgrid').onclick = e => {
    if (e.target.closest('.add')) return $('#mediaPick').click();
    const t = e.target.closest('.mt'); if (!t) return;
    mediaOn = t.dataset.id || null; drawMedia(); sendMedia();
  };
  $('#mediaPick').onchange = async e => {
    const f = e.target.files[0]; e.target.value = ''; if (!f) return;
    snack('Uploading ' + f.name + '…');
    const data = R.b64(new Uint8Array(await f.arrayBuffer()));
    post({ type: 'uploadMedia', name: f.name, mime: f.type, data });
  };
  app.on('mediaUploaded', m => { if (m.error) snack(m.error); else { snack('Added to My media'); loadMedia(); } });
  function sendMedia() {
    if (!R.room) return;
    const m = media.find(x => x.id === mediaOn);
    R.sendReliable({ t: 'media', show: $('#showMedia').classList.contains('on'), id: m?.attachmentId || null, kind: m ? (m.contentType.startsWith('video') ? 'video' : 'image') : 'default', sound: $('#sound').classList.contains('on'), minimised });
    $('#ghost').hidden = !$('#showMedia').classList.contains('on') || !video;
  }
  $('#sound').onclick = () => { $('#sound').classList.toggle('on'); sendMedia(); };
  $('#showMedia').onclick = () => { $('#showMedia').classList.toggle('on'); sendMedia(); };
  $('#umin').onclick = () => { minimised = !minimised; $('#umin span').textContent = minimised ? 'Show' : 'Minimise'; $('#umin').classList.toggle('on', minimised); sendMedia(); };

  // Full screen: the picture takes the whole monitor; a small bar slides in at the top edge.
  const setFs = on => { document.body.classList.toggle('fs', on); post({ type: 'fullscreen', on }); setTimeout(() => stage.focus(), 50); };
  $('#fs').onclick = () => setFs(true);
  $('#fsExit').onclick = () => setFs(false);
  $('#fsEnd').onclick = () => end();
  document.addEventListener('mousemove', e => { if (document.body.classList.contains('fs')) $('#fsbar').classList.toggle('show', e.clientY < 8 || !!e.target.closest('#fsbar')); });
  document.addEventListener('keydown', e => { if (e.key === 'F11') { e.preventDefault(); setFs(!document.body.classList.contains('fs')); } }, true);

  app.ready();
  loadMedia();
})();
