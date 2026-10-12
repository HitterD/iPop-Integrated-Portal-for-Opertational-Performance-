// Task 7: Flip shows only with >1 camera, so .NET re-counts on navigator.mediaDevices 'devicechange'.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const file = path.join(__dirname, '../src/IPop.Web/wwwroot/js/meeting-rooms.js');
const sdk = require('../src/IPop.Web/wwwroot/js/livekit-client.umd.js');

function harness() {
  const calls = [], listeners = new Map(), timers = new Map(), rooms = [];
  let nextTimer = 1;
  class Element {
    constructor() { this.dataset = {}; this.classList = { add() {}, remove() {}, toggle() {} }; }
    appendChild() {} remove() {} querySelector() { return null; } querySelectorAll() { return []; }
  }
  class Room {
    constructor(options) {
      this.options = options; this.handlers = {}; this.remoteParticipants = new Map(); this.metadata = '';
      this.localParticipant = { identity: 'me', permissions: { canPublish: true, canPublishSources: [] }, getTrackPublication: () => undefined,
        publishTrack: async () => {}, setCameraEnabled: async () => {} };
      rooms.push(this);
    }
    on(e, fn) { this.handlers[e] = fn; return this; }
    connect() { return Promise.resolve(); }
    disconnect() { return Promise.resolve(); }
  }
  const mediaDevices = {
    enumerateDevices: async () => [],
    addEventListener(type, fn) { listeners.set(fn, type); },
    removeEventListener(type, fn) { listeners.delete(fn); },
  };
  const window = { LivekitClient: { ...sdk, Room, createLocalAudioTrack: async () => ({ mute: async () => {}, stop() {}, source: sdk.Track.Source.Microphone }) } };
  vm.runInNewContext(fs.readFileSync(file, 'utf8'), {
    window, document: { body: new Element(), createElement: () => new Element(), getElementById: () => null, querySelectorAll: () => [] },
    navigator: { userAgent: 'Desktop', mediaDevices }, location: { protocol: 'http:' }, CSS: { escape: x => x },
    TextDecoder, TextEncoder, Uint8Array, console,
    setTimeout: (fn) => { const id = nextTimer++; timers.set(id, fn); return id; }, clearTimeout: (id) => timers.delete(id),
  });
  const ref = { invokeMethodAsync: async (...args) => { calls.push(args); } };
  const fire = () => [...listeners].filter(([, t]) => t === 'devicechange').forEach(([fn]) => fn());
  const flush = () => { const fns = [...timers.values()]; timers.clear(); fns.forEach(f => f()); };
  return { api: window.ipopRooms, calls, listeners, fire, flush, ref };
}

test('devicechange while in a room asks .NET to re-count cameras (debounced)', async () => {
  const h = harness();
  assert.equal(await h.api.join('ws://x', 't', h.ref, {}), true);
  assert.equal([...h.listeners.values()].filter(t => t === 'devicechange').length, 1);
  h.fire(); h.fire(); h.fire();
  h.flush();
  assert.equal(h.calls.filter(c => c[0] === 'OnDevicesChanged').length, 1);
});

test('leaving removes the devicechange listener and a pending re-count never fires', async () => {
  const h = harness();
  await h.api.join('ws://x', 't', h.ref, {});
  h.fire();
  await h.api.leave();
  h.flush();
  assert.equal(h.listeners.size, 0);
  assert.equal(h.calls.filter(c => c[0] === 'OnDevicesChanged').length, 0);
});

test('joining again keeps a single listener', async () => {
  const h = harness();
  await h.api.join('ws://x', 't', h.ref, {});
  await h.api.join('ws://x', 't2', h.ref, {});
  assert.equal(h.listeners.size, 1);
});
