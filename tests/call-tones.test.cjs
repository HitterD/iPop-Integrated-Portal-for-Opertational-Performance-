// Run with: node tests/call-tones.test.cjs
// A call tone must stop on every stop, however its start calls overlap: the caller gets two
// "Ringing" updates (hub echo + POST result) while ringback.mp3 is still loading.
const assert = require("node:assert/strict");
const path = require("node:path");

const sources = [];
class FakeCtx {
  constructor() { this.state = "running"; this.destination = {}; }
  createGain() { return { gain: { value: 1 }, connect() { }, disconnect() { } }; }
  createBufferSource() {
    const s = { playing: false, connect() { }, disconnect() { }, start() { this.playing = true; }, stop() { this.playing = false; } };
    sources.push(s);
    return s;
  }
  decodeAudioData(b) { return Promise.resolve({ decoded: b }); }
  resume() { return Promise.resolve(); }
}

// fetch stays pending until the test releases it, like a slow mp3 download.
const pending = [];
globalThis.fetch = () => new Promise(res => pending.push(() => res({ ok: true, arrayBuffer: () => Promise.resolve(new ArrayBuffer(1)) })));
globalThis.window = globalThis;
globalThis.AudioContext = FakeCtx;
globalThis.localStorage = { getItem: () => null, setItem() { } };
globalThis.addEventListener = () => { };
globalThis.removeEventListener = () => { };
globalThis.document = { visibilityState: "visible" };
require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/sounds.js"));
const s = globalThis.ipopSound;

const flush = () => new Promise(r => setTimeout(r, 0));
const playing = () => sources.filter(x => x.playing).length;

(async () => {
  // Two overlapping starts while the file loads → exactly one tone, and stop silences it.
  s.loop("ringback");
  s.loop("ringback");
  pending.splice(0).forEach(f => f());
  await flush();
  assert.equal(playing(), 1, "overlapping loop() calls must leave a single tone");
  s.stopLoop();
  assert.equal(playing(), 0, "stopLoop must silence every tone");

  // Stop while loading → nothing starts afterwards.
  sources.length = 0;
  s.loop("ringtone");
  s.stopLoop();
  pending.splice(0).forEach(f => f());
  await flush();
  assert.equal(playing(), 0, "a tone stopped while loading must never start");

  // Cached buffer: rapid restarts (reconnect refresh) still leave one tone.
  sources.length = 0;
  s.loop("ringback"); s.loop("ringback"); s.loop("ringback");
  await flush();
  assert.equal(playing(), 1);
  s.loop("ringtone");
  pending.splice(0).forEach(f => f());
  await flush();
  assert.equal(playing(), 1, "switching tones replaces the old one");
  s.stopLoop();
  assert.equal(playing(), 0);

  console.log("call-tones: ok");
})().catch(e => { console.error(e); process.exit(1); });
