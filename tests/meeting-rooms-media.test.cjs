const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const file = path.join(__dirname, '../src/IPop.Web/wwwroot/js/meeting-rooms.js');
const media = require(file);
const sdk = require('../src/IPop.Web/wwwroot/js/livekit-client.umd.js');
const message = { id: '11111111-1111-1111-1111-111111111111', userId: '22222222-2222-2222-2222-222222222222', name: '<img onerror=bad>', text: '<script>hello</script>', sentAt: '2026-10-12T10:00:00Z', kind: 'text' };
const encode = value => new TextEncoder().encode(JSON.stringify(value));
const deferred = () => { let resolve; const promise = new Promise(r => resolve = r); return { promise, resolve }; };

test('chat parser validates bounded fields and returns plain text, ignoring extra fields', () => {
    assert.equal(typeof media.parseChatPayload, 'function');
    assert.deepEqual(media.parseChatPayload(encode({ ...message, extra: 'ignored' })), message);
    assert.deepEqual(media.parseChatPayload(encode({ ...message, userId: '00000000-0000-0000-0000-000000000000', name: '', kind: 'system' })), { ...message, userId: '00000000-0000-0000-0000-000000000000', name: '', kind: 'system' });
    for (const value of [null, [], {}, { ...message, id: 'not-a-guid' }, { ...message, userId: 'NIK' }, { ...message, name: 'a'.repeat(257) }, { ...message, text: 'a'.repeat(2001) }, { ...message, text: '' }, { ...message, sentAt: 'tomorrow' }, { ...message, kind: 'html' }]) assert.equal(media.parseChatPayload(encode(value)), null);
    assert.equal(media.parseChatPayload(new Uint8Array(20000)), null);
    assert.equal(media.parseChatPayload(new Uint8Array([255])), null);
});
test('chat parser rejects non-string Guid fields instead of accepting JSON coercion', () => {
    assert.equal(media.parseChatPayload(encode({ ...message, id: [message.id] })), null);
    assert.equal(media.parseChatPayload(encode({ ...message, userId: [message.userId] })), null);
});
test('camera choice cycles distinct actual devices and never selects current or blank IDs', () => {
    assert.equal(typeof media.nextCamera, 'function');
    const devices = [{ kind: 'audioinput', deviceId: 'mic' }, { kind: 'videoinput', deviceId: '' }, { kind: 'videoinput', deviceId: 'front' }, { kind: 'videoinput', deviceId: 'front' }, { kind: 'videoinput', deviceId: 'back' }];
    assert.equal(media.nextCamera(devices, 'front').deviceId, 'back');
    assert.equal(media.nextCamera(devices, 'back').deviceId, 'front');
    assert.equal(media.nextCamera(devices, 'unknown').deviceId, 'front');
    assert.equal(media.nextCamera(devices.slice(0, 4), 'front'), null);
});

