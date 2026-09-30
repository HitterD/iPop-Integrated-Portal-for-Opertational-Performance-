// webrtc-audio.js — 1:1 voice calls (peer-to-peer WebRTC over STUN/TURN) + call tones for IPop.
// .NET drives it through window.ipopAudio; state flows back via the CallState DotNetObjectReference:
//   OnOfferGenerated / OnAnswerGenerated / OnIceCandidateGenerated — signaling to relay over SignalR
//   OnConnectionStateChanged("connecting" | "connected" | "reconnecting" | "failed")
//   OnWebRtcError(message) — shown in the call overlay
// Levels are written straight to the DOM (no interop per frame): any element with
// data-meter="local" | "remote" | "test" gets the CSS variable --level (0..1), and
// .avatar-local-speaker / .avatar-remote-speaker get .speaking while that side talks.
(function () {
    let audioCtx = null;
    let ringOsc1 = null;
    let ringOsc2 = null;
    let ringGain = null;
    let ringInterval = null;

    let peerConnection = null;
    let localStream = null;
    let remoteAudioEl = null;
    let dotNetRef = null;
    let currentCallId = null;
    let isCallerSide = false;
    let connectTimer = null;
    let restartedIce = false;

    let inputDeviceId = null;
    let outputDeviceId = null;

    const analysers = { local: null, remote: null, test: null };
    let meterFrame = null;
    let testStream = null;

    const defaultIceServers = [{ urls: ['stun:stun.l.google.com:19302'] }];
    let rtcConfig = { iceServers: defaultIceServers };

    const CONNECT_TIMEOUT_MS = 20000;

    function notify(method, ...args) {
        if (!dotNetRef) return;
        try { dotNetRef.invokeMethodAsync(method, ...args).catch(() => { }); } catch (e) { }
    }

    function reportError(message) {
        console.warn('[IPop Calls]', message);
        notify('OnWebRtcError', message);
    }

    function reportState(state) {
        notify('OnConnectionStateChanged', state);
    }

    function getAudioContext() {
        if (!audioCtx || audioCtx.state === 'closed') {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        }
        if (audioCtx.state === 'suspended') {
            audioCtx.resume().catch(() => { });
        }
        return audioCtx;
    }

    // ── Environment checks ───────────────────────────────────────────────────

    /// What the browser allows here. The microphone is only exposed on https or localhost.
    function checkSupport() {
        return {
            secure: !!window.isSecureContext,
            mediaDevices: !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia),
            webRtc: typeof RTCPeerConnection !== 'undefined',
            outputSelect: typeof HTMLMediaElement !== 'undefined' && 'setSinkId' in HTMLMediaElement.prototype
        };
    }

    function micErrorMessage(err) {
        switch (err && err.name) {
            case 'NotAllowedError':
            case 'SecurityError':
                return { code: 'denied', message: 'Microphone permission was denied. Click the lock icon in the address bar, allow Microphone, then try again.' };
            case 'NotFoundError':
            case 'DevicesNotFoundError':
            case 'OverconstrainedError':
                return { code: 'no-device', message: 'No microphone detected. Plug in a headset or mic, or continue in listen-only mode.' };
            case 'NotReadableError':
            case 'TrackStartError':
                return { code: 'busy', message: 'The microphone is being used by another app (Zoom/Teams?). Close that app, then try again.' };
            default:
                return { code: 'error', message: 'Couldn\'t open the microphone: ' + ((err && err.message) || 'unknown error') };
        }
    }

    const insecureMessage = 'Calls need HTTPS: browsers only allow the microphone on https or localhost pages.';

    async function openMic(deviceId) {
        const base = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
        const audio = deviceId ? { ...base, deviceId: { exact: deviceId } } : base;
        try {
            return await navigator.mediaDevices.getUserMedia({ audio, video: false });
        } catch (e) {
            // A remembered device may have been unplugged — fall back to the default one.
            if (deviceId && (e.name === 'OverconstrainedError' || e.name === 'NotFoundError')) {
                return await navigator.mediaDevices.getUserMedia({ audio: base, video: false });
            }
            throw e;
        }
    }

    /// Pre-call check: asks for the microphone before we ring anyone, so a denied
    /// permission is reported to the caller instead of producing a silent call.
    async function prepareMic(deviceId) {
        if (deviceId !== undefined) inputDeviceId = deviceId || null;
        const support = checkSupport();
        if (!support.mediaDevices) {
            return { ok: false, code: support.secure ? 'unsupported' : 'insecure', message: support.secure ? 'This browser doesn\'t support the microphone.' : insecureMessage };
        }
        if (localStream && localStream.getAudioTracks().some(t => t.readyState === 'live')) {
            return { ok: true };
        }
        try {
            localStream = await openMic(inputDeviceId);
            return { ok: true };
        } catch (e) {
            const err = micErrorMessage(e);
            return { ok: false, code: err.code, message: err.message };
        }
    }

    // ── Tones ────────────────────────────────────────────────────────────────

    function stopAudioTones() {
        if (ringInterval) {
            clearInterval(ringInterval);
            ringInterval = null;
        }
        try {
            if (ringOsc1) { ringOsc1.stop(); ringOsc1.disconnect(); ringOsc1 = null; }
            if (ringOsc2) { ringOsc2.stop(); ringOsc2.disconnect(); ringOsc2 = null; }
            if (ringGain) { ringGain.disconnect(); ringGain = null; }
        } catch (e) { }
    }

    function startDualTone(ctx, f1, f2, volume) {
        ringGain = ctx.createGain();
        ringGain.gain.setValueAtTime(0, ctx.currentTime);
        ringOsc1 = ctx.createOscillator();
        ringOsc1.type = 'sine';
        ringOsc1.frequency.setValueAtTime(f1, ctx.currentTime);
        ringOsc2 = ctx.createOscillator();
        ringOsc2.type = 'sine';
        ringOsc2.frequency.setValueAtTime(f2, ctx.currentTime);
        ringOsc1.connect(ringGain);
        ringOsc2.connect(ringGain);
        ringGain.connect(ctx.destination);
        ringOsc1.start();
        ringOsc2.start();
        return (on) => {
            try { if (ringGain) ringGain.gain.setTargetAtTime(on ? volume : 0, ctx.currentTime, 0.015); } catch (e) { }
        };
    }

    /// Incoming ring: two short bursts every 3 s.
    function playRingtone() {
        stopAudioTones();
        try {
            const set = startDualTone(getAudioContext(), 440, 480, 0.16);
            const burst = () => {
                set(true);
                setTimeout(() => set(false), 400);
                setTimeout(() => set(true), 600);
                setTimeout(() => set(false), 1000);
            };
            burst();
            ringInterval = setInterval(burst, 3000);
        } catch (e) { console.warn('Ringtone error:', e); }
    }

    /// Outgoing ringback: 1 s tone, 3 s silence.
    function playDialTone() {
        stopAudioTones();
        try {
            const set = startDualTone(getAudioContext(), 440, 480, 0.08);
            const beep = () => { set(true); setTimeout(() => set(false), 1000); };
            beep();
            ringInterval = setInterval(beep, 4000);
        } catch (e) { console.warn('Dial tone error:', e); }
    }

    /// Short descending beep when a call ends.
    function playEndTone() {
        stopAudioTones();
        try {
            const ctx = getAudioContext();
            const gain = ctx.createGain();
            gain.connect(ctx.destination);
            [[620, 0], [480, 0.18]].forEach(([f, at]) => {
                const osc = ctx.createOscillator();
                osc.frequency.setValueAtTime(f, ctx.currentTime + at);
                osc.connect(gain);
                osc.start(ctx.currentTime + at);
                osc.stop(ctx.currentTime + at + 0.15);
            });
            gain.gain.setValueAtTime(0.08, ctx.currentTime);
            gain.gain.setValueAtTime(0, ctx.currentTime + 0.36);
        } catch (e) { }
    }

    // ── Level meters ─────────────────────────────────────────────────────────

    function attachMeter(stream, type) {
        try {
            const ctx = getAudioContext();
            const source = ctx.createMediaStreamSource(stream);
            const analyser = ctx.createAnalyser();
            analyser.fftSize = 256;
            source.connect(analyser);
            analysers[type] = analyser;
            if (!meterFrame) meterFrame = requestAnimationFrame(tickMeters);
        } catch (e) {
            console.warn('Meter setup error:', e);
        }
    }

    const levelBuffer = new Uint8Array(256);
    function levelOf(analyser) {
        analyser.getByteTimeDomainData(levelBuffer);
        let sum = 0;
        for (let i = 0; i < levelBuffer.length; i++) {
            const v = (levelBuffer[i] - 128) / 128;
            sum += v * v;
        }
        // RMS → a perceptual-ish 0..1 (speech sits around 0.02–0.2 RMS).
        return Math.min(1, Math.sqrt(sum / levelBuffer.length) * 4);
    }

    const levels = { local: 0, remote: 0, test: 0 };
    function tickMeters() {
        meterFrame = null;
        let any = false;
        for (const type of ['local', 'remote', 'test']) {
            const analyser = analysers[type];
            if (!analyser) continue;
            any = true;
            let level = levelOf(analyser);
            if (type === 'local' && localStream && !localStream.getAudioTracks().some(t => t.enabled)) level = 0;
            // Fast attack, slow release so the bar does not flicker.
            levels[type] = level > levels[type] ? level : levels[type] * 0.85 + level * 0.15;
            document.querySelectorAll(`[data-meter="${type}"]`).forEach(el => el.style.setProperty('--level', levels[type].toFixed(3)));
            if (type !== 'test') {
                document.querySelectorAll(`.avatar-${type}-speaker`).forEach(el => el.classList.toggle('speaking', levels[type] > 0.12));
            }
        }
        if (any) meterFrame = requestAnimationFrame(tickMeters);
    }

    function getLevels() {
        return { local: levels.local, remote: levels.remote, test: levels.test };
    }

    // ── Devices ──────────────────────────────────────────────────────────────

    async function listDevices() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) return { inputs: [], outputs: [] };
        const all = await navigator.mediaDevices.enumerateDevices();
        const map = (kind, fallback) => all
            .filter(d => d.kind === kind && d.deviceId !== 'communications')
            .map((d, i) => ({ id: d.deviceId === 'default' ? '' : d.deviceId, label: d.label || `${fallback} ${i + 1}` }));
        return { inputs: map('audioinput', 'Microphone'), outputs: map('audiooutput', 'Speaker') };
    }

    /// Switch microphone mid-call without renegotiating (replaceTrack on the audio sender).
    async function setInputDevice(deviceId) {
        inputDeviceId = deviceId || null;
        if (!localStream && !peerConnection) return { ok: true };
        try {
            const fresh = await openMic(inputDeviceId);
            const track = fresh.getAudioTracks()[0];
            const wasMuted = localStream ? !localStream.getAudioTracks().some(t => t.enabled) : false;
            track.enabled = !wasMuted;
            if (peerConnection) {
                const sender = peerConnection.getSenders().find(s => s.track && s.track.kind === 'audio')
                    || peerConnection.getTransceivers().map(t => t.sender).find(s => !s.track);
                if (sender) await sender.replaceTrack(track);
            }
            if (localStream) localStream.getTracks().forEach(t => t.stop());
            localStream = fresh;
            attachMeter(localStream, 'local');
            return { ok: true };
        } catch (e) {
            const err = micErrorMessage(e);
            reportError(err.message);
            return { ok: false, code: err.code, message: err.message };
        }
    }

    async function setOutputDevice(deviceId) {
        outputDeviceId = deviceId || null;
        if (remoteAudioEl && remoteAudioEl.setSinkId) {
            try { await remoteAudioEl.setSinkId(outputDeviceId || ''); }
            catch (e) { reportError('Couldn\'t select the speaker: ' + e.message); return false; }
        }
        return true;
    }

    // ── Device test (no call needed) ─────────────────────────────────────────

    async function startMicTest(deviceId) {
        stopMicTest();
        const support = checkSupport();
        if (!support.mediaDevices) {
            return { ok: false, code: support.secure ? 'unsupported' : 'insecure', message: support.secure ? 'This browser doesn\'t support the microphone.' : insecureMessage };
        }
        try {
            testStream = await openMic(deviceId || null);
            attachMeter(testStream, 'test');
            const label = testStream.getAudioTracks()[0]?.label || '';
            return { ok: true, label };
        } catch (e) {
            const err = micErrorMessage(e);
            return { ok: false, code: err.code, message: err.message };
        }
    }

    function stopMicTest() {
        if (testStream) testStream.getTracks().forEach(t => t.stop());
        testStream = null;
        analysers.test = null;
        levels.test = 0;
        document.querySelectorAll('[data-meter="test"]').forEach(el => el.style.setProperty('--level', '0'));
    }

    /// Plays a short chime on the chosen speaker (setSinkId where supported).
    async function playTestTone(deviceId) {
        const ctx = new (window.AudioContext || window.webkitAudioContext)();
        const dest = ctx.createMediaStreamDestination();
        const gain = ctx.createGain();
        gain.connect(dest);
        [523.25, 659.25, 783.99].forEach((f, i) => {
            const osc = ctx.createOscillator();
            osc.frequency.value = f;
            osc.connect(gain);
            osc.start(ctx.currentTime + i * 0.18);
            osc.stop(ctx.currentTime + i * 0.18 + 0.22);
        });
        gain.gain.setValueAtTime(0.15, ctx.currentTime);
        const el = new Audio();
        el.srcObject = dest.stream;
        try {
            if (deviceId && el.setSinkId) await el.setSinkId(deviceId);
            await el.play();
        } catch (e) {
            ctx.close();
            return { ok: false, message: 'The speaker couldn\'t play sound: ' + e.message };
        }
        setTimeout(() => { el.pause(); el.srcObject = null; ctx.close(); }, 900);
        return { ok: true };
    }

    // ── WebRTC ───────────────────────────────────────────────────────────────

    let initPromise = null;
    let candidateQueue = [];

    /// ICE servers from /api/calls/config (STUN + the LAN TURN). Kept for later inits.
    function configure(iceServers) {
        if (Array.isArray(iceServers) && iceServers.length > 0) {
            rtcConfig = { iceServers };
        }
    }

    function ensureRemoteAudio(stream) {
        if (!remoteAudioEl) {
            remoteAudioEl = document.getElementById('ipop-remote-audio');
            if (!remoteAudioEl) {
                remoteAudioEl = document.createElement('audio');
                remoteAudioEl.id = 'ipop-remote-audio';
                remoteAudioEl.autoplay = true;
                remoteAudioEl.playsInline = true;
                // Off-screen rather than display:none so browsers never pause or throttle playback.
                Object.assign(remoteAudioEl.style, { position: 'fixed', left: '-9999px', top: '-9999px', width: '1px', height: '1px', opacity: '0' });
                document.body.appendChild(remoteAudioEl);
            }
        }
        remoteAudioEl.srcObject = stream;
        remoteAudioEl.volume = 1.0;
        remoteAudioEl.muted = false;
        if (outputDeviceId && remoteAudioEl.setSinkId) remoteAudioEl.setSinkId(outputDeviceId).catch(() => { });

        const playPromise = remoteAudioEl.play();
        if (playPromise !== undefined) {
            playPromise.catch(() => {
                reportError('The browser blocked the other person\'s audio. Click anywhere on the page to turn on audio.');
                document.addEventListener('click', () => { if (remoteAudioEl) remoteAudioEl.play().catch(() => { }); }, { once: true });
            });
        }
    }

    async function drainCandidateQueue() {
        if (!peerConnection || !peerConnection.remoteDescription) return;
        while (candidateQueue.length > 0) {
            const cand = candidateQueue.shift();
            try { await peerConnection.addIceCandidate(new RTCIceCandidate(cand)); }
            catch (err) { console.warn('[IPop WebRTC] Failed to add queued candidate:', err); }
        }
    }

    function clearConnectTimer() {
        if (connectTimer) { clearTimeout(connectTimer); connectTimer = null; }
    }

    function armConnectTimer() {
        clearConnectTimer();
        connectTimer = setTimeout(() => {
            if (!peerConnection || peerConnection.connectionState === 'connected') return;
            reportState('failed');
            reportError('Audio couldn\'t connect within 20 seconds. A network or firewall may be blocking it; try calling again.');
        }, CONNECT_TIMEOUT_MS);
    }

    async function sendOffer(options) {
        const offer = await peerConnection.createOffer(options);
        await peerConnection.setLocalDescription(offer);
        notify('OnOfferGenerated', currentCallId, JSON.stringify(offer));
    }

    function onConnectionState() {
        if (!peerConnection) return;
        const s = peerConnection.connectionState;
        if (s === 'connected') {
            clearConnectTimer();
            restartedIce = false;
            reportState('connected');
        } else if (s === 'connecting' || s === 'new') {
            reportState('connecting');
        } else if (s === 'disconnected') {
            // Usually a network blip; ICE recovers on its own within a few seconds.
            reportState('reconnecting');
            armConnectTimer();
        } else if (s === 'failed') {
            if (isCallerSide && !restartedIce && peerConnection.restartIce) {
                restartedIce = true;
                reportState('reconnecting');
                peerConnection.restartIce();
                sendOffer({ iceRestart: true }).catch(e => reportError('Couldn\'t reconnect: ' + e.message));
                armConnectTimer();
            } else {
                clearConnectTimer();
                reportState('failed');
                reportError('The audio connection dropped. Try calling again.');
            }
        }
    }

    async function initCall(callId, isCaller, helper, iceServers) {
        dotNetRef = helper || dotNetRef;
        configure(iceServers);
        stopAudioTones();

        // Already set up for this call (e.g. an early offer started the callee side).
        if (peerConnection && currentCallId === callId) return { success: true };
        if (initPromise) return initPromise;

        if (currentCallId !== callId) candidateQueue = [];
        currentCallId = callId;
        isCallerSide = !!isCaller;
        restartedIce = false;

        if (typeof RTCPeerConnection === 'undefined') {
            const err = 'This browser doesn\'t support voice calls (WebRTC).';
            reportError(err);
            reportState('failed');
            return { success: false, error: err };
        }

        initPromise = (async () => {
            try {
                reportState('connecting');
                const ctx = getAudioContext();
                if (ctx && ctx.state === 'suspended') await ctx.resume().catch(() => { });

                const mic = await prepareMic();
                if (!mic.ok) {
                    // Keep the call alive in listen-only mode; tell the user why they can't be heard.
                    reportError(mic.message + ' (You can still listen.)');
                }
                if (localStream) {
                    localStream.getAudioTracks().forEach(track => { track.enabled = true; });
                    attachMeter(localStream, 'local');
                }

                if (peerConnection) { peerConnection.close(); peerConnection = null; }
                peerConnection = new RTCPeerConnection(rtcConfig);

                if (localStream) {
                    localStream.getAudioTracks().forEach(track => peerConnection.addTrack(track, localStream));
                } else if (isCaller) {
                    peerConnection.addTransceiver('audio', { direction: 'recvonly' });
                }

                peerConnection.ontrack = (event) => {
                    const stream = (event.streams && event.streams[0]) ? event.streams[0] : new MediaStream([event.track]);
                    ensureRemoteAudio(stream);
                    attachMeter(stream, 'remote');
                };
                peerConnection.onicecandidate = (event) => {
                    if (event.candidate && currentCallId) {
                        notify('OnIceCandidateGenerated', currentCallId, JSON.stringify(event.candidate));
                    }
                };
                peerConnection.onconnectionstatechange = onConnectionState;

                armConnectTimer();
                if (isCaller) await sendOffer({ offerToReceiveAudio: true, offerToReceiveVideo: false });
                return { success: true };
            } catch (err) {
                const msg = err.message || 'Couldn\'t start audio.';
                reportError(msg);
                reportState('failed');
                return { success: false, error: msg };
            }
        })();

        try {
            return await initPromise;
        } finally {
            initPromise = null;
        }
    }

    async function handleSignal(callId, type, payload) {
        try {
            if (!peerConnection && !initPromise) {
                if (type === 'candidate') {
                    candidateQueue.push(JSON.parse(payload));
                    return;
                }
                // The offer can beat our own "active" state update — set up the callee side now.
                await initCall(callId, false, dotNetRef);
            }
            if (initPromise) await initPromise;
            if (!peerConnection || callId !== currentCallId) return;

            if (type === 'offer') {
                await peerConnection.setRemoteDescription(new RTCSessionDescription(JSON.parse(payload)));
                await drainCandidateQueue();
                const answer = await peerConnection.createAnswer();
                await peerConnection.setLocalDescription(answer);
                notify('OnAnswerGenerated', callId, JSON.stringify(answer));
            } else if (type === 'answer') {
                // Only an answer to our own pending offer applies; a late or duplicate
                // answer (already "stable") is harmless and must not surface as an error.
                if (peerConnection.signalingState !== 'have-local-offer') return;
                await peerConnection.setRemoteDescription(new RTCSessionDescription(JSON.parse(payload)));
                await drainCandidateQueue();
            } else if (type === 'candidate') {
                const candidate = JSON.parse(payload);
                if (peerConnection.remoteDescription && peerConnection.remoteDescription.type) {
                    await peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
                } else {
                    candidateQueue.push(candidate);
                }
            }
        } catch (err) {
            console.error('[IPop WebRTC] Error handling signal:', type, err);
            reportError(`Couldn't process the call signal (${type}): ${err.message}`);
        }
    }

    function setMute(isMuted) {
        if (localStream) localStream.getAudioTracks().forEach(track => { track.enabled = !isMuted; });
    }

    function endCall() {
        stopAudioTones();
        clearConnectTimer();
        candidateQueue = [];
        initPromise = null;
        if (localStream) { localStream.getTracks().forEach(track => track.stop()); localStream = null; }
        if (peerConnection) { peerConnection.close(); peerConnection = null; }
        if (remoteAudioEl) { remoteAudioEl.srcObject = null; remoteAudioEl.pause(); }
        analysers.local = null;
        analysers.remote = null;
        levels.local = 0;
        levels.remote = 0;
        document.querySelectorAll('.avatar-local-speaker, .avatar-remote-speaker').forEach(el => el.classList.remove('speaking'));
        currentCallId = null;
    }

    window.ipopAudio = {
        checkSupport,
        prepareMic,
        configure,
        playRingtone,
        playDialTone,
        playEndTone,
        stopAudioTones,
        initCall,
        handleSignal,
        setMute,
        endCall,
        getLevels,
        listDevices,
        setInputDevice,
        setOutputDevice,
        startMicTest,
        stopMicTest,
        playTestTone
    };
})();
