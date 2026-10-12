/* Static Egress page: deliberately independent of Blazor and application authentication. */
(function (root) {
    'use strict';
    function displayName(person) { return person.name?.trim() || 'Participant'; }
    function initials(name) {
        return name.trim().split(/\s+/u).filter(Boolean).slice(0, 2).map(word => Array.from(word)[0]).join('').toUpperCase() || '?';
    }
    function calculateLayout(participants) {
        const people = participants.filter(p => !p.identity.startsWith('EG_'));
        const share = people.find(p => p.sharing) || null;
        const tiles = people.slice(0, share ? 5 : 20);
        const columns = share ? 1 : Math.min(5, Math.max(1, Math.ceil(Math.sqrt(tiles.length))));
        return { mode: share ? 'share' : 'grid', share, tiles, columns, rows: Math.max(1, Math.ceil(tiles.length / columns)) };
    }
    const api = { calculateLayout, displayName, initials };
    if (typeof module === 'object' && module.exports) module.exports = api;
    if (!root.document) return;

    const document = root.document;
    const query = new URLSearchParams(root.location.search);
    const roomName = query.get('room') || 'Meeting room';
    document.getElementById('room-name').textContent = roomName;
    document.getElementById('meeting-title').textContent = query.get('title') || 'Meeting';
    const date = new Date(query.get('date') || Date.now());
    document.getElementById('meeting-date').textContent = Number.isNaN(date.getTime()) ? '' : date.toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' });
    const colours = { Arabika: '#c47b45', Robusta: '#3f7d5a', Toraja: '#5562b8', Kalosi: '#b0412d', Mandheling: '#8a5a3c' };
    document.getElementById('swatch').style.background = colours[roomName] || '#c47b45';

    const LK = root.LivekitClient;
    const stage = document.getElementById('stage');
    const audioHost = document.getElementById('audio');
    const tiles = new Map();
    const audio = new Map();
    // TrackUnsubscribed can precede the SDK clearing publication.track.
    const unsubscribed = new WeakSet();
    let started = false, ended = false, connected = false, fallback;
    function participants() {
        return [...room.remoteParticipants.values()].filter(p => !p.identity.startsWith('EG_'));
    }
    function startSignal(allowEmpty = false) {
        if (!connected || started || ended) return;
        const people = participants();
        if (!people.length && !allowEmpty) return;
        if (people.length) {
            const expectedVideo = people.some(p => publication(p, LK.Track.Source.Camera) || publication(p, LK.Track.Source.ScreenShare));
            const visible = [...tiles.values()];
            if (visible.some(item => item.expectedVideo && !item.ready)) return;
            if (expectedVideo && !visible.some(item => item.ready)) return;
        }
        started = true;
        clearTimeout(fallback);
        console.log('START_RECORDING');
    }
    function endSignal() {
        if (ended) return;
        ended = true;
        clearTimeout(fallback);
        for (const item of [...tiles.values(), ...audio.values()]) detach(item);
        tiles.clear(); audio.clear();
        console.log('END_RECORDING');
    }
    function detach(item) {
        if (item.track && item.media) item.track.detach(item.media);
        item.element.remove();
    }
    function play(media) { const result = media.play(); if (result?.catch) result.catch(() => {}); }
    function publication(participant, source) {
        // A published, unmuted video is expected even while subscription is pending.
        return [...participant.trackPublications.values()].find(p => p.source === source && !p.isMuted);
    }
    function makeTile(person, track, sharing) {
        const element = document.createElement('section');
        element.className = 'tile' + (sharing ? ' share-main' : '');
        const mono = document.createElement('span'); mono.className = 'initials';
        const name = document.createElement('span'); name.className = 'name';
        element.append(mono, name);
        let media;
        const item = { element, mono, name, track, ready: false };
        if (track) {
            media = document.createElement('video'); media.autoplay = true; media.muted = true; media.playsInline = true;
            element.append(media); track.attach(media); play(media);
            // Start only after decoded media reaches the compositor, not on TrackSubscribed alone.
            const ready = () => {
                if (unsubscribed.has(track) || ![...tiles.values()].includes(item)) return;
                item.ready = true;
                root.requestAnimationFrame(() => startSignal());
            };
            if (media.requestVideoFrameCallback) media.requestVideoFrameCallback(ready);
            else media.addEventListener('loadeddata', ready, { once: true });
        }
        item.media = media;
        return item;
    }
    if (!LK || !query.get('url') || !query.get('token')) {
        const message = document.createElement('p'); message.className = 'empty'; message.textContent = 'Waiting for meeting connection'; stage.append(message);
        return;
    }
    // SDK debug/error context may include signaling URLs; only our credential-free markers are logged.
    LK.setLogLevel('silent');
    const room = new LK.Room({ adaptiveStream: false, dynacast: false });
    function render() {
        if (ended) return;
        const people = participants().map(p => ({
            identity: p.identity, name: p.name, participant: p,
            sharing: !!publication(p, LK.Track.Source.ScreenShare),
        }));
        const layout = calculateLayout(people);
        stage.className = layout.mode;
        stage.style.setProperty('--columns', layout.columns);
        stage.style.setProperty('--rows', layout.rows);
        const wanted = new Set();
        function tile(person, sharing) {
            const key = (sharing ? 'share:' : 'camera:') + person.identity;
            wanted.add(key);
            const pub = publication(person.participant, sharing ? LK.Track.Source.ScreenShare : LK.Track.Source.Camera);
            const track = pub?.track && !unsubscribed.has(pub.track) ? pub.track : undefined;
            let item = tiles.get(key);
            if (!item || item.track !== track) {
                if (item) detach(item);
                item = makeTile(person, track, sharing); tiles.set(key, item);
            }
            item.expectedVideo = !!pub;
            item.mono.textContent = initials(displayName(person));
            item.name.textContent = displayName(person) + (sharing ? ' · sharing' : '');
            item.element.classList.toggle('speaking', person.participant.isSpeaking);
            return item.element;
        }
        const children = [];
        if (layout.share) {
            children.push(tile(layout.share, true));
            const strip = document.createElement('div'); strip.className = 'strip';
            strip.style.setProperty('--rows', layout.rows);
            for (const person of layout.tiles) strip.append(tile(person, false));
            children.push(strip);
        } else for (const person of layout.tiles) children.push(tile(person, false));
        if (!people.length) {
            const message = document.createElement('p'); message.className = 'empty'; message.textContent = 'Waiting for participants'; children.push(message);
        }
        stage.replaceChildren(...children);
        for (const [key, item] of tiles) if (!wanted.has(key)) { detach(item); tiles.delete(key); }
        // Audio is independent of visible tiles: record everyone, including screen-share audio.
        const wantedAudio = new Set();
        for (const person of people) for (const pub of person.participant.trackPublications.values()) {
            if (!pub.track || unsubscribed.has(pub.track) || pub.kind !== LK.Track.Kind.Audio || pub.isMuted) continue;
            wantedAudio.add(pub.trackSid);
            const current = audio.get(pub.trackSid);
            if (!current || current.track !== pub.track) {
                if (current) detach(current);
                const media = document.createElement('audio'); media.autoplay = true;
                audioHost.append(media); pub.track.attach(media); play(media);
                audio.set(pub.trackSid, { element: media, media, track: pub.track });
            }
        }
        for (const [key, item] of audio) if (!wantedAudio.has(key)) { detach(item); audio.delete(key); }
        if (!people.some(p => publication(p.participant, LK.Track.Source.Camera) || p.sharing) && people.length)
            root.requestAnimationFrame(() => root.requestAnimationFrame(() => startSignal()));
    }
    for (const event of ['ParticipantConnected', 'ParticipantDisconnected', 'TrackMuted', 'TrackUnmuted', 'ParticipantNameChanged', 'ActiveSpeakersChanged'])
        room.on(LK.RoomEvent[event], render);
    room.on(LK.RoomEvent.TrackUnsubscribed, track => {
        unsubscribed.add(track);
        // Reconciliation ignores this object even if the SDK publication still references it.
        render();
    });
    room.on(LK.RoomEvent.TrackSubscribed, track => {
        unsubscribed.delete(track);
        render();
    });
    room.on(LK.RoomEvent.Disconnected, endSignal);
    room.connect(query.get('url'), query.get('token'), { autoSubscribe: true }).then(async () => {
        connected = true;
        // Egress Chromium permits autoplay; startAudio also resumes the SDK audio context.
        try { await room.startAudio(); } catch { /* media elements continue attempting playback */ }
        render();
        fallback = setTimeout(() => {
            // Only an actually empty room may use the timeout; pending media must wait.
            if (!participants().length) root.requestAnimationFrame(() => startSignal(true));
        }, 3000);
    }).catch(() => {
        // No error object/URL logging: SDK errors can contain connection credentials.
        console.error('Recording connection failed'); endSignal();
    });
})(typeof window === 'object' ? window : globalThis);
