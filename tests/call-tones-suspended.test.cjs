// Run with: node tests/call-tones-suspended.test.cjs
// Phones suspend WebAudio while iPop is in the background and resume it only after a tap, so an
// incoming call there was silent. The ring must then fall back to an <audio> element, and every
// stop must still silence it.
const assert = require("node:assert/strict");
const path = require("node:path");

let resumeWorks = false;
class SuspendedCtx {
  constructor() { this.state = "suspended"; this.destination = {}; }
  createGain() { return { gain: { value: 1 }, connect() { } }; }
  createBufferSource() { return { connect() { }, disconnect() { }, start() { }, stop() { } }; }
  decodeAudioData(b) { return Promise.resolve({ decoded: b }); }
  resume() { if (resumeWorks) this.state = "running"; return Promise.resolve(); }
}
const audios = [];
class FakeAudio {
  constructor(src) { this.src = src; this.loop = false; this.volume = 1; this.playing = false; audios.push(this); }
  play() { this.playing = true; return Promise.resolve(); }
  pause() { this.playing = false; }
  removeAttribute() { } load() { }
}
globalThis.fetch = () => Promise.resolve({ ok: true, arrayBuffer: () => Promise.resolve(new ArrayBuffer(1)) });
globalThis.window = globalThis;
globalThis.AudioContext = SuspendedCtx;
globalThis.Audio = FakeAudio;
globalThis.localStorage = { getItem: () => null, setItem() { } };
globalThis.addEventListener = () => { };
globalThis.removeEventListener = () => { };
globalThis.document = { visibilityState: "hidden" };
require(path.join(__dirname, "../src/IPop.Web/wwwroot/js/sounds.js"));
const s = globalThis.ipopSound;
const wait = ms => new Promise(r => setTimeout(r, ms));
const ringing = () => audios.filter(a => a.playing);

(async () => {
  s.loop("ringtone");
  await wait(400);
  assert.equal(ringing().length, 1, "suspended WebAudio: the ring falls back to an <audio> element");
  assert.ok(ringing()[0].loop && ringing()[0].src.endsWith("sounds/ringtone.mp3"));
  s.stopLoop();
  assert.equal(ringing().length, 0, "stopLoop silences the fallback ring");

  // Stopped during the resume attempt: nothing may start afterwards.
  s.loop("ringtone");
  s.stopLoop();
  await wait(400);
  assert.equal(ringing().length, 0, "a ring stopped while resuming never starts");

  // A new ring replaces the old one: never two at once.
  s.loop("ringtone"); await wait(400);
  s.loop("ringback"); await wait(400);
  assert.equal(ringing().length, 1);
  assert.ok(ringing()[0].src.endsWith("sounds/ringback.mp3"));
  s.stopLoop();

  // When resume succeeds, WebAudio is used and no <audio> element is created.
  resumeWorks = true;
  const before = audios.length;
  s.loop("ringtone"); await wait(400);
  assert.equal(audios.length, before, "a running WebAudio context needs no fallback");
  s.stopLoop();
  console.log("call-tones-suspended: ok");
})().catch(e => { console.error(e); process.exit(1); });
