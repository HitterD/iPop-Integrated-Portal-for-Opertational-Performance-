(function () {
  const R = window.remote, { post } = window.app;
  window.addEventListener('error', e => post({ type: 'log', text: 'error: ' + (e.message || e) }));
  window.addEventListener('unhandledrejection', e => post({ type: 'log', text: 'rejection: ' + (e.reason && e.reason.message || e.reason) }));
  let stats = null;

  app.on('join', async m => {
    try {
      const s = await (await R.join(m.info, m.quality)) && R.screenPub?.track?.mediaStreamTrack?.getSettings();
      post({ type: 'joined', w: s?.width, h: s?.height, fps: s?.frameRate });
    } catch (e) { post({ type: 'failed', message: String(e && (e.name + ': ' + e.message) || e) }); }
  });
  app.on('quality', async m => { try { await R.publishScreen(m.preset); } catch { } });
  app.on('leave', async () => { await R.leave(); post({ type: 'left' }); });
  app.on('send', m => {
    if (m.topic === 'clip' && m.msg) return R.sendClip(m.msg.kind, m.msg.text, m.msg.data).catch(e => post({ type: 'log', text: 'clip send ' + e.message }));
    R.sendReliable(m.msg, m.topic || 'ctl');
  });
  app.on('sendFile', async m => {
    // A file the user picked (clipboard copy or Send file): bytes arrive from the app as base64.
    const bin = Uint8Array.from(atob(m.data), c => c.charCodeAt(0));
    const f = new File([bin], m.name, { type: m.mime || 'application/octet-stream' });
    await R.sendFile(f, m.topic || 'file', p => post({ type: 'fileProgress', id: m.id, p }));
    post({ type: 'fileSent', id: m.id, name: m.name });
  });

  R.onData = (m, topic) => post({ type: 'data', topic, msg: m });
  R.onPeer = (p, joined) => post({ type: 'peer', identity: p.identity, name: p.name, joined });
  R.onDisconnected = reason => post({ type: 'disconnected', reason: String(reason ?? '') });
  R.onReconnecting = on => post({ type: 'reconnecting', on });
  const receive = topic => async (reader, from) => {
    const bytes = await R.readAll(reader);
    post({ type: 'file', topic, name: reader.info.name, mime: reader.info.mimeType, from: from?.identity || '', data: R.b64(bytes) });
  };
  R.onFile = receive('file');
  R.onClipFile = receive('clip-file');
  R.onClipData = async reader => {
    const bytes = await R.readAll(reader);
    const isText = (reader.info.mimeType || '').startsWith('text/');
    post({ type: 'data', topic: 'clip', msg: isText ? { kind: 'text', text: new TextDecoder().decode(bytes) } : { kind: 'image', data: R.b64(bytes) } });
  };

  app.ready();
})();
