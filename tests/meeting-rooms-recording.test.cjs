const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const file = path.join(__dirname, '../src/IPop.Web/wwwroot/recording/recording.js');
const template = fs.existsSync(file) ? require(file) : {};

test('recording template exposes the pure layout calculation', () => {
    assert.equal(typeof template.calculateLayout, 'function');
});
test('share uses main and at most five right-strip tiles, excluding EG_ identities', () => {
    const people = Array.from({ length: 8 }, (_, i) => ({ identity: `user-${i}`, name: `User ${i}`, sharing: i === 6 }));
    people.unshift({ identity: 'EG_recorder', sharing: true });
    const layout = template.calculateLayout(people);
    assert.equal(layout.mode, 'share');
    assert.equal(layout.share.identity, 'user-6');
    assert.equal(layout.tiles.length, 5);
    assert.ok(layout.tiles.every(p => !p.identity.startsWith('EG_')));
    assert.equal(layout.columns, 1);
    assert.equal(layout.rows, 5);
});
test('no share grid contains up to twenty participants with bounded tile math', () => {
    for (let n = 0; n <= 24; n++) {
        const layout = template.calculateLayout(Array.from({ length: n }, (_, i) => ({ identity: `${i}` })));
        assert.equal(layout.mode, 'grid');
        assert.equal(layout.tiles.length, Math.min(n, 20));
        assert.ok(layout.columns * layout.rows >= Math.min(n, 20));
        assert.ok(layout.columns <= 5 && layout.rows <= 4);
    }
});
test('four participants use the approved two by two grid', () => {
    const layout = template.calculateLayout(Array.from({ length: 4 }, (_, i) => ({ identity: `${i}` })));
    assert.equal(layout.columns, 2);
    assert.equal(layout.rows, 2);
});
test('initials and fallback names never expose Guid identities', () => {
    assert.equal(template.initials('Sari Andini'), 'SA');
    assert.equal(template.initials('  '), '?');
    assert.equal(template.displayName({ identity: '12345678-1234-1234-1234-123456789abc' }), 'Participant');
});

function harness(people = []) {
    const logs = [], timers = [], frames = [], rooms = [], attached = [], detached = [], levels = [];
    class Element {
        constructor(tag) { this.tag = tag; this.children = []; this.style = { setProperty() {} }; this.classList = { toggle() {} }; }
        append(...children) { this.children.push(...children); }
        replaceChildren(...children) { this.children = children; }
        remove() { this.removed = true; }
        play() { this.played = true; return Promise.resolve(); }
        requestVideoFrameCallback(fn) { frames.push(fn); }
        set innerHTML(_) { throw Error('Unsafe HTML insertion'); }
    }
    const elements = new Map(['stage', 'audio', 'room-name', 'meeting-title', 'meeting-date', 'swatch'].map(id => [id, new Element(id)]));
    const E = Object.fromEntries(['ParticipantConnected', 'ParticipantDisconnected', 'TrackSubscribed', 'TrackUnsubscribed', 'TrackMuted', 'TrackUnmuted', 'ParticipantNameChanged', 'ActiveSpeakersChanged', 'Disconnected'].map(k => [k, k]));
    const track = kind => ({ kind, attach(el) { attached.push(el); }, detach(el) { detached.push(el); } });
    class Room {
        constructor(options) { this.options = options; this.handlers = {}; this.remoteParticipants = new Map(people.map(p => [p.identity, p])); rooms.push(this); }
        on(e, fn) { this.handlers[e] = fn; return this; }
        connect(url, token, options) { this.connection = { url, token, options }; return Promise.resolve(); }
        startAudio() { return Promise.resolve(); }
    }
    const window = { document: { getElementById: id => elements.get(id), createElement: tag => new Element(tag) },
        location: { search: '?url=ws%3A%2F%2Flivekit%3A7880&token=secret-token&room=Arabika&title=%3Cscript%3Ebad%3C%2Fscript%3E&date=2026-10-12T10%3A14%3A00Z' },
        requestAnimationFrame: fn => fn(), LivekitClient: { Room, RoomEvent: E, Track: { Kind: { Audio: 'audio' }, Source: { Camera: 'camera', ScreenShare: 'screen_share' } }, setLogLevel: level => levels.push(level) } };
    vm.runInNewContext(fs.readFileSync(file, 'utf8'), { window, URLSearchParams, console: { log: x => logs.push(x), error: x => logs.push(x) }, setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {} });
    return { logs, timers, frames, rooms, elements, attached, detached, levels, track };
}
const settle = async () => { await new Promise(resolve => setImmediate(resolve)); };
test('Egress connection preserves Docker URL, uses safe badge text, silences credential-bearing SDK logs, starts once and ends once', async () => {
    const h = harness(); await settle();
    assert.equal(h.rooms[0].connection.url, 'ws://livekit:7880');
    assert.equal(h.rooms[0].connection.token, 'secret-token');
    assert.equal(h.rooms[0].connection.options.autoSubscribe, true);
    assert.equal(h.elements.get('meeting-title').textContent, '<script>bad</script>');
    assert.deepEqual(h.levels, ['silent']);
    assert.deepEqual(h.logs, []);
    h.timers[0](); h.timers[0]();
    assert.deepEqual(h.logs, ['START_RECORDING']);
    h.rooms[0].handlers.Disconnected(); h.rooms[0].handlers.Disconnected();
    assert.deepEqual(h.logs, ['START_RECORDING', 'END_RECORDING']);
});
test('decoded video starts recording and audio from off-grid participants plays and detaches', async () => {
    let attaches = 0, detaches = 0;
    const track = { attach(el) { attaches++; }, detach() { detaches++; } };
    const people = Array.from({ length: 21 }, (_, i) => ({ identity: `user-${i}`, name: '<img onerror=bad>', isSpeaking: i === 0,
        trackPublications: new Map(i === 0 ? [['camera', { source: 'camera', kind: 'video', track }]] : i === 20 ? [['audio', { trackSid: 'audio', source: 'microphone', kind: 'audio', track }]] : []) }));
    people.push({ identity: 'EG_hidden', trackPublications: new Map([['audio', { trackSid: 'hidden', kind: 'audio', track }]]) });
    const h = harness(people); await settle();
    assert.equal(h.elements.get('stage').children.length, 20);
    assert.equal(h.elements.get('audio').children.length, 1);
    assert.equal(h.elements.get('audio').children[0].played, true);
    assert.deepEqual(h.logs, []);
    h.frames[0](); h.frames[0]();
    assert.deepEqual(h.logs, ['START_RECORDING']);
    assert.equal(attaches, 2);
    h.rooms[0].handlers.Disconnected();
    assert.equal(detaches, 2);
});
