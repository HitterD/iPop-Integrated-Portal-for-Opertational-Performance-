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

  // Quality "Auto" (1.8.0): the host publishes simulcast layers and LiveKit picks one per viewer (dynacast on the
  // host, adaptive stream on the viewer), so a slow network gets a lighter picture instead of stalls.
  R.join = async (info, quality, screen) => {
    R.role = info.role; R.mode = info.mode; R.helper = info.helper || '';
    const room = R.room = new LK.Room({
      adaptiveStream: info.role !== 'host', dynacast: info.role === 'host',
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
    room.registerByteStreamHandler('clip-data', async (reader, from) => (R.onClipData || (() => { }))(reader, from));
    await room.connect(info.url, info.token, { autoSubscribe: true });
    if (R.role === 'host') await R.publishScreen(quality || 'auto', screen);
    return room;
  };

  // ── host: screen ───────────────────────────────────────────────────────
  // One monitor: getDisplayMedia, answered by the app itself (no picker). Several monitors (1.8.0): the whole desktop
  // through getUserMedia('screen'), cropped frame by frame to the monitor the helper picked (screen.crop; none = All).
  const PRESETS = {
    auto: { maxW: 1920, maxH: 1080, fps: 30, bitrate: 6_000_000, simulcast: true, degrade: 'balanced', hint: 'detail' },
    smooth: { maxW: 1920, maxH: 1080, fps: 60, bitrate: 8_000_000, degrade: 'maintain-framerate', hint: 'motion' },
    quality: { maxW: 2560, maxH: 1440, fps: 30, bitrate: 12_000_000, degrade: 'maintain-resolution', hint: 'detail' },
  };
  async function capture(p, screen) {
    if (!screen || !screen.multi) {
      const stream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: p.fps, max: p.fps }, width: { max: p.maxW }, height: { max: p.maxH }, cursor: 'always' }, audio: false,
      });
      return stream.getVideoTracks()[0];
    }
    const v = screen.virtual;
    const desk = (await navigator.mediaDevices.getUserMedia({ audio: false, video: { mandatory: {
      chromeMediaSource: 'screen', minWidth: v.w, maxWidth: v.w, minHeight: v.h, maxHeight: v.h, maxFrameRate: p.fps } } })).getVideoTracks()[0];
    const c = screen.crop;
    if (!c) return desk;
    const gen = new MediaStreamTrackGenerator({ kind: 'video' });
    const rect = { x: c.x & ~1, y: c.y & ~1, width: c.w & ~1, height: c.h & ~1 };
    new MediaStreamTrackProcessor({ track: desk }).readable.pipeThrough(new TransformStream({
      transform(frame, out) {
        try { out.enqueue(new VideoFrame(frame, { visibleRect: rect })); } catch { }
        frame.close();
      },
    })).pipeTo(gen.writable).catch(() => { });
    gen.addEventListener('ended', () => desk.stop());
    R.desk = desk;
    return gen;
  }
  R.capture = capture; // QA: the capture path alone (multi-monitor crop) without a LiveKit room
  R.publishScreen = async (preset, screen) => {
    const p = PRESETS[preset] || PRESETS.auto;
    R.screen = screen || R.screen;
    if (R.screenPub) { await R.room.localParticipant.unpublishTrack(R.screenPub.track); R.screenPub = null; try { R.desk?.stop(); } catch { } }
    const mst = await capture(p, R.screen);
    try { mst.contentHint = p.hint; } catch { }
    const track = new LK.LocalVideoTrack(mst, undefined, false);
    R.screenPub = await R.room.localParticipant.publishTrack(track, {
      source: LK.Track.Source.ScreenShare, videoCodec: 'h264', simulcast: !!p.simulcast,
      screenShareSimulcastLayers: p.simulcast ? [LK.ScreenSharePresets.h720fps15] : undefined,
      screenShareEncoding: { maxBitrate: p.bitrate, maxFramerate: p.fps },
      videoEncoding: { maxBitrate: p.bitrate, maxFramerate: p.fps },
      degradationPreference: p.degrade,
    });
    const s = mst.getSettings();
    const c = R.screen?.crop;
    sendReliable({ t: 'screen', w: c ? c.w : s.width, h: c ? c.h : s.height, fps: s.frameRate, preset });
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

  // Clipboard: small text as one message; anything bigger (pictures, long text) as a byte stream (no 64 KB limit).
  R.sendClip = async (kind, text, b64) => {
    if (kind === 'text' && text && text.length < 30000) return sendReliable({ t: 'clip', kind: 'text', text }, 'clip');
    const bytes = kind === 'text' ? ENC.encode(text || '') : Uint8Array.from(atob(b64 || ''), c => c.charCodeAt(0));
    const w = await R.room.localParticipant.streamBytes({ name: kind === 'text' ? 'clip.txt' : 'clip.png', totalSize: bytes.length,
      mimeType: kind === 'text' ? 'text/plain' : 'image/png', topic: 'clip-data' });
    for (let o = 0; o < bytes.length; o += 60000) await w.write(bytes.subarray(o, o + 60000));
    await w.close();
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