function harness() {
    const calls = [], rooms = [], elements = [], devices = [{ kind: 'videoinput', deviceId: 'front', label: 'Front' }, { kind: 'videoinput', deviceId: 'back', label: 'Back' }];
    class Element {
        constructor(tag) { this.tag = tag; this.dataset = {}; this.children = []; this.style = {}; this.classList = { add() {}, remove() {}, toggle() {} }; }
        appendChild(child) { this.children.push(child); elements.push(child); }
        append(...children) { children.forEach(c => this.appendChild(c)); }
        remove() { this.removed = true; }
        play() { this.played = true; return Promise.resolve(); }
    }
    const document = { body: new Element('body'), createElement: tag => new Element(tag), getElementById: () => null, querySelectorAll: () => elements.filter(e => e.dataset.mrAudio && !e.removed) };
    const source = sdk.Track.Source;
    const makeTrack = (kind, src) => {
        const track = { kind, source: src, mediaStreamTrack: { readyState: 'live', addEventListener() {}, getSettings: () => ({ deviceId: 'front', facingMode: 'user' }) }, mute: async () => {}, stop() { this.stopped = true; }, attach() { const e = new Element(kind); this.element = e; return e; }, detach() { this.detached = true; return this.element ? [this.element] : []; }, restartTrack: async function(options) { track.restartOptions = options; } };
        return track;
    };
    class Room {
        constructor(options) {
            this.options = options; this.handlers = {}; this.remoteParticipants = new Map(); this.metadata = ''; this.state = 'connected';
            const pubs = new Map();
            this.localParticipant = { identity: 'local', permissions: { canPublish: true, canPublishSources: [] }, isCameraEnabled: false,
                getTrackPublication: src => pubs.get(src), publications: pubs,
                publishTrack: async (track, options) => { calls.push(['publish', track, options]); pubs.set(track.source, { track, source: track.source, isMuted: false }); return pubs.get(track.source); },
                unpublishTrack: async track => { calls.push(['unpublish', track]); pubs.delete(track.source); track.stop(); },
                setCameraEnabled: async on => { calls.push(['camera', on]); this.localParticipant.isCameraEnabled = on; },
                setScreenShareEnabled: async on => { calls.push(['share', on]); if (on) { pubs.set(source.ScreenShare, { track: makeTrack('video', source.ScreenShare) }); } else { pubs.delete(source.ScreenShare); pubs.delete(source.ScreenShareAudio); } },
                createScreenTracks: async options => { calls.push(['capture', options]); return this.captured || [makeTrack('video', source.ScreenShare)]; },
                publishData: async (data, options) => calls.push(['data', JSON.parse(new TextDecoder().decode(data)), options]),
            };
            rooms.push(this);
        }
        on(e, fn) { this.handlers[e] = fn; return this; }
        connect() { return Promise.resolve(); }
        disconnect() { this.disconnected = true; return Promise.resolve(); }
        getActiveDevice() { return this.activeDevice || 'front'; }
        async switchActiveDevice(kind, id) { calls.push(['switch', kind, id]); this.activeDevice = id; return true; }
    }
    const window = { LivekitClient: { ...sdk, Room, createLocalAudioTrack: async () => makeTrack('audio', source.Microphone) } };
    const navigator = { userAgent: 'Desktop', mediaDevices: { enumerateDevices: async () => devices } };
    vm.runInNewContext(fs.readFileSync(file, 'utf8'), { window, document, navigator, location: { protocol: 'http:' }, CSS: { escape: x => x }, TextDecoder, TextEncoder, Uint8Array, console, setTimeout: () => 1, clearTimeout() {} });
    const ref = { invokeMethodAsync: async (...args) => { calls.push(['net', ...args]); } };
    const api = window.ipopRooms;
    return { api, calls, rooms, elements, devices, navigator, makeTrack, ref, source, async join() { await api.join('ws://test', 'token', ref, {}); return rooms.at(-1); }, camera(r) { r.localParticipant.isCameraEnabled = true; const track = makeTrack('video', source.Camera); r.localParticipant.publications.set(source.Camera, { track, isMuted: false }); return track; } };
}

