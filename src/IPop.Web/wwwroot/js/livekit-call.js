// Group calls over the LiveKit SFU, audio only. .NET (CallState) drives it through
// window.ipopGroupCall and receives state back through a DotNetObjectReference:
//   OnGroupConnectionState(state)   "connecting" | "connected" | "reconnecting" | "disconnected" | "failed"
//   OnGroupParticipants(list)       [{ identity, name, speaking, muted, level, isLocal }] (throttled ~150 ms)
//   OnGroupError(message)
// 1:1 calls stay on the P2P path in webrtc-audio.js.
(function () {
    const LK = () => window.LivekitClient;
    let room = null;
    let mic = null;
    let dotnet = null;
    let audioHost = null;
    let outputDeviceId = null;
    let reportTimer = null;
    let levelTimer = null;
    let lastReport = '';

    function log(...a) { console.debug('[IPop GroupCall]', ...a); }

    /// On https the page cannot open ws:// (mixed content) and the LAN address may not be
    /// reachable: use the page host, where the reverse proxy forwards /rtc to LiveKit.
    function resolveUrl(url) {
        if (location.protocol === 'https:') return 'wss://' + location.host;
        return url;
    }

    function host() {
        if (audioHost && document.body.contains(audioHost)) return audioHost;
        audioHost = document.createElement('div');
        audioHost.id = 'ipop-groupcall-audio';
        audioHost.hidden = true;
        document.body.appendChild(audioHost);
        return audioHost;
    }

    function attach(track, _pub, participant) {
        if (track.kind !== 'audio') return;
        const el = track.attach();
        el.dataset.identity = participant?.identity || '';
        el.autoplay = true;
        if (outputDeviceId && typeof el.setSinkId === 'function') el.setSinkId(outputDeviceId).catch(() => { });
        host().appendChild(el);
        schedule();
    }

    function detach(track) {
        track.detach().forEach(el => el.remove());
        schedule();
    }

    function snapshot() {
        if (!room) return [];
        const list = [];
        const lp = room.localParticipant;
        list.push({
            identity: lp.identity, name: lp.name || lp.identity, isLocal: true,
            speaking: !!lp.isSpeaking, muted: !lp.isMicrophoneEnabled, level: Math.round((lp.audioLevel || 0) * 100) / 100,
        });
        room.remoteParticipants.forEach(p => {
            const pub = p.getTrackPublication?.(LK().Track.Source.Microphone);
            list.push({
                identity: p.identity, name: p.name || p.identity, isLocal: false,
                speaking: !!p.isSpeaking, muted: pub ? !!pub.isMuted : true,
                level: Math.round((p.audioLevel || 0) * 100) / 100,
            });
        });
        return list;
    }

    // Participants/speakers are pushed at most every 150 ms, and only when something changed.
    function schedule() {
        if (reportTimer) return;
        reportTimer = setTimeout(() => {
            reportTimer = null;
            if (!dotnet || !room) return;
            const list = snapshot();
            const key = JSON.stringify(list.map(p => [p.identity, p.speaking, p.muted, Math.round(p.level * 10)]));
            if (key === lastReport) return;
            lastReport = key;
            dotnet.invokeMethodAsync('OnGroupParticipants', list).catch(() => { });
        }, 150);
    }

    function state(s) {
        log('state', s);
        if (dotnet) dotnet.invokeMethodAsync('OnGroupConnectionState', s).catch(() => { });
    }

    function error(msg) {
        if (dotnet) dotnet.invokeMethodAsync('OnGroupError', msg).catch(() => { });
    }

    function micError(e) {
        const n = e?.name || '';
        if (n === 'NotAllowedError' || n === 'SecurityError') return 'Microphone access was denied. Allow the microphone in your browser to talk.';
        if (n === 'NotFoundError' || n === 'OverconstrainedError') return 'No microphone found. You can only listen.';
        if (n === 'NotReadableError') return 'The microphone is being used by another app.';
        return 'The microphone can\'t be used: ' + (e?.message || n);
    }

    async function leave() {
        clearTimeout(reportTimer); reportTimer = null;
        clearInterval(levelTimer); levelTimer = null;
        lastReport = '';
        const prev = room;
        room = null;
        if (mic) { try { mic.stop(); } catch { } mic = null; }
        if (prev) { try { await prev.disconnect(); } catch { } }
        if (audioHost) { audioHost.querySelectorAll('audio').forEach(a => a.remove()); }
    }

    async function join(url, token, dotnetRef, inputDeviceId) {
        await leave();
        dotnet = dotnetRef;
        const lk = LK();
        if (!lk?.Room) { error('The group media client isn\'t available.'); state('failed'); return false; }
        const target = resolveUrl(url);
        const next = new lk.Room({
            adaptiveStream: false,
            dynacast: false,
            audioCaptureDefaults: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, deviceId: inputDeviceId || undefined },
        });
        const E = lk.RoomEvent;
        next.on(E.TrackSubscribed, attach)
            .on(E.TrackUnsubscribed, detach)
            .on(E.ParticipantConnected, schedule)
            .on(E.ParticipantDisconnected, schedule)
            .on(E.TrackMuted, schedule)
            .on(E.TrackUnmuted, schedule)
            .on(E.ActiveSpeakersChanged, schedule)
            .on(E.LocalTrackPublished, schedule)
            .on(E.Reconnecting, () => state('reconnecting'))
            .on(E.SignalReconnecting, () => state('reconnecting'))
            .on(E.Reconnected, () => { state('connected'); schedule(); })
            .on(E.AudioPlaybackStatusChanged, () => {
                if (!next.canPlaybackAudio) error('Click anywhere on the page to turn on call audio.');
            })
            .on(E.Disconnected, reason => {
                if (room !== next) return;
                log('disconnected', reason);
                void leave();
                state('disconnected');
            });

        state('connecting');
        try {
            await next.connect(target, token, { autoSubscribe: true });
        } catch (e) {
            log('connect failed', target, e);
            try { await next.disconnect(); } catch { }
            error('Couldn\'t connect to the media server (' + target + '): ' + (e?.message || e));
            state('failed');
            return false;
        }
        room = next;
        state('connected');
        try { await next.startAudio(); } catch { }

        try {
            mic = await lk.createLocalAudioTrack({ echoCancellation: true, noiseSuppression: true, autoGainControl: true, deviceId: inputDeviceId || undefined });
            await next.localParticipant.publishTrack(mic, { source: lk.Track.Source.Microphone, dtx: true, red: true });
        } catch (e) {
            log('mic failed', e);
            mic = null;
            error(micError(e));
        }
        // audioLevel changes without events: poll it for the speaking meters.
        levelTimer = setInterval(schedule, 200);
        schedule();
        return true;
    }

    async function setMuted(muted) {
        if (!room) return;
        try { await room.localParticipant.setMicrophoneEnabled(!muted); } catch (e) { error(micError(e)); }
        schedule();
    }

    async function setInputDevice(id) {
        if (!room) return true;
        try { await room.switchActiveDevice('audioinput', id || 'default'); return true; } catch { return false; }
    }

    async function setOutputDevice(id) {
        outputDeviceId = id || null;
        if (room) { try { await room.switchActiveDevice('audiooutput', id || 'default'); } catch { } }
        if (audioHost) audioHost.querySelectorAll('audio').forEach(a => { if (a.setSinkId) a.setSinkId(id || '').catch(() => { }); });
        return true;
    }

    function isConnected() { return !!room; }

    window.ipopGroupCall = { join, leave, setMuted, setInputDevice, setOutputDevice, isConnected, resolveUrl };
})();
