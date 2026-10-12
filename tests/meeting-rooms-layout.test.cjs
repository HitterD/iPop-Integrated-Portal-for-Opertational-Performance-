const test = require('node:test');
const assert = require('node:assert');
const { tileLayout } = require('../src/IPop.Web/wwwroot/js/meeting-rooms.js');

test('one person fills the stage', () => assert.deepStrictEqual(tileLayout(1, 1280, 720), { cols: 1, rows: 1, w: 1280, h: 720 }));
test('four people make a 2x2 grid', () => assert.deepStrictEqual(tileLayout(4, 1280, 720), { cols: 2, rows: 2, w: 640, h: 360 }));
test('twenty people stay inside the canvas', () => {
  const l = tileLayout(20, 1280, 720);
  assert.ok(l.cols * l.rows >= 20);
  assert.ok(l.cols * l.w <= 1280 && l.rows * l.h <= 720);
});

const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(require.resolve('../src/IPop.Web/wwwroot/js/meeting-rooms.js'), 'utf8');
const sdk = require('../src/IPop.Web/wwwroot/js/livekit-client.umd.js');
function harness(protocol = 'https:', extras = {}) {
  const callbacks = [], rooms = [], steps = [], timers = [];
  const S = { Camera: 'camera', Microphone: 'microphone', ScreenShare: 'screen_share' };
  const E = Object.fromEntries(['ParticipantConnected', 'ParticipantDisconnected', 'TrackSubscribed', 'TrackUnsubscribed', 'TrackMuted', 'TrackUnmuted', 'LocalTrackPublished', 'LocalTrackUnpublished', 'ActiveSpeakersChanged', 'ParticipantNameChanged', 'ParticipantAttributesChanged', 'RoomMetadataChanged', 'ConnectionStateChanged', 'Disconnected', 'ParticipantPermissionsChanged', 'DataReceived'].map(k => [k, k]));
  class Room {
    constructor(options) {
      this.options = options; this.handlers = {}; this.remoteParticipants = new Map(); this.name = 'hall-test';
      this.localParticipant = {
        identity: 'me', name: 'Me', permissions: { canPublish: true, canPublishSources: [2, 3, 4] }, attributes: {},
        getTrackPublication: () => undefined,
        publishTrack: async track => { if (track.source !== S.ScreenShare) { steps.push('publish'); assert.equal(track.muted, true); } },
        createScreenTracks: async options => {
          await this.localParticipant.setScreenShareEnabled(true, options);
          return [{ source: S.ScreenShare, mediaStreamTrack: { addEventListener() {} }, stop() {} }];
        },
        unpublishTrack: async () => {},
        setMicrophoneEnabled: async on => { this.localParticipant.isMicrophoneEnabled = on; this.emit(E.TrackMuted, { source: S.Microphone }, this.localParticipant); },
        setCameraEnabled: async on => { steps.push('camera:' + on); this.localParticipant.isCameraEnabled = on; },
        setScreenShareEnabled: async (on, options) => { steps.push(['share', on, options]); },
        publishData: async (data, options) => { steps.push(['data', JSON.parse(new TextDecoder().decode(data)), options]); },
        setAttributes: async attrs => { this.localParticipant.attributes = attrs; }
      }; rooms.push(this);
    }
    on(event, handler) { this.handlers[event] = handler; return this; }
    emit(event, ...args) { return this.handlers[event]?.(...args); }
    async connect(url) { this.state = 'connected'; this.url = url; this.emit(E.ConnectionStateChanged, 'connected'); }
    async disconnect() { this.state = 'disconnected'; this.emit(E.Disconnected); }
  }
  const window = { LivekitClient: { VideoPreset: sdk.VideoPreset, VideoPresets: sdk.VideoPresets, ScreenSharePresets: sdk.ScreenSharePresets, Room, RoomEvent: E, Track: { Source: S }, createLocalAudioTrack: async () => ({ mute: async function () { this.muted = true; steps.push('mute'); }, stop() {} }) } };
  vm.runInNewContext(source, { window, location: { protocol, host: 'ipop.test' }, document: { getElementById: () => null, querySelectorAll: () => [] }, setTimeout: (fn, ms) => { if (ms === 100) { fn(); return 0; } timers.push(fn); return timers.length; }, clearTimeout() {}, TextEncoder, TextDecoder, console, ...extras });
  return { api: window.ipopRooms, rooms, steps, callbacks, E, S, timers, ref: { invokeMethodAsync: (...args) => { callbacks.push(args); return Promise.resolve(); } } };
}
test('browser joins muted, camera off, exact capture settings and HTTPS host rewrite', async () => {
  const h = harness();
  assert.equal(await h.api.join('wss://other.test', 'token', h.ref, {}), true);
  assert.equal(h.rooms[0].url, 'wss://ipop.test');
  assert.deepStrictEqual(h.steps, ['mute', 'publish']);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.width, 1280);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.height, 720);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.frameRate, 30);
  assert.equal(h.api.startPcRecording(), false);
  await h.api.leave();
});
test('camera permissions use numeric protobuf sources and require canPublish', async () => {
  const h = harness('http:'); await h.api.join('ws://lan:7880', 'token', h.ref, {});
  const r = h.rooms[0]; assert.equal(r.url, 'ws://lan:7880');
  assert.equal(await h.api.setCamera(true), false);
  r.localParticipant.permissions.canPublishSources = [1];
  await r.emit(h.E.ParticipantPermissionsChanged, {}, r.localParticipant);
  assert.equal(await h.api.setCamera(true), true);
  r.localParticipant.permissions.canPublishSources = [];
  assert.equal(await h.api.setCamera(true), true);
  r.localParticipant.permissions.canPublish = false;
  await r.emit(h.E.ParticipantPermissionsChanged, {}, r.localParticipant);
  assert.equal(await h.api.setCamera(true), false);
  assert.ok(h.callbacks.some(c => c[1] === 'video_on'));
  assert.ok(h.callbacks.some(c => c[1] === 'video_off'));
  assert.ok(h.steps.includes('camera:false'));
  await h.api.leave();
});
test('share busy/takeover, server mute notice, own mute suppression and stale events', async () => {
  const h = harness(); await h.api.join('ws://lan', 'token', h.ref, {});
  const r = h.rooms[0];
  r.remoteParticipants.set('other', { identity: 'other', getTrackPublication: () => ({ isMuted: false }) });
  assert.equal(await h.api.setShare(true, false), 'busy');
  assert.equal(await h.api.setShare(true, true), 'ok');
  assert.equal(h.steps.find(s => s[0] === 'data')[1].target, 'other');
  assert.equal(h.steps.find(s => s[0] === 'share')[2].resolution.frameRate, 60);
  assert.equal(await h.api.setMic(false), false);
  assert.equal(h.callbacks.filter(c => c[1] === 'muted_by_server').length, 0);
  r.emit(h.E.TrackMuted, { source: h.S.Microphone }, r.localParticipant);
  assert.equal(h.callbacks.filter(c => c[1] === 'muted_by_server').length, 1);
  await h.api.leave(); const count = h.callbacks.length;
  r.emit(h.E.RoomMetadataChanged, '{}'); assert.equal(h.callbacks.length, count);
  assert.equal(await h.api.setShare(true, true), 'denied');
});
test('PC recorder stops idempotently, saves WebM and releases generated tracks/context', async () => {
  const outputs = [], recorders = [];
  let stopped = 0, closed = 0, cancelled = 0;
  const videoTrack = { stop() { stopped++; } }, audioTrack = { stop() { stopped++; } };
  class Stream { constructor(tracks) { this.tracks = tracks; } getTracks() { return this.tracks; } }
  class Recorder {
    static isTypeSupported() { return true; }
    constructor(stream, options) { this.stream = stream; this.options = options; this.state = 'inactive'; recorders.push(this); }
    start(ms) { assert.equal(ms, 1000); this.state = 'recording'; }
    stop() { this.state = 'inactive'; }
  }
  class Audio {
    async resume() {} async close() { closed++; }
    createMediaStreamDestination() { return { stream: { getAudioTracks: () => [audioTrack] } }; }
  }
  const document = {
    getElementById: () => null, querySelectorAll: () => [], body: { appendChild() {} },
    createElement: tag => tag === 'canvas' ? { getContext: () => ({ fillRect() {} }), captureStream: fps => { assert.equal(fps, 15); return { getVideoTracks: () => [videoTrack] }; } }
      : { click() { outputs.push(this.download); }, remove() {} }
  };
  const h = harness('https:', { document, MediaRecorder: Recorder, MediaStream: Stream, AudioContext: Audio, Blob, URL: { createObjectURL: () => 'blob:test', revokeObjectURL() {} }, requestAnimationFrame: () => 1, cancelAnimationFrame: () => { cancelled++; } });
  await h.api.join('ws://lan', 'token', h.ref, {});
  assert.equal(h.api.startPcRecording(), true);
  assert.equal(h.rooms[0].localParticipant.attributes.rec, 'pc');
  assert.equal(h.api.startPcRecording(), false);
  h.rooms[0].state = 'disconnected';
  await h.rooms[0].emit(h.E.Disconnected);
  h.api.stopPcRecording();
  assert.equal(stopped, 2); assert.equal(closed, 1);
  await h.api.leave();
  await h.api.join('ws://lan', 'token', h.ref, {});
  await h.api.setRecordingAttr('new-room-marker');
  recorders[0].ondataavailable({ data: new Blob(['test']) });
  recorders[0].onstop();
  assert.match(outputs[0], /^test .*\.webm$/);
  assert.equal(stopped, 2); assert.equal(closed, 1); assert.ok(cancelled > 0);
});
test('share rechecks after picker and retracts when another presenter wins', async () => {
  const h = harness(); await h.api.join('ws://lan', 'token', h.ref, {});
  const r = h.rooms[0], enabled = [];
  r.localParticipant.setScreenShareEnabled = async on => {
    enabled.push(on);
    if (on) r.remoteParticipants.set('other', { identity: 'other', getTrackPublication: () => ({ isMuted: false }) });
  };
  assert.equal(await h.api.setShare(true, false), 'busy');
  // v2 captures first and checks the room before publishing; discard the unpublished capture.
  assert.deepStrictEqual(enabled, [true]);
  assert.equal(h.steps.filter(s => s === 'publish').length, 1); // only the muted microphone
});
test('share refuses stale picker and stale takeover results', async () => {
  const h = harness(); await h.api.join('ws://lan', 'token', h.ref, {});
  const r = h.rooms[0];
  r.localParticipant.setScreenShareEnabled = async () => { await h.api.leave(); };
  assert.equal(await h.api.setShare(true, false), 'denied');
  await h.api.join('ws://lan', 'token', h.ref, {});
  const next = h.rooms[1];
  next.remoteParticipants.set('other', { identity: 'other', getTrackPublication: () => ({ isMuted: false }) });
  next.localParticipant.publishData = async () => { await h.api.leave(); };
  assert.equal(await h.api.setShare(true, true), 'denied');
  assert.equal(h.steps.filter(s => s[0] === 'share').length, 0);
});
test('takeover waits for publication to disappear or bounded timeout before publishing', async () => {
  let polls = 0;
  const h = harness('https:', { setTimeout: (fn, ms) => { if (ms === 100) { polls++; fn(); } return 1; } });
  await h.api.join('ws://lan', 'token', h.ref, {});
  const r = h.rooms[0];
  r.remoteParticipants.set('other', { identity: 'other', getTrackPublication: () => polls < 2 ? { isMuted: false } : undefined });
  assert.equal(await h.api.setShare(true, true), 'ok'); assert.equal(polls, 2);
  polls = 0;
  r.remoteParticipants.set('other', { identity: 'other', getTrackPublication: () => ({ isMuted: false }) });
  assert.equal(await h.api.setShare(true, true), 'ok'); assert.equal(polls, 30);
});
test('participant tile displays BD initials for Budi Darmawan', async () => {
  const avatar = {}, label = {}, video = {};
  const tile = { dataset: { identity: 'me' }, classList: { remove() {}, toggle() {} },
    querySelector: selector => selector === '.mr-tile-av' ? avatar : selector === '.mr-tile-nm' ? label : video };
  const strip = { querySelector: () => tile, querySelectorAll: () => [tile] };
  const stage = { querySelector: () => null, classList: { remove() {}, toggle() {} } };
  const document = { getElementById: id => id === 'strip' ? strip : stage, querySelectorAll: () => [] };
  const h = harness('https:', { document, CSS: { escape: value => value } });
  await h.api.join('ws://lan', 'token', h.ref, { stripId: 'strip', stageId: 'stage' });
  h.rooms[0].localParticipant.name = 'Budi Darmawan';
  h.rooms[0].emit(h.E.ParticipantNameChanged);
  assert.equal(avatar.textContent, 'BD');
  await h.api.leave();
});
test('unexpected disconnect cleans DOM, denies controls, and notifies only once', async () => {
  let removed = 0;
  const strip = { innerHTML: 'tiles' };
  const h2 = harness('https:', { document: { getElementById: id => id === 'strip' ? strip : null, querySelectorAll: () => [{ remove() { removed++; } }] } });
  await h2.api.join('ws://lan', 'token', h2.ref, { stripId: 'strip', stageId: 'stage' });
  h2.rooms[0].state = 'disconnected';
  await h2.rooms[0].emit(h2.E.Disconnected);
  await h2.api.leave();
  assert.equal(strip.innerHTML, ''); assert.equal(removed, 1);
  assert.equal(await h2.api.setMic(true), false);
  assert.equal(h2.callbacks.filter(c => c[0] === 'OnRoomState' && c[1] === 'disconnected').length, 1);
});
