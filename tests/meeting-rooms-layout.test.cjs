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
        publishTrack: async track => { steps.push('publish'); assert.equal(track.muted, true); },
        setMicrophoneEnabled: async on => { this.localParticipant.isMicrophoneEnabled = on; this.emit(E.TrackMuted, { source: S.Microphone }, this.localParticipant); },
        setCameraEnabled: async on => { steps.push('camera:' + on); this.localParticipant.isCameraEnabled = on; },
        setScreenShareEnabled: async (on, options) => { steps.push(['share', on, options]); },
        publishData: async (data, options) => { steps.push(['data', JSON.parse(new TextDecoder().decode(data)), options]); },
        setAttributes: async attrs => { this.localParticipant.attributes = attrs; }
      }; rooms.push(this);
    }
    on(event, handler) { this.handlers[event] = handler; return this; }
    emit(event, ...args) { return this.handlers[event]?.(...args); }
    async connect(url) { this.url = url; this.emit(E.ConnectionStateChanged, 'connected'); }
    async disconnect() { this.emit(E.Disconnected); }
  }
  const window = { LivekitClient: { Room, RoomEvent: E, Track: { Source: S }, createLocalAudioTrack: async () => ({ mute: async function () { this.muted = true; steps.push('mute'); }, stop() {} }) } };
  vm.runInNewContext(source, { window, location: { protocol, host: 'ipop.test' }, document: { getElementById: () => null, querySelectorAll: () => [] }, setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {}, TextEncoder, TextDecoder, console, ...extras });
  return { api: window.ipopRooms, rooms, steps, callbacks, E, S, timers, ref: { invokeMethodAsync: (...args) => { callbacks.push(args); return Promise.resolve(); } } };
}
test('browser joins muted, camera off, exact capture settings and HTTPS host rewrite', async () => {
  const h = harness();
  assert.equal(await h.api.join('wss://other.test', 'token', h.ref, {}), true);
  assert.equal(h.rooms[0].url, 'wss://ipop.test');
  assert.deepStrictEqual(h.steps, ['mute', 'publish']);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.width, 640);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.height, 360);
  assert.equal(h.rooms[0].options.videoCaptureDefaults.resolution.frameRate, 15);
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
  assert.equal(h.steps.find(s => s[0] === 'share')[2].resolution.frameRate, 10);
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
  h.api.stopPcRecording(); h.api.stopPcRecording();
  assert.equal(h.rooms[0].localParticipant.attributes.rec, '');
  await h.api.leave();
  recorders[0].ondataavailable({ data: new Blob(['test']) });
  recorders[0].onstop();
  assert.match(outputs[0], /^test .*\.webm$/);
  assert.equal(stopped, 2); assert.equal(closed, 1); assert.ok(cancelled > 0);
});
