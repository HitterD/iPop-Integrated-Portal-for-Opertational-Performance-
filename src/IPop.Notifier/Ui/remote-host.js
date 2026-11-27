// The user's PC: hidden page that shares the screen through LiveKit and passes the helper's data to the app.
// 1.8 v3: rejoins with a fresh token when the app asks (network drop, sleep, crash); files stream both ways in
// batches (remote-xfer.js) — never a whole file in memory.
(function () {
  const R = window.remote, { post } = window.app;
  window.addEventListener('error', e => post({ type: 'log', text: 'error: ' + (e.message || e) }));
  window.addEventListener('unhandledrejection', e => post({ type: 'log', text: 'rejection: ' + (e.reason && e.reason.message || e.reason) }));

  const joined = () => { const s = R.screenPub?.track?.mediaStreamTrack?.getSettings(); post({ type: 'joined', w: s?.width, h: s?.height, fps: s?.frameRate }); };
  app.on('join', async m => {
    try { R.screen = m.screen; await R.join(m.info, m.quality, m.screen); joined(); }
    catch (e) { post({ type: 'failed', message: String(e && (e.name + ': ' + e.message) || e) }); }
  });
  app.on('rejoin', async m => {
    try { R.screen = m.screen || R.screen; await R.rejoin(m.info, R.screen); joined(); }
    catch (e) { post({ type: 'rejoinFailed', message: String(e && e.message || e) }); }
  });
  app.on('noop', () => { });
  app.on('quality', async m => { try { await R.publishScreen(m.preset || 'auto'); } catch (e) { post({ type: 'log', text: 'quality ' + e.message }); } });
  // Another monitor (or All) picked, or the displays changed: re-publish with the new crop.
  app.on('screen', async m => { R.screen = m.screen; if (!R.room) return; try { await R.publishScreen(R.preset || 'auto', m.screen); } catch (e) { post({ type: 'log', text: 'screen ' + e.message }); } });
  app.on('leave', async () => { await R.leave(); post({ type: 'left' }); });
  app.on('send', m => {
    if (m.topic === 'clip' && m.msg) return R.sendClip(m.msg.kind, m.msg.text, m.msg.data).catch(e => post({ type: 'log', text: 'clip send ' + e.message }));
    R.sendReliable(m.msg, m.topic || 'ctl');
  });

  // A file on this PC going to ICT: the app serves it from disk by token; we stream it into LiveKit.
  app.on('sendUrl', async m => {
    const out = (state, p) => post({ type: 'xferOut', id: m.id, state, p, token: m.token });
    let last = 0;
    try {
      const r = await xfer.send({ url: m.url, name: m.name, size: m.size }, { id: m.id, topic: m.topic || 'file',
        onProgress: (sent, size) => { const now = performance.now(); if (now - last > 250) { last = now; out('run', size ? sent / size : 0); } } });
      out(r === 'cancel' ? 'cancel' : 'done', 1);
    } catch (e) { post({ type: 'log', text: 'send file ' + (e && e.message) }); out('fail', 0); }
  });
  app.on('xferCancel', m => { xfer.cancelSend(m.id); if (m.reason === 'cancel') xfer.cancelReceive(m.id); else R.sendReliable({ t: 'xfer', id: m.id, op: 'fail', msg: m.reason }, 'xfer'); });

  R.onData = (m, topic) => { if (topic === 'xfer') return xfer.onPeer(m); post({ type: 'data', topic, msg: m }); };
  R.onPeer = (p, joined) => post({ type: 'peer', identity: p.identity, name: p.name, joined });
  R.onDisconnected = reason => post({ type: 'disconnected', reason: String(reason ?? '') });
  R.onReconnecting = on => post({ type: 'reconnecting', on });
  // Files from ICT: streamed to the app in batches; it writes them to disk and enforces the limit again.
  const receive = topic => (reader, from) => xfer.receive(reader, { max: R.maxFileBytes || 0, topic, from: from?.identity || '' });
  R.onFile = receive('file');
  R.onClipFile = receive('clip-file');
  R.onClipData = async reader => {
    const bytes = await R.readAll(reader);
    const isText = (reader.info.mimeType || '').startsWith('text/');
    post({ type: 'data', topic: 'clip', msg: isText ? { kind: 'text', text: new TextDecoder().decode(bytes) } : { kind: 'image', data: R.b64(bytes) } });
  };
  app.on('limits', m => { R.maxFileBytes = m.maxFileBytes || 0; });

  app.ready();
})();
