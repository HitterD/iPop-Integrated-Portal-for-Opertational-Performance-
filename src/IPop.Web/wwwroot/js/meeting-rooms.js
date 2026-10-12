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
  // Server chat is text-only. Whitelist fields before crossing the .NET interop boundary.
  function parseChatPayload(payload) {
    if (!ArrayBuffer.isView(payload) || payload.byteLength > 16_384) return null;
    try {
      const m = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(payload));
      const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
      if (!m || Array.isArray(m) || typeof m.id !== 'string' || typeof m.userId !== 'string' || !guid.test(m.id) || !guid.test(m.userId) ||
          typeof m.name !== 'string' || m.name.length > 256 ||
          typeof m.text !== 'string' || !m.text.trim() || m.text.length > 2000 ||
          typeof m.sentAt !== 'string' || m.sentAt.length > 64 ||
          !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(m.sentAt) || !Number.isFinite(Date.parse(m.sentAt)) ||
          !['text', 'system'].includes(m.kind)) return null;
      return { id: m.id, userId: m.userId, name: m.name, text: m.text, sentAt: m.sentAt, kind: m.kind };
    } catch { return null; }
  }

  function cameraFacing(device) {
    if (['user', 'environment'].includes(device?.facingMode)) return device.facingMode;
    const label = device?.label || '';
    if (/\b(front|user)\b/i.test(label)) return 'user';
    if (/\b(back|rear|environment)\b/i.test(label)) return 'environment';
    return '';
  }

  function nextCamera(devices, currentId, currentFacing) {
    const unique = [...new Map(devices.filter(d => d.kind === 'videoinput' && d.deviceId).map(d => [d.deviceId, d])).values()];
    const index = unique.findIndex(d => d.deviceId === currentId);
    const facing = currentFacing || cameraFacing(unique[index]);
    if (unique.some(d => cameraFacing(d))) {
      if (!facing) return null; // Do not choose another lens when we cannot identify the current facing.
      const opposite = facing === 'user' ? 'environment' : 'user';
      return unique.find(d => d.deviceId !== currentId && cameraFacing(d) === opposite) || null;
    }
    return unique.find((d, i) => i === (index + 1) % unique.length && d.deviceId !== currentId) || null;
  }
  if (typeof module !== 'undefined') { module.exports = { tileLayout, parseChatPayload, nextCamera }; return; }

  const LK = () => window.LivekitClient;
  let room = null, ref = null, opts = null, rec = null, peopleTimer = 0, lastVideoAllowed = null, micChanging = false, session = 0;
  let shareChanging = null, cameraChanging = null;
  // navigator.mediaDevices 'devicechange' → .NET OnDevicesChanged (Flip shows only with >1 camera). One listener per room.
  let deviceWatch = null;
  function watchDevices(dotnetRef, isCurrent) {
    unwatchDevices();
    const md = typeof navigator !== 'undefined' && navigator.mediaDevices;
    if (!md?.addEventListener) return;
    let timer = 0;
    const handler = () => {
      clearTimeout(timer);
      timer = setTimeout(() => { if (isCurrent()) dotnetRef.invokeMethodAsync('OnDevicesChanged').catch(() => {}); }, 300);
    };
    md.addEventListener('devicechange', handler);
    deviceWatch = () => { clearTimeout(timer); md.removeEventListener('devicechange', handler); };
  }
  function unwatchDevices() { if (deviceWatch) { deviceWatch(); deviceWatch = null; } }
  const remoteAudio = new Map();

  function detachAudio(track) {
    track.detach().forEach(x => { if (x.dataset.mrAudio) { x.srcObject = null; x.remove(); } });
    remoteAudio.delete(track);
  }

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
    t.classList.toggle('local', !!isLocal);
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
      videoCaptureDefaults: { resolution: L.VideoPresets.h720.resolution },
      publishDefaults: { videoEncoding: L.VideoPresets.h720.encoding, simulcast: true,
        videoSimulcastLayers: [L.VideoPresets.h180, L.VideoPresets.h360] },
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
      .on(E.TrackSubscribed, (track) => {
        if (track.kind === 'audio' && !remoteAudio.has(track)) {
          const a = track.attach(); a.dataset.mrAudio = '1'; remoteAudio.set(track, a);
          document.body.appendChild(a);
          // Explicit play covers both microphone and ScreenShareAudio; autoplay policies still apply.
          a.play()?.catch(() => {});
        }
        changed();
      })
      .on(E.TrackUnsubscribed, (track) => { detachAudio(track); changed(); })
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
      .on(E.DataReceived, async (payload, participant, _kind, topic) => {
        if (topic === 'ipop.chat') {
          // RoomService.SendData has no participant; client publishData must not forge persisted chat.
          if (participant !== undefined) return;
          const chat = parseChatPayload(payload);
          if (chat) ref.invokeMethodAsync('OnRoomChat', JSON.stringify(chat)).catch(() => {});
          return;
        }
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
    watchDevices(ref, () => room === current && ticket === session);
    changed();
    return true;
  }

  async function teardown(r, disconnect) {
    if (!r || room !== r) return;
    ++session;
    clearTimeout(peopleTimer);
    unwatchDevices();
    if (rec) stopPcRecording();
    const oldRef = ref, oldOpts = opts;
    room = null; ref = null; opts = null; lastVideoAllowed = null;
    shareChanging = null; cameraChanging = null;
    [...remoteAudio.keys()].forEach(detachAudio);
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

  // enumerateDevices does not prompt or acquire temporary tracks (unlike SDK getLocalDevices).
  async function listCameras() {
    const current = room;
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      if (room !== current) return 0;
      return devices.filter(d => d.kind === 'videoinput').length;
    } catch { return 0; }
  }

  async function flipCamera() {
    const r = room, failure = { success: false };
    const publication = r?.localParticipant.getTrackPublication(LK().Track.Source.Camera);
    const track = publication?.track;
    const valid = () => room === r && canCamera() && r.localParticipant.isCameraEnabled &&
      !publication.isMuted && r.localParticipant.getTrackPublication(LK().Track.Source.Camera)?.track === track;
    if (!r || !track || !valid() || cameraChanging === r) return failure;
    cameraChanging = r;
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      if (!valid()) return failure;
      const settings = track.mediaStreamTrack.getSettings();
      const cameras = devices.filter(d => d.kind === 'videoinput');
      const capabilityFacing = track.mediaStreamTrack.getCapabilities?.().facingMode;
      const currentFacing = ['user', 'environment'].includes(settings.facingMode) ? settings.facingMode :
        (capabilityFacing?.length === 1 ? capabilityFacing[0] : '');
      const next = nextCamera(cameras, settings.deviceId || r.getActiveDevice('videoinput'), currentFacing);
      let success, facingMode;
      if (next) success = await r.switchActiveDevice('videoinput', next.deviceId);
      else if (cameras.length > 1 && cameras.every(d => !d.label) && /Android|iPhone|iPad|Mobile/i.test(navigator.userAgent)) {
        facingMode = (currentFacing || r.options.videoCaptureDefaults.facingMode) === 'environment' ? 'user' : 'environment';
        // Do not carry an exact deviceId into the facingMode fallback.
        await track.restartTrack({ facingMode, resolution: LK().VideoPresets.h720.resolution });
        success = true;
      } else return failure;
      if (room !== r) { track.stop(); return failure; }
      if (!valid()) { await r.localParticipant.setCameraEnabled(false).catch(() => {}); return failure; }
      if (!success) return failure;
      // Defaults belong to this room only, and must survive camera mute/unmute or recapture.
      if (next) {
        r.options.videoCaptureDefaults.deviceId = { exact: next.deviceId };
        delete r.options.videoCaptureDefaults.facingMode;
      } else {
        delete r.options.videoCaptureDefaults.deviceId;
        r.options.videoCaptureDefaults.facingMode = facingMode;
      }
      changed();
      const actual = track.mediaStreamTrack.getSettings();
      return { success: true, deviceId: r.getActiveDevice('videoinput') || actual.deviceId || '',
        facingMode: facingMode || actual.facingMode || '', label: next?.label || '' };
    } catch {
      if (room !== r) track.stop();
      return failure;
    } finally { if (cameraChanging === r) cameraChanging = null; }
  }

  async function setShare(on, takeover) {
    const r = room;
    if (!r || shareChanging === r) return 'denied';
    shareChanging = r;
    let tracks = [];
    const discard = async () => {
      for (const track of tracks) {
        await r.localParticipant.unpublishTrack(track).catch(() => {});
        track.stop();
      }
    };
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
      const L = LK();
      if (on && !sharing(r.localParticipant)) {
        const preset = new L.VideoPreset(1920, 1080, 6_000_000, 60);
        // SDK 2.22.3 drops top-level suppressLocalAudioPlayback in its display constraints adapter.
        // Its audio object passes through unchanged, so request the browser constraint there too.
        tracks = await r.localParticipant.createScreenTracks({ audio: { suppressLocalAudioPlayback: true },
          suppressLocalAudioPlayback: true, systemAudio: 'include', selfBrowserSurface: 'exclude',
          surfaceSwitching: 'include', resolution: preset.resolution, contentHint: 'detail' });
        if (room !== r) { await discard(); return 'denied'; }
        if (!takeover && otherSharer()) { await discard(); changed(); return 'busy'; }
        const video = tracks.find(t => t.source === L.Track.Source.ScreenShare);
        // SDK ends each publication separately; explicitly stop paired audio if only video ends.
        video?.mediaStreamTrack.addEventListener('ended', () => {
          const audio = tracks.find(t => t.source === L.Track.Source.ScreenShareAudio);
          if (audio) {
            audio.stop();
            r.localParticipant.unpublishTrack(audio).catch(() => {});
          }
        }, { once: true });
        const publish = { screenShareEncoding: preset.encoding, simulcast: true, videoCodec: 'vp8',
          screenShareSimulcastLayers: [L.ScreenSharePresets.h720fps30], degradationPreference: 'maintain-resolution' };
        for (const track of tracks) {
          await r.localParticipant.publishTrack(track, publish);
          if (room !== r) { await discard(); return 'denied'; }
        }
      } else if (!on) await r.localParticipant.setScreenShareEnabled(false);
      if (room !== r) return 'denied';
      if (on && !takeover && otherSharer()) {
        await r.localParticipant.setScreenShareEnabled(false);
        if (room !== r) return 'denied';
        changed(); return 'busy';
      }
      if (on && tracks.length) {
        const audio = r.localParticipant.getTrackPublication(L.Track.Source.ScreenShareAudio);
        if (!audio?.track || audio.track.mediaStreamTrack?.readyState === 'ended')
          ref.invokeMethodAsync('OnRoomNotice', 'share_no_audio', null).catch(() => {});
      }
    } catch { await discard(); return 'denied'; }
    finally { if (shareChanging === r) shareChanging = null; }
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

  // Rooms tab deep link (/calls?tab=rooms&recording={id}): bring that recording row into view.
  function reveal(id) {
    const el = document.getElementById(id);
    if (!el) return;
    const smooth = !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    el.scrollIntoView({ block: 'center', behavior: smooth ? 'smooth' : 'auto' });
  }

  // Recordings: save a file from its ticket URL (the API answers with Content-Disposition: attachment).
  function download(url) {
    const a = document.createElement('a');
    a.href = url; a.download = ''; a.rel = 'noopener'; a.style.display = 'none';
    document.body.appendChild(a); a.click(); a.remove();
  }

  window.ipopRooms = { join, leave, setMic, setCamera, listCameras, flipCamera, setShare, startPcRecording, stopPcRecording, setRecordingAttr, tileLayout, reveal, download };
})();
