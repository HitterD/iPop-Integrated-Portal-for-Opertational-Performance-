// Calls & Meetings → Meeting Rooms: LiveKit media for the five SJA Hall rooms (camera off on join, one screen
// share at a time, record to this PC). The .NET side (MeetingRoomState) owns the UI; this file owns tracks.
(function () {
  function tileLayout(count, width, height) {
    if (count <= 1) return { cols: 1, rows: 1, w: width, h: height };
    let best = null;
    for (let cols = 1; cols <= count; cols++) {
      const rows = Math.ceil(count / cols);
      const w = Math.floor(width / cols), h = Math.floor(height / rows);
      const tile = Math.min(w, h * 16 / 9);
      if (!best || tile > best.tile) best = { cols, rows, w, h, tile };
    }
    return { cols: best.cols, rows: best.rows, w: best.w, h: best.h };
  }
  if (typeof module !== 'undefined') { module.exports = { tileLayout }; return; }

  const LK = () => window.LivekitClient;
  let room = null, ref = null, opts = null, rec = null, peopleTimer = 0, lastVideoAllowed = null, micChanging = false, session = 0;

  function resolveUrl(url) {
    // Same rule as livekit-call.js: on https use the page host (the proxy forwards /rtc).
    if (location.protocol === 'https:') return `wss://${location.host}`;
    return url;
  }

  function el(id) { return document.getElementById(id); }

  function tileFor(identity, name, isLocal) {
    const strip = el(opts.stripId); if (!strip) return null;
    let t = strip.querySelector(`[data-identity="${CSS.escape(identity)}"]`);
    if (!t) {
      t = document.createElement('div');
      t.className = 'mr-tile'; t.dataset.identity = identity;
      t.innerHTML = `<div class="mr-tile-av"></div><video autoplay playsinline muted></video><span class="mr-tile-nm"></span>`;
      strip.appendChild(t);
    }
    t.querySelector('.mr-tile-av').textContent = (name || '?').split(/\s+/).map(s => s[0] || '').join('').slice(0, 2).toUpperCase();
    t.querySelector('.mr-tile-nm').textContent = isLocal ? 'You' : name || identity;
    return t;
  }

  function render() {
    if (!room) return;
    const all = [room.localParticipant, ...room.remoteParticipants.values()];
    const strip = el(opts.stripId), stage = el(opts.stageId);
    if (!strip || !stage) return;
    const alive = new Set();
    let sharer = null;
    for (const p of all) {
      if (p.identity.startsWith('EG_')) continue;
      alive.add(p.identity);
      const t = tileFor(p.identity, p.name, p === room.localParticipant);
      const cam = p.getTrackPublication(LK().Track.Source.Camera);
      const v = t.querySelector('video');
      if (cam && cam.track && !cam.isMuted) { if (v.srcObject !== cam.track.mediaStream) cam.track.attach(v); t.classList.add('cam'); }
      else { v.srcObject = null; t.classList.remove('cam'); }
      t.classList.toggle('speaking', p.isSpeaking);
      const share = p.getTrackPublication(LK().Track.Source.ScreenShare);
      if (share && share.track && !share.isMuted) sharer = { p, track: share.track };
    }
    strip.querySelectorAll('.mr-tile').forEach(t => { if (!alive.has(t.dataset.identity)) t.remove(); });
    const sv = stage.querySelector('video.mr-share');
    if (sharer) {
      stage.classList.add('sharing');
      if (sv && sv.srcObject !== sharer.track.mediaStream) sharer.track.attach(sv);
    } else {
      stage.classList.remove('sharing');
      if (sv) sv.srcObject = null;
    }
    stage.classList.toggle('grid', !sharer);
  }

  function pushPeople() {
    if (!room || !ref) return;
    const S = LK().Track.Source;
    const people = [room.localParticipant, ...room.remoteParticipants.values()]
      .filter(p => !p.identity.startsWith('EG_'))
      .map(p => ({
        identity: p.identity, name: p.name || '', speaking: p.isSpeaking,
        micOn: !!p.getTrackPublication(S.Microphone) && !p.getTrackPublication(S.Microphone).isMuted,
        camOn: !!p.getTrackPublication(S.Camera) && !p.getTrackPublication(S.Camera).isMuted,
        sharing: !!p.getTrackPublication(S.ScreenShare) && !p.getTrackPublication(S.ScreenShare).isMuted,
        recPc: (p.attributes || {}).rec === 'pc',
        isLocal: p === room.localParticipant,
      }));
    ref.invokeMethodAsync('OnRoomPeople', people).catch(() => {});
  }

  function changed() {
    render();
    clearTimeout(peopleTimer);
    peopleTimer = setTimeout(pushPeople, 150);
  }

  function canCamera() {
    const perm = room && room.localParticipant.permissions;
    return !!perm?.canPublish && (perm.canPublishSources.length === 0 || perm.canPublishSources.includes(1)); // protobuf TrackSource.CAMERA = 1
  }

  async function join(url, token, dotnetRef, o) {
    await leave();
    const ticket = ++session;
    ref = dotnetRef; opts = o || {};
    const L = LK();
    if (!L?.Room) return false;
    room = new L.Room({
      adaptiveStream: true, dynacast: true,
      videoCaptureDefaults: { resolution: { width: 640, height: 360, frameRate: 15 } },
      publishDefaults: { videoEncoding: { maxBitrate: 450_000, maxFramerate: 15 }, screenShareEncoding: { maxBitrate: 1_500_000, maxFramerate: 10 } },
      audioCaptureDefaults: { deviceId: opts.inputDeviceId || undefined, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    });
    const current = room;
    const E = L.RoomEvent;
    // Ignore events from a disconnected/replaced room, including late async events.
    const events = { on(event, handler) {
      current.on(event, (...args) => { if (room === current) return handler(...args); });
      return events;
    } };
    events.on(E.ParticipantConnected, changed).on(E.ParticipantDisconnected, changed)
      .on(E.TrackSubscribed, (track) => { if (track.kind === 'audio') { const a = track.attach(); a.dataset.mrAudio = '1'; document.body.appendChild(a); } changed(); })
      .on(E.TrackUnsubscribed, (track) => { track.detach().forEach(x => { if (x.dataset.mrAudio) x.remove(); }); changed(); })
      .on(E.TrackMuted, (pub, participant) => {
        if (participant === current.localParticipant && pub.source === L.Track.Source.Microphone && !micChanging)
          ref.invokeMethodAsync('OnRoomNotice', 'muted_by_server', null).catch(() => {});
        changed();
      }).on(E.TrackUnmuted, changed)
      .on(E.LocalTrackPublished, changed).on(E.LocalTrackUnpublished, changed)
      .on(E.ActiveSpeakersChanged, changed)
      .on(E.ParticipantNameChanged, changed)
      .on(E.ParticipantAttributesChanged, changed)
      .on(E.RoomMetadataChanged, (m) => ref.invokeMethodAsync('OnRoomMetadata', m || '{}').catch(() => {}))
      .on(E.ConnectionStateChanged, (s) => { if (['connected', 'reconnecting'].includes(String(s))) ref.invokeMethodAsync('OnRoomState', String(s)).catch(() => {}); })
      .on(E.Disconnected, () => teardown(current, false))
      .on(E.ParticipantPermissionsChanged, async (_prev, p) => {
        if (p !== room.localParticipant) return;
        const allowed = canCamera();
        if (lastVideoAllowed !== null && allowed !== lastVideoAllowed) {
          if (!allowed) await room.localParticipant.setCameraEnabled(false).catch(() => {});
          if (room !== current) return;
          ref.invokeMethodAsync('OnRoomNotice', allowed ? 'video_on' : 'video_off', null).catch(() => {});
        }
        lastVideoAllowed = allowed; changed();
      })
      .on(E.DataReceived, async (payload, participant) => {
        try {
          const msg = JSON.parse(new TextDecoder().decode(payload));
          if (participant && msg.t === 'share-takeover' && msg.target === current.localParticipant.identity) {
            await room.localParticipant.setScreenShareEnabled(false);
            if (room !== current) return;
            ref.invokeMethodAsync('OnRoomNotice', 'share_taken', participant ? participant.name : null).catch(() => {});
            changed();
          }
        } catch { /* ignore */ }
      });
    try {
      await room.connect(resolveUrl(url), token, { autoSubscribe: true });
    } catch (e) {
      console.warn('meeting room connect failed', e);
      if (room === current) await leave();
      else await current.disconnect().catch(() => {});
      return false;
    }
    if (ticket !== session || room !== current) { await current.disconnect().catch(() => {}); return false; }
    lastVideoAllowed = canCamera();
    // Publish an already-muted track: never transmit a moment of audio on join.
    let mic;
    try {
      mic = await L.createLocalAudioTrack(current.options.audioCaptureDefaults);
      await mic.mute();
      if (ticket !== session || room !== current) { mic.stop(); return false; }
      await current.localParticipant.publishTrack(mic);
    } catch { mic?.stop(); /* microphone permission may be denied; joining listen-only still works */ }
    if (ticket !== session || room !== current) return false;
    if (room.metadata) ref.invokeMethodAsync('OnRoomMetadata', room.metadata).catch(() => {});
    changed();
    return true;
  }

  async function teardown(r, disconnect) {
    if (!r || room !== r) return;
    ++session;
    clearTimeout(peopleTimer);
    if (rec) stopPcRecording();
    const oldRef = ref, oldOpts = opts;
    room = null; ref = null; opts = null; lastVideoAllowed = null;
    document.querySelectorAll('[data-mr-audio]').forEach(a => a.remove());
    const strip = oldOpts && el(oldOpts.stripId); if (strip) strip.innerHTML = '';
    const stage = oldOpts && el(oldOpts.stageId);
    if (stage) { stage.classList.remove('sharing', 'grid'); const v = stage.querySelector('video.mr-share'); if (v) v.srcObject = null; }
    if (disconnect) await r.disconnect().catch(() => {});
    oldRef?.invokeMethodAsync('OnRoomState', 'disconnected').catch(() => {});
  }

  async function leave() { await teardown(room, true); }

  async function setMic(on) {
    if (!room) return false;
    const current = room;
    micChanging = true;
    try { await current.localParticipant.setMicrophoneEnabled(on); }
    catch { return false; }
    finally { micChanging = false; }
    if (room !== current) return false;
    changed(); return current.localParticipant.isMicrophoneEnabled;
  }

  async function setCamera(on) {
    if (!room) return false;
    if (on && !canCamera()) return false;
    const current = room;
    try { await current.localParticipant.setCameraEnabled(on); } catch { return false; }
    if (room !== current) return false;
    changed(); return current.localParticipant.isCameraEnabled;
  }

  async function setShare(on, takeover) {
    const r = room;
    if (!r) return 'denied';
    const sharing = p => { const s = p.getTrackPublication(LK().Track.Source.ScreenShare); return !!s && !s.isMuted; };
    const otherSharer = () => [...r.remoteParticipants.values()].find(sharing);
    try {
      if (on) {
        const other = otherSharer();
        if (other && !takeover) return 'busy';
        if (other) {
          const data = new TextEncoder().encode(JSON.stringify({ t: 'share-takeover', target: other.identity }));
          await r.localParticipant.publishData(data, { reliable: true, destinationIdentities: [other.identity] });
          if (room !== r) return 'denied';
          // Allow the previous presenter to stop; bounded even if they never respond.
          for (let waited = 0; waited < 3000 && r.remoteParticipants.has(other.identity) && sharing(other); waited += 100) {
            await new Promise(resolve => setTimeout(resolve, 100));
            if (room !== r) return 'denied';
          }
        }
      }
      await r.localParticipant.setScreenShareEnabled(on, { audio: false, resolution: { width: 1280, height: 720, frameRate: 10 } });
      if (room !== r) return 'denied';
      if (on && !takeover && otherSharer()) {
        await r.localParticipant.setScreenShareEnabled(false);
        if (room !== r) return 'denied';
        changed(); return 'busy';
      }
    } catch { return 'denied'; }
    changed(); return 'ok';
  }

  async function setRecordingAttr(value) {
    if (!room) return;
    try { await room.localParticipant.setAttributes({ rec: value || '' }); } catch { /* old server: indicator only from metadata */ }
  }

  // Record to this PC: draw the stage + tiles onto a canvas, mix every voice, and save a .webm when stopped.
  function startPcRecording() {
    if (!room || rec || typeof MediaRecorder === 'undefined' || typeof AudioContext === 'undefined') return false;
    const recordingRoom = room;
    let ac, stream;
    try {
      const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720;
      const g = canvas.getContext('2d');
      ac = new AudioContext();
      ac.resume().catch(() => {});
      const dest = ac.createMediaStreamDestination();
      const sources = new Map();
      const mix = () => {
        const tracks = [];
        for (const p of [room.localParticipant, ...room.remoteParticipants.values()]) {
          const m = p.getTrackPublication(LK().Track.Source.Microphone);
          if (m && !m.isMuted && m.track && m.track.mediaStreamTrack && m.track.mediaStreamTrack.readyState !== 'ended') tracks.push(m.track.mediaStreamTrack);
        }
        const active = new Set(tracks.map(t => t.id));
        for (const [id, source] of sources) if (!active.has(id)) { source.disconnect(); sources.delete(id); }
        for (const t of tracks) if (!sources.has(t.id)) { const s = ac.createMediaStreamSource(new MediaStream([t])); s.connect(dest); sources.set(t.id, s); }
      };
      mix();
      const draw = () => {
        if (rec !== recording) return;
        mix();
        g.fillStyle = '#141210'; g.fillRect(0, 0, 1280, 720);
        const stage = el(opts.stageId), share = stage && stage.querySelector('video.mr-share');
        const tiles = [...(el(opts.stripId)?.querySelectorAll('.mr-tile') || [])];
        let area = { x: 0, y: 0, w: 1280, h: 720 };
        if (share && share.srcObject && share.videoWidth) {
          g.drawImage(share, 0, 0, 1280, 600);
          area = { x: 0, y: 600, w: 1280, h: 120 };
        }
        const l = area.h === 720 ? tileLayout(tiles.length, area.w, area.h) : { cols: tiles.length || 1, rows: 1, w: Math.floor(area.w / Math.max(1, tiles.length)), h: area.h };
        tiles.forEach((t, i) => {
          const x = area.x + (i % l.cols) * l.w, y = area.y + Math.floor(i / l.cols) * l.h;
          const v = t.querySelector('video');
          g.fillStyle = '#24201d'; g.fillRect(x + 2, y + 2, l.w - 4, l.h - 4);
          if (t.classList.contains('cam') && v.videoWidth) g.drawImage(v, x + 2, y + 2, l.w - 4, l.h - 4);
          else { g.fillStyle = '#e7e5e4'; g.font = `${Math.max(14, Math.floor(l.h / 5))}px Inter, sans-serif`; g.textAlign = 'center'; g.fillText(t.querySelector('.mr-tile-av').textContent, x + l.w / 2, y + l.h / 2); }
          g.fillStyle = 'rgba(0,0,0,.55)'; g.fillRect(x + 6, y + l.h - 26, Math.min(l.w - 12, 160), 20);
          g.fillStyle = '#fff'; g.font = '12px Inter, sans-serif'; g.textAlign = 'left'; g.fillText(t.querySelector('.mr-tile-nm').textContent, x + 12, y + l.h - 12);
        });
        rec.raf = requestAnimationFrame(draw);
      };
      stream = new MediaStream([...canvas.captureStream(15).getVideoTracks(), ...dest.stream.getAudioTracks()]);
      const mime = MediaRecorder.isTypeSupported('video/webm;codecs=vp9,opus') ? 'video/webm;codecs=vp9,opus' : 'video/webm';
      const mr = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 2_000_000 });
      const recording = { mr, raf: 0, name: (room.name || 'meeting').replace(/^hall-/, '') };
      let cleaned = false;
      const clearAttr = () => {
        if (room === recordingRoom && recordingRoom.state === 'connected')
          recordingRoom.localParticipant.setAttributes({ rec: '' }).catch(() => {});
      };
      const cleanup = () => {
        if (cleaned) return;
        cleaned = true;
        cancelAnimationFrame(recording.raf);
        stream.getTracks().forEach(t => t.stop());
        sources.forEach(s => s.disconnect());
        ac.close().catch(() => {});
        if (rec === recording) rec = null;
        clearAttr();
      };
      recording.cleanup = cleanup;
      const chunks = [];
      mr.ondataavailable = (e) => { if (e.data.size) chunks.push(e.data); };
      mr.onstop = () => {
        const blob = new Blob(chunks, { type: 'video/webm' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `${recording.name || 'meeting'} ${new Date().toISOString().slice(0, 16).replace('T', ' ').replace(':', '')}.webm`;
        document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(a.href), 60_000);
        cleanup();
      };
      mr.onerror = cleanup;
      rec = recording;
      mr.start(1000);
      draw();
      setRecordingAttr('pc');
      return true;
    } catch (e) {
      if (rec) cancelAnimationFrame(rec.raf);
      rec = null; stream?.getTracks().forEach(t => t.stop());
      ac?.close().catch(() => {});
      console.warn('meeting room recording unavailable', e);
      return false;
    }
  }

  function stopPcRecording() {
    if (!rec) return;
    const recording = rec;
    cancelAnimationFrame(recording.raf);
    if (recording.mr.state !== 'inactive') recording.mr.stop();
    recording.cleanup();
  }

  window.ipopRooms = { join, leave, setMic, setCamera, setShare, startPcRecording, stopPcRecording, setRecordingAttr, tileLayout };
})();