test('review: participant chat forgery is dropped but server relay still arrives', async () => {
    const h = harness(), r = await h.join();
    await r.handlers[sdk.RoomEvent.DataReceived](encode(message), { identity: 'attacker' }, 0, 'ipop.chat');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomChat').length, 0);
    await r.handlers[sdk.RoomEvent.DataReceived](encode(message), undefined, 0, 'ipop.chat');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomChat').length, 1);
});
test('review: front/back flip skips secondary rear lenses and unknown-facing lists still cycle', () => {
    const cameras = ['Front', 'Back', 'Back Dual Wide', 'Back Ultra Wide'].map((label, i) => ({ kind: 'videoinput', deviceId: String(i), label }));
    assert.equal(media.nextCamera(cameras, '0', 'user').deviceId, '1');
    assert.equal(media.nextCamera(cameras, '1', 'environment').deviceId, '0');
    assert.equal(media.nextCamera(cameras, '2').deviceId, '0');
    const unknown = cameras.map(d => ({ ...d, label: '' }));
    assert.equal(media.nextCamera(unknown, '1').deviceId, '2');
    assert.equal(media.nextCamera(cameras.slice(1), '1', 'environment'), null);
});
test('review: flip uses current facing capabilities before conflicting current-device label', async () => {
    const h = harness(), r = await h.join(), track = h.camera(r);
    h.devices.push({ kind: 'videoinput', deviceId: 'wide', label: 'Back Ultra Wide' });
    track.mediaStreamTrack.getSettings = () => ({ deviceId: 'front' });
    track.mediaStreamTrack.getCapabilities = () => ({ facingMode: ['environment'] });
    const result = await h.api.flipCamera();
    assert.equal(result.success, false); // current is physically rear despite the misleading label; no other front device
    assert.equal(h.calls.filter(c => c[0] === 'switch').length, 0);
});
test('review: repeated share enable emits no duplicate missing-audio notice', async () => {
    const h = harness(); await h.join();
    assert.equal(await h.api.setShare(true), 'ok');
    assert.equal(await h.api.setShare(true), 'ok');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomNotice' && c[2] === 'share_no_audio').length, 1);
    assert.equal(h.calls.filter(c => c[0] === 'capture').length, 1);
});
test('review: fallback facing selection is remembered for camera off/on and cleared on room replacement', async () => {
    const h = harness(), r = await h.join(), track = h.camera(r);
    h.navigator.userAgent = 'iPhone'; h.devices.forEach(d => { d.label = ''; d.deviceId = ''; });
    r.options.videoCaptureDefaults.deviceId = { exact: 'old-front' };
    assert.equal((await h.api.flipCamera()).success, true);
    await h.api.setCamera(false); await h.api.setCamera(true);
    assert.equal(r.options.videoCaptureDefaults.facingMode, 'environment');
    assert.equal(r.options.videoCaptureDefaults.deviceId, undefined);
    assert.equal(r.options.videoCaptureDefaults.resolution.height, 720);
    const next = await h.join();
    assert.equal(next.options.videoCaptureDefaults.facingMode, undefined);
});
test('join configures h720 camera and explicit h180/h360 simulcast while retaining adaptation', async () => {
    const h = harness(), r = await h.join();
    assert.equal(r.options.videoCaptureDefaults.resolution.width, 1280);
    assert.equal(r.options.videoCaptureDefaults.resolution.height, 720);
    assert.equal(r.options.videoCaptureDefaults.resolution.frameRate, 30);
    assert.deepEqual(Array.from(r.options.publishDefaults.videoSimulcastLayers, p => p.height), [180, 360]);
    assert.equal(r.options.publishDefaults.videoEncoding.maxFramerate, 30);
    assert.equal(r.options.adaptiveStream, true); assert.equal(r.options.dynacast, true);
});
test('successful silent share requests HD60 screen audio and publishes 720p30 layer, then notices once', async () => {
    const h = harness(); await h.join();
    assert.equal(await h.api.setShare(true, false), 'ok');
    const capture = h.calls.find(c => c[0] === 'capture')?.[1];
    assert.ok(capture, 'capture uses guarded SDK track creation');
    assert.equal(capture.resolution.width, 1920); assert.equal(capture.resolution.height, 1080); assert.equal(capture.resolution.frameRate, 60);
    assert.equal(capture.contentHint, 'detail'); assert.equal(capture.systemAudio, 'include'); assert.equal(capture.selfBrowserSurface, 'exclude'); assert.equal(capture.surfaceSwitching, 'include');
    assert.equal(capture.suppressLocalAudioPlayback, true);
    assert.equal(capture.audio.suppressLocalAudioPlayback, true, '2.22.3 forwards nested audio constraint, not top-level suppression');
    const pub = h.calls.find(c => c[0] === 'publish' && c[1].source === h.source.ScreenShare)?.[2];
    assert.equal(pub.screenShareEncoding.maxBitrate, 6000000); assert.equal(pub.screenShareEncoding.maxFramerate, 60);
    assert.equal(pub.screenShareSimulcastLayers[0].height, 720); assert.equal(pub.screenShareSimulcastLayers[0].encoding.maxFramerate, 30);
    assert.equal(pub.degradationPreference, 'maintain-resolution'); assert.equal(pub.simulcast, true);
    assert.deepEqual(h.calls.filter(c => c[1] === 'OnRoomNotice'), [['net', 'OnRoomNotice', 'share_no_audio', null]]);
});
test('share with actual audio does not warn, cancellation does not warn', async () => {
    const h = harness(), r = await h.join();
    r.captured = [h.makeTrack('video', h.source.ScreenShare), h.makeTrack('audio', h.source.ScreenShareAudio)];
    assert.equal(await h.api.setShare(true), 'ok');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomNotice').length, 0);
    await h.api.setShare(false);
    r.localParticipant.createScreenTracks = async () => { throw new Error('NotAllowedError'); };
    assert.equal(await h.api.setShare(true), 'denied');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomNotice').length, 0);
});
test('capture finishing after room replacement stops tracks and never publishes or notices in either room', async () => {
    const h = harness(), r = await h.join(), pending = deferred();
    const track = h.makeTrack('video', h.source.ScreenShare);
    r.localParticipant.createScreenTracks = () => pending.promise;
    const share = h.api.setShare(true);
    await h.join(); pending.resolve([track]);
    assert.equal(await share, 'denied'); assert.equal(track.stopped, true);
    assert.equal(h.calls.filter(c => c[0] === 'publish' && c[1].source === h.source.ScreenShare).length, 0);
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomNotice').length, 0);
});
test('partial share publication failure stops and unpublishes captured tracks', async () => {
    const h = harness(), r = await h.join();
    const tracks = [h.makeTrack('video', h.source.ScreenShare), h.makeTrack('audio', h.source.ScreenShareAudio)]; r.captured = tracks;
    const publish = r.localParticipant.publishTrack;
    r.localParticipant.publishTrack = async (track, options) => { if (track.kind === 'audio') throw new Error('publish failed'); return publish(track, options); };
    assert.equal(await h.api.setShare(true), 'denied');
    assert.ok(tracks.every(t => t.stopped)); assert.equal(r.localParticipant.getTrackPublication(h.source.ScreenShare), undefined);
});
test('remote screen audio plays and detaches on unsubscribe and leave; stale-room events do nothing', async () => {
    const h = harness(), r = await h.join(), track = h.makeTrack('audio', h.source.ScreenShareAudio);
    r.handlers[sdk.RoomEvent.TrackSubscribed](track);
    assert.equal(track.element.played, true);
    r.handlers[sdk.RoomEvent.TrackUnsubscribed](track);
    assert.equal(track.detached, true); assert.equal(track.element.removed, true);
    const second = h.makeTrack('audio', h.source.ScreenShareAudio); r.handlers[sdk.RoomEvent.TrackSubscribed](second);
    await h.api.leave(); assert.equal(second.detached, true); assert.equal(second.element.removed, true);
    const stale = h.makeTrack('audio', h.source.ScreenShareAudio); r.handlers[sdk.RoomEvent.TrackSubscribed](stale); assert.equal(stale.element, undefined);
});
test('server-origin chat with undefined participant relays safe JSON only on ipop.chat, takeover still works', async () => {
    const h = harness(), r = await h.join();
    await r.handlers[sdk.RoomEvent.DataReceived](encode(message), undefined, 0, 'ipop.chat');
    assert.deepEqual(JSON.parse(h.calls.find(c => c[1] === 'OnRoomChat')?.[2] || 'null'), message);
    await r.handlers[sdk.RoomEvent.DataReceived](encode({ ...message, text: 'x'.repeat(2001) }), undefined, 0, 'ipop.chat');
    await r.handlers[sdk.RoomEvent.DataReceived](encode(message), undefined, 0, 'other');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomChat').length, 1);
    await r.handlers[sdk.RoomEvent.DataReceived](encode({ t: 'share-takeover', target: 'local' }), { name: 'Other' });
    assert.ok(h.calls.some(c => c[1] === 'OnRoomNotice' && c[2] === 'share_taken'));
    await h.join(); await r.handlers[sdk.RoomEvent.DataReceived](encode(message), undefined, 0, 'ipop.chat');
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomChat').length, 1);
});
test('listCameras returns count without opening devices and flip preserves camera off and video lock', async () => {
    const h = harness(), r = await h.join();
    assert.equal(typeof h.api.listCameras, 'function'); assert.equal(typeof h.api.flipCamera, 'function');
    assert.equal(await h.api.listCameras(), 2);
    assert.equal((await h.api.flipCamera()).success, false);
    h.camera(r); r.localParticipant.permissions.canPublishSources = [2];
    assert.equal((await h.api.flipCamera()).success, false);
    assert.equal(h.calls.filter(c => ['switch', 'camera'].includes(c[0])).length, 0);
});
test('flip switches to actual other device and returns current camera info without enabling camera', async () => {
    const h = harness(), r = await h.join(); h.camera(r);
    const result = await h.api.flipCamera();
    assert.equal(result.success, true); assert.equal(result.deviceId, 'back');
    assert.ok(h.calls.some(c => c[0] === 'switch' && c[1] === 'videoinput' && c[2] === 'back'));
    assert.equal(h.calls.filter(c => c[0] === 'camera').length, 0);
});
test('mobile blank labels fallback restarts same camera publication with facingMode and quality', async () => {
    const h = harness(), r = await h.join(), track = h.camera(r);
    h.navigator.userAgent = 'iPhone'; h.devices.forEach(d => { d.label = ''; d.deviceId = ''; });
    const result = await h.api.flipCamera();
    assert.equal(result.success, true); assert.equal(result.facingMode, 'environment');
    assert.equal(track.restartOptions.facingMode, 'environment'); assert.equal(track.restartOptions.resolution.height, 720);
    assert.equal(h.calls.filter(c => c[0] === 'switch').length, 0);
});
test('enumeration await cannot flip a replaced room or a newly locked/off camera', async () => {
    for (const change of ['replace', 'lock', 'off']) {
        const h = harness(), r = await h.join(); h.camera(r);
        const pending = deferred(); h.navigator.mediaDevices.enumerateDevices = () => pending.promise;
        assert.equal(typeof h.api.flipCamera, 'function');
        const flip = h.api.flipCamera();
        if (change === 'replace') await h.join();
        else if (change === 'lock') r.localParticipant.permissions.canPublish = false;
        else r.localParticipant.isCameraEnabled = false;
        pending.resolve(h.devices);
        assert.equal((await flip).success, false); assert.equal(h.calls.filter(c => c[0] === 'switch').length, 0);
    }
});
test('browser stop-sharing ends paired screen audio even if browser leaves audio live', async () => {
    const h = harness(), r = await h.join();
    const video = h.makeTrack('video', h.source.ScreenShare), audio = h.makeTrack('audio', h.source.ScreenShareAudio);
    const handlers = {};
    video.mediaStreamTrack.addEventListener = (event, fn) => { handlers[event] = fn; };
    r.captured = [video, audio];
    assert.equal(await h.api.setShare(true), 'ok');
    assert.equal(typeof handlers.ended, 'function');
    handlers.ended(); await new Promise(resolve => setImmediate(resolve));
    assert.equal(audio.stopped, true); assert.equal(r.localParticipant.getTrackPublication(h.source.ScreenShareAudio), undefined);
});
test('SDK 2.22.3 actually forwards nested suppression and the requested display constraints', async () => {
    const originalNavigator = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
    const originalStream = globalThis.MediaStream;
    let constraints;
    const browserTrack = { kind: 'video', id: 'capture', label: 'Screen', enabled: true, readyState: 'live', addEventListener() {}, removeEventListener() {}, getConstraints: () => ({}), getSettings: () => ({ width: 1920, height: 1080, frameRate: 60 }), stop() {} };
    class Stream { constructor(tracks) { this.tracks = tracks; } getTracks() { return this.tracks; } getVideoTracks() { return this.tracks; } getAudioTracks() { return []; } addTrack() {} removeTrack() {} }
    try {
        globalThis.MediaStream = Stream;
        Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { userAgent: 'Chrome/130.0', mediaDevices: { getDisplayMedia: async value => { constraints = value; return new Stream([browserTrack]); } } } });
        const h = harness(), r = await h.join();
        r.localParticipant.createScreenTracks = options => sdk.LocalParticipant.prototype.createScreenTracks.call({ roomOptions: {}, logContext: {}, emit() {} }, options);
        assert.equal(await h.api.setShare(true), 'ok');
        assert.equal(constraints.audio.suppressLocalAudioPlayback, true);
        assert.equal(constraints.systemAudio, 'include'); assert.equal(constraints.selfBrowserSurface, 'exclude'); assert.equal(constraints.surfaceSwitching, 'include');
        assert.equal(constraints.video.width.ideal, 1920); assert.equal(constraints.video.height.ideal, 1080); assert.equal(constraints.video.frameRate, 60);
        assert.equal(browserTrack.contentHint, 'detail');
        await h.api.setShare(false);
    } finally {
        if (originalNavigator) Object.defineProperty(globalThis, 'navigator', originalNavigator); else delete globalThis.navigator;
        if (originalStream === undefined) delete globalThis.MediaStream; else globalThis.MediaStream = originalStream;
    }
});
test('share publication resolving after replacement cleans old capture without changing new room', async () => {
    const h = harness(), r = await h.join(), pending = deferred(), track = h.makeTrack('video', h.source.ScreenShare);
    r.captured = [track]; r.localParticipant.publishTrack = () => pending.promise;
    const share = h.api.setShare(true); await new Promise(resolve => setImmediate(resolve));
    const next = await h.join(); pending.resolve({ track });
    assert.equal(await share, 'denied'); assert.equal(track.stopped, true);
    assert.equal(next.localParticipant.getTrackPublication(h.source.ScreenShare), undefined);
    assert.equal(h.calls.filter(c => c[1] === 'OnRoomNotice').length, 0);
});
test('admin lock during device switch disables old camera and returns failure', async () => {
    const h = harness(), r = await h.join(); h.camera(r); const pending = deferred();
    r.switchActiveDevice = () => pending.promise;
    const flip = h.api.flipCamera(); await new Promise(resolve => setImmediate(resolve));
    r.localParticipant.permissions.canPublish = false; pending.resolve(true);
    assert.equal((await flip).success, false);
    assert.ok(h.calls.some(c => c[0] === 'camera' && c[1] === false));
});
test('failed device switch stays off after a concurrent user camera-off', async () => {
    const h = harness(), r = await h.join(), track = h.camera(r), pending = deferred();
    r.switchActiveDevice = () => pending.promise;
    const flip = h.api.flipCamera(); await new Promise(resolve => setImmediate(resolve));
    r.localParticipant.isCameraEnabled = false; pending.resolve(false);
    assert.equal((await flip).success, false);
    assert.equal(h.calls.some(c => c[0] === 'camera' && c[1] === true), false);
});
test('flip device restart finishing after leave stops the old camera track', async () => {
    const h = harness(), r = await h.join(), track = h.camera(r), pending = deferred();
    assert.equal(typeof h.api.flipCamera, 'function');
    r.switchActiveDevice = () => pending.promise;
    const flip = h.api.flipCamera(); await new Promise(resolve => setImmediate(resolve));
    await h.api.leave(); pending.resolve(true);
    assert.equal((await flip).success, false); assert.equal(track.stopped, true);
});
