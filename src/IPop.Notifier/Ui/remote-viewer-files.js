// ICT viewer › Files (BIGIS #8 v3, mockup png/06): both ways, streamed, with progress, Cancel and Retry.
//   to the user:   a dropped/picked File → remote-xfer.js send (64 KB chunks)
//   from the user: LiveKit stream → batches to the app → a ".part" file in the ICT's save folder → renamed when done
// Rows and wording follow the mockup's xfEl(): "To the user · 64% · 24 MB of 38 MB", "Too large: the limit is 2 GB per
// file", "The connection was interrupted", "cancelled".
(function () {
  const R = window.remote, { post, esc } = window.app;
  const $ = s => document.querySelector(s);
  const rows = new Map(); // id → transfer
  let maxBytes = 2048 * 1048576, saveDir = 'Downloads\\iPop Remote', allowed = false;
  const fmtSize = b => b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB' : b >= 1048576 ? (b / 1048576).toFixed(b >= 10485760 ? 0 : 1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
  const lim = b => { const mb = Math.round(b / 1048576); return mb >= 1024 ? mb / 1024 + ' GB' : mb + ' MB'; };

  function draw(t) {
    let el = document.querySelector(`#flist .file[data-t="${t.id}"]`);
    if (!el) { el = document.createElement('div'); el.className = 'file'; el.dataset.t = t.id; $('#flist').prepend(el); }
    const lab = t.dir === 'up' ? 'To the user' : 'From the user';
    const pct = Math.round(t.p * 100);
    let sub, act = '';
    if (t.state === 'run') { sub = `${lab} · ${pct}% · ${fmtSize(t.size * t.p)} of ${fmtSize(t.size)}`; act = `<button class="fx" data-a="cancel" title="Cancel">${IC('x')}Cancel</button>`; }
    else if (t.state === 'wait') { sub = `${lab} · waiting for the connection…`; act = `<button class="fx" data-a="cancel" title="Cancel">${IC('x')}Cancel</button>`; }
    else if (t.state === 'done') { sub = `${lab} · delivered · ${fmtSize(t.size)}`; act = t.dir === 'down' && t.path ? `<button class="fx" data-a="open">${IC('folder-open')}Show</button>` : ''; }
    else if (t.state === 'cancel') { sub = `${lab} · cancelled`; act = t.retry ? `<button class="fx" data-a="retry">${IC('rotate-cw')}Retry</button>` : ''; }
    else { sub = `${lab} · ${t.msg || 'failed'}`; act = t.state === 'big' || !t.retry ? '' : `<button class="fx" data-a="retry">${IC('rotate-cw')}Retry</button>`; }
    el.className = 'file' + (t.state === 'fail' || t.state === 'big' ? ' err' : t.state === 'cancel' ? ' off' : t.state === 'done' ? ' ok' : '');
    el.innerHTML = `<span class="fi">${IC(t.dir === 'up' ? 'file-up' : 'file-down')}</span><span class="m"><b>${esc(t.name)}</b><small>${esc(sub)}</small>${t.state === 'run' || t.state === 'wait' ? `<div class="bar"><i style="width:${pct}%"></i></div>` : ''}</span>${act}`;
    el.onclick = e => {
      const a = e.target.closest('[data-a]')?.dataset.a;
      if (a === 'cancel') cancel(t);
      if (a === 'retry' && t.retry) t.retry();
      if (a === 'open') post({ type: 'reveal', file: t.path });
    };
  }

  function cancel(t) {
    if (t.dir === 'up') xfer.cancelSend(t.id); else xfer.cancelReceive(t.id);
    t.state = 'cancel'; draw(t);
  }

  async function sendOne(file) {
    const t = { id: xfer.newId(), name: file.name, size: file.size, dir: 'up', p: 0, state: 'run' };
    t.retry = () => { rows.delete(t.id); document.querySelector(`#flist .file[data-t="${t.id}"]`)?.remove(); sendOne(file); };
    rows.set(t.id, t);
    if (!allowed) { t.state = 'fail'; t.msg = 'File transfer is not allowed in this session'; t.retry = null; return draw(t); }
    if (file.size > maxBytes) { t.state = 'big'; t.msg = `Too large: the limit is ${lim(maxBytes)} per file`; return draw(t); }
    draw(t);
    let last = 0;
    try {
      const r = await xfer.send(file, { id: t.id, topic: 'file', onProgress: (sent, size) => {
        t.p = size ? sent / size : 1; const now = performance.now(); if (now - last > 200) { last = now; draw(t); } } });
      if (r === 'cancel') { t.state = 'cancel'; } else if (t.state === 'run') { t.state = 'done'; t.p = 1; viewerFiles.sent.push(file.name); }
    } catch { if (t.state !== 'cancel') { t.state = 'fail'; t.msg = 'The connection was interrupted'; } }
    draw(t);
  }

  // From the user: the app writes the batches to disk (".part"), renamed when complete.
  function receive(reader, from) {
    const info = reader.info;
    const t = { id: info.id, name: info.name, size: info.size || 0, dir: 'down', p: 0, state: 'run' };
    rows.set(t.id, t); draw(t);
    if (!allowed) { t.state = 'fail'; t.msg = 'File transfer is not allowed in this session'; draw(t); }
    if ($('.tbar [data-p=files]') && !document.querySelector('#p-files.on')) app.snack(`Receiving ${t.name} from the user…`);
    return xfer.receive(reader, { max: allowed ? maxBytes : 1, topic: 'file', from: from?.identity || '' });
  }

  // The app's answers for incoming files.
  app.on('xferProgress', m => { const t = rows.get(m.id); if (!t) return; t.p = t.size ? m.n / t.size : 0; draw(t); });
  app.on('xferSaved', m => { const t = rows.get(m.id); if (!t) return; if (m.path) { t.state = 'done'; t.p = 1; t.path = m.path; } else if (t.state !== 'cancel') { t.state = m.reason === 'big' ? 'big' : 'fail'; t.msg = m.reason === 'big' ? `Too large: the limit is ${lim(maxBytes)} per file` : 'The connection was interrupted'; } draw(t); });
  // The user cancelled or the other side refused.
  function onPeer(m) {
    xfer.onPeer(m);
    const t = rows.get(m.id); if (!t) return;
    if (m.op === 'cancel' && t.state === 'run') { t.state = 'cancel'; t.retry = null; }
    if (m.op === 'fail' && t.state === 'run') { t.state = m.msg === 'big' ? 'big' : 'fail'; t.msg = m.msg === 'big' ? `Too large: the limit is ${lim(maxBytes)} per file` : m.msg === 'notallowed' ? 'File transfer is not allowed in this session' : 'The connection was interrupted'; }
    draw(t);
  }
  // While reconnecting, running rows show "waiting for the connection"; they fail if the session ends.
  function paused(on) { rows.forEach(t => { if (t.state === 'run' && on) { t.state = 'wait'; draw(t); } else if (t.state === 'wait' && !on) { t.state = 'fail'; t.msg = 'The connection was interrupted'; draw(t); } }); }
  function ended() { rows.forEach(t => { if (t.state === 'run' || t.state === 'wait') { if (t.dir === 'up') xfer.cancelSend(t.id); t.state = 'fail'; t.msg = 'The session ended'; t.retry = null; draw(t); } }); }

  function setup(info, prefs) {
    maxBytes = info.maxFileBytes || maxBytes;
    if (prefs?.saveDir) saveDir = prefs.saveDir;
    allowed = (info.permissions || '').split(',').includes('files');
    note();
  }
  function setAllowed(list) { allowed = (list || '').split(',').includes('files'); note(); }
  function note() {
    $('#fnote').innerHTML = allowed ? `${IC('info')}Files go to the user's Downloads; theirs arrive in ${esc(saveDir)}` : `${IC('lock')}The user did not allow file transfer in this session`;
    $('#drop').style.opacity = allowed ? '' : '.5';
  }
  const sideNote = () => `Both ways · streaming · up to ${lim(maxBytes)}`;

  $('#p-files').insertAdjacentHTML('afterbegin', '<div class="note" id="fnote"></div>');
  const pick = list => [...list].forEach(sendOne);
  $('#pick').onchange = e => { pick(e.target.files); e.target.value = ''; };
  const drop = $('#drop');
  ['dragenter', 'dragover'].forEach(t => drop.addEventListener(t, e => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach(t => drop.addEventListener(t, e => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', e => pick(e.dataTransfer.files));

  window.viewerFiles = { sendOne, pick, receive, onPeer, paused, ended, setup, setAllowed, sideNote, sent: [] };
  (window.__qa = window.__qa || {}).fileRow = t => { rows.set(t.id, t); draw(t); }; // screenshots/QA: draw a row without LiveKit
})();
