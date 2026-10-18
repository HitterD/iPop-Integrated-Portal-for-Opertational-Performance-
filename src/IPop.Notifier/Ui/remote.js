// Remote help over LiveKit (docs/mockups/notifier/remote.html). Two roles share this file:
//   host   — the user's PC: hidden window, publishes the screen (H.264, 60 fps) and plays the helper's input
//   helper — the ICT viewer: shows the stream, sends mouse/keyboard, clipboard, files, chat and media commands
// Everything travels peer to peer through the company LiveKit; iPop keeps only the session log. Chat is not stored.
(function () {
  const LK = window.LivekitClient;
  const { post } = window.app;
  const ENC = new TextEncoder(), DEC = new TextDecoder();
  const R = window.remote = { room: null, role: null, mode: 'control', helper: '' };

  // Input travels on a lossy channel (latest wins, no head-of-line blocking); everything else is reliable.
  const sendLossy = obj => R.room?.localParticipant.publishData(ENC.encode(JSON.stringify(obj)), { reliable: false, topic: 'in' });
  const sendReliable = (obj, topic = 'ctl') => R.room?.localParticipant.publishData(ENC.encode(JSON.stringify(obj)), { reliable: true, topic });
  R.sendReliable = sendReliable;

  R.join = async (info, quality) => {
    R.role = info.role; R.mode = info.mode; R.helper = info.helper || '';
    const room = R.room = new LK.Room({
      adaptiveStream: false, dynacast: false,
      publishDefaults: { videoCodec: 'h264', backupCodec: false, simulcast: false, degradationPreference: 'maintain-framerate' },
    });
    room.on(LK.RoomEvent.DataReceived, (payload, participant, kind, topic) => {
      let m; try { m = JSON.parse(DEC.decode(payload)); } catch { return; }
      (R.onData || (() => { }))(m, topic, participant);
    });
    room.on(LK.RoomEvent.Disconnected, reason => (R.onDisconnected || (() => { }))(reason));
    room.on(LK.RoomEvent.Reconnecting, () => (R.onReconnecting || (() => { }))(true));
    room.on(LK.RoomEvent.Reconnected, () => (R.onReconnecting || (() => { }))(false));
    room.on(LK.RoomEvent.ParticipantConnected, p => (R.onPeer || (() => { }))(p, true));
    room.on(LK.RoomEvent.ParticipantDisconnected, p => (R.onPeer || (() => { }))(p, false));
    room.on(LK.RoomEvent.TrackSubscribed, (track, pub, p) => (R.onTrack || (() => { }))(track, p));
    room.registerByteStreamHandler('file', async (reader, from) => (R.onFile || (() => { }))(reader, from));
    room.registerByteStreamHandler('clip-file', async (reader, from) => (R.onClipFile || (() => { }))(reader, from));
    await room.connect(info.url, info.token, { autoSubscribe: true });
    if (R.role === 'host') await R.publishScreen(quality || 'smooth');
    return room;
  };

  // ── host: screen ───────────────────────────────────────────────────────
  // The app answers getDisplayMedia itself (WebView2 picks the monitor, no picker), so no Chrome prompt appears.
  const PRESETS = {
    smooth: { maxW: 1920, maxH: 1080, fps: 60, bitrate: 8_000_000 },
    quality: { maxW: 2560, maxH: 1440, fps: 30, bitrate: 12_000_000 },
  };
  R.publishScreen = async preset => {
    const p = PRESETS[preset] || PRESETS.smooth;
    if (R.screenPub) { await R.room.localParticipant.unpublishTrack(R.screenPub.track); R.screenPub = null; }
    const stream = await navigator.mediaDevices.getDisplayMedia({
      video: { frameRate: { ideal: p.fps, max: p.fps }, width: { max: p.maxW }, height: { max: p.maxH }, cursor: 'always' },
      audio: false,
    });
    const mst = stream.getVideoTracks()[0];
    try { mst.contentHint = preset === 'quality' ? 'detail' : 'motion'; } catch { }
    const track = new LK.LocalVideoTrack(mst, undefined, false);
    R.screenPub = await R.room.localParticipant.publishTrack(track, {
      source: LK.Track.Source.ScreenShare, videoCodec: 'h264', simulcast: false,
      screenShareEncoding: { maxBitrate: p.bitrate, maxFramerate: p.fps },
      videoEncoding: { maxBitrate: p.bitrate, maxFramerate: p.fps },
      degradationPreference: preset === 'quality' ? 'maintain-resolution' : 'maintain-framerate',
    });
    const s = mst.getSettings();
    sendReliable({ t: 'screen', w: s.width, h: s.height, fps: s.frameRate, preset });
    return s;
  };

  // ── helper: stats (fps + round trip) ────────────────────────────────────
  // Stats from the subscribed screen track itself (public API; no reliance on LiveKit internals).
  let lastFrames = null;
  R.stats = async () => {
    const out = { fps: 0, rtt: 0, w: 0, h: 0, codec: '' };
    const room = R.room; if (!room) return out;
    let track = null;
    room.remoteParticipants.forEach(p => p.trackPublications.forEach(pub => { if (pub.track && pub.kind === 'video') track = pub.track; }));
    if (!track) return out;
    const rep = await track.getRTCStatsReport();
    if (!rep) return out;
    rep.forEach(r => {
      if (r.type === 'inbound-rtp' && r.kind === 'video') {
        out.w = r.frameWidth || 0; out.h = r.frameHeight || 0; out.codecId = r.codecId;
        // framesPerSecond is sometimes missing; derive it from framesDecoded between calls.
        if (r.framesPerSecond) out.fps = Math.round(r.framesPerSecond);
        else if (lastFrames && r.timestamp > lastFrames.t) out.fps = Math.round((r.framesDecoded - lastFrames.n) * 1000 / (r.timestamp - lastFrames.t));
        lastFrames = { n: r.framesDecoded, t: r.timestamp };
      }
      if (r.type === 'candidate-pair' && r.nominated && r.currentRoundTripTime != null) out.rtt = Math.round(r.currentRoundTripTime * 1000);
      if (r.type === 'remote-candidate' && !out.via) out.via = r.candidateType;
    });
    if (out.codecId) { const c = rep.get(out.codecId); out.codec = (c?.mimeType || '').split('/')[1] || ''; }
    return out;
  };

  R.input = obj => sendLossy(obj);
  R.inputReliable = obj => sendReliable(obj, 'in');

  // Files: LiveKit byte streams (chunked, with progress); the receiving app saves them to Downloads.
  R.sendFile = async (file, topic = 'file', onProgress) => {
    const writer = await R.room.localParticipant.streamBytes({ name: file.name, totalSize: file.size, mimeType: file.type || 'application/octet-stream', topic });
    const chunk = 64 * 1024; let sent = 0;
    for (let off = 0; off < file.size; off += chunk) {
      const buf = new Uint8Array(await file.slice(off, off + chunk).arrayBuffer());
      await writer.write(buf); sent += buf.length; onProgress && onProgress(sent / file.size);
    }
    await writer.close();
  };

  R.readAll = async reader => {
    const parts = []; let n = 0;
    for await (const c of reader) { parts.push(c); n += c.length; R.onReadProgress && R.onReadProgress(reader.info, n); }
    const out = new Uint8Array(n); let o = 0; for (const p of parts) { out.set(p, o); o += p.length; }
    return out;
  };

  R.leave = async () => { try { await R.room?.disconnect(); } catch { } R.room = null; };
  R.b64 = bytes => { let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); return btoa(s); };
})();
