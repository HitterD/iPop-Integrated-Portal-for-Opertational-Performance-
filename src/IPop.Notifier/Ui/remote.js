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
    R.maxFileBytes = info.maxFileBytes || R.maxFileBytes || 0;
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
    if (R.role === 'host') await R.publishScreen(quality || R.preset || 'auto', screen);
    return room;
  };

  /// Rejoin after a drop (BIGIS #8 v3): leave whatever is left of the old room and join again with a fresh token
  /// from /join. The screen is published again on the host; the viewer re-attaches the track (onTrack).
  R.rejoin = async (info, screen) => {
    const old = R.room; R.room = null; R.screenPub = null;
    try { await old?.disconnect(); } catch { }
    return R.join(info, R.preset || 'auto', screen || R.screen);
  };

  // ── host: screen ───────────────────────────────────────────────────────
  // getDisplayMedia, answered by the app itself (no picker): the environment auto-selects the first screen
  // ("Entire screen" with one monitor, "Screen 1" with several). 1.8.1: the 1.8.0 whole-desktop path
  // (getUserMedia chromeMediaSource:'screen' + crop) was refused or hung on PCs with two monitors in WebView2 158,
  // so nobody with two monitors could be helped; it is gone.
  const PRESETS = {
    auto: { maxW: 1920, maxH: 1080, fps: 30, bitrate: 6_000_000, simulcast: true, degrade: 'balanced', hint: 'detail' },
    smooth: { maxW: 1920, maxH: 1080, fps: 60, bitrate: 8_000_000, degrade: 'maintain-framerate', hint: 'motion' },
    quality: { maxW: 2560, maxH: 1440, fps: 30, bitrate: 12_000_000, degrade: 'maintain-resolution', hint: 'detail' },
  };
  const CAPTURE_TIMEOUT_MS = 15000;
  async function capture(p) {
    // A capture that never answers (no matching source) must fail, not leave the helper on "Connecting…".
    let timer;
    const stream = await Promise.race([
      navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: p.fps, max: p.fps }, width: { max: p.maxW }, height: { max: p.maxH }, cursor: 'always' }, audio: false,
      }),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('The screen capture did not start within 15 s')), CAPTURE_TIMEOUT_MS); }),
    ]).finally(() => clearTimeout(timer));
    return stream.getVideoTracks()[0];
  }
  R.capture = capture; // QA: the capture path alone without a LiveKit room
  R.publishScreen = async (preset, screen) => {
    const p = PRESETS[preset] || PRESETS.auto; R.preset = preset;
    R.screen = screen || R.screen;
    if (R.screenPub) { await R.room.localParticipant.unpublishTrack(R.screenPub.track); R.screenPub = null; }
    const mst = await capture(p);
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

  R.readAll = async reader => {
    const parts = []; let n = 0;
    for await (const c of reader) { parts.push(c); n += c.length; R.onReadProgress && R.onReadProgress(reader.info, n); }
    const out = new Uint8Array(n); let o = 0; for (const p of parts) { out.set(p, o); o += p.length; }
    return out;
  };

  R.leave = async () => { try { await R.room?.disconnect(); } catch { } R.room = null; };
  R.b64 = bytes => { let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000)); return btoa(s); };
})();
