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

    let peerConnection = null;
    let localStream = null;
    let remoteAudioEl = null;
    let dotNetRef = null;
    let currentCallId = null;
    let isCallerSide = false;
    let connectTimer = null;
    let restartAttempts = 0;
    let restartTimer = null;

    let inputDeviceId = null;
    let outputDeviceId = null;

    const analysers = { local: null, remote: null, test: null };
    // The MediaStreamSource feeding each analyser: disconnected when replaced (device switch,
    // a renegotiated remote track) so old graphs don't pile up in the AudioContext.
    const meterSources = { local: null, remote: null, test: null };
    let meterFrame = null;
    let testStream = null;

    const defaultIceServers = [{ urls: ['stun:stun.l.google.com:19302'] }];
    let rtcConfig = { iceServers: defaultIceServers };

    const CONNECT_TIMEOUT_MS = 20000;
    // A dropped network: either side restarts ICE, a few times, before calling it lost.
    const MAX_ICE_RESTARTS = 4;
    const DISCONNECT_RESTART_MS = 4000;

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
            const gen = generation;
            const stream = await openMic(inputDeviceId);
            if (gen !== generation) {
                // endCall ran while the prompt was open (hung up, or the call ended): release it.
                stream.getTracks().forEach(t => t.stop());
                return { ok: false, code: 'ended', message: 'The call ended.' };
            }
            localStream = stream;
            return { ok: true };
        } catch (e) {
            const err = micErrorMessage(e);
            return { ok: false, code: err.code, message: err.message };
        }
    }

    // ── Tones ────────────────────────────────────────────────────────────────

    function stopAudioTones() {
        if (window.ipopSound) window.ipopSound.stopLoop();
    }

    // Call tones are the files in wwwroot/sounds (js/sounds.js), so they match the notification set
    // and follow the user's volume.
    function playRingtone() {
        stopAudioTones();
        if (window.ipopSound) window.ipopSound.loop('ringtone');
    }

    /// Outgoing ringback (sounds/ringback.mp3).
    function playDialTone() {
        stopAudioTones();
        if (window.ipopSound) window.ipopSound.loop('ringback');
    }

    function playEndTone() {
        stopAudioTones();
        if (window.ipopSound) window.ipopSound.play('hangup');
    }

    // ── Level meters ─────────────────────────────────────────────────────────

    function detachMeter(type) {
        if (meterSources[type]) { try { meterSources[type].disconnect(); } catch { } }
        meterSources[type] = null;
        analysers[type] = null;
    }

    function attachMeter(stream, type) {
        try {
            detachMeter(type);
            const ctx = getAudioContext();
            const source = ctx.createMediaStreamSource(stream);
            const analyser = ctx.createAnalyser();
            analyser.fftSize = 256;
            source.connect(analyser);
            meterSources[type] = source;
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
        detachMeter('test');
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
    // Bumped by endCall: a microphone prompt still open when the call ends must not leave the
    // mic on, and an initCall waiting on it must not build a peer connection for a call that is over.
    let generation = 0;

    /// ICE servers from /api/calls/config (STUN + the LAN TURN). Kept for later inits.
    function configure(iceServers) {
        if (Array.isArray(iceServers) && iceServers.length > 0) {
            rtcConfig = { iceServers };
        }
    }

    let autoplayRetryArmed = false;

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
                if (autoplayRetryArmed) return;   // one pending retry, however often play() is refused
                autoplayRetryArmed = true;
                document.addEventListener('click', () => {
                    autoplayRetryArmed = false;
                    if (remoteAudioEl && remoteAudioEl.srcObject) remoteAudioEl.play().catch(() => { });
                }, { once: true });
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

    function clearRestartTimer() {
        if (restartTimer) { clearTimeout(restartTimer); restartTimer = null; }
    }

    /// New ICE credentials over the same peer connection: recovers a changed network (Wi-Fi to
    /// LAN, VPN, sleep). Either side may start it; the other answers like any offer.
    function restartIce() {
        if (!peerConnection || restartAttempts >= MAX_ICE_RESTARTS) return false;
        if (peerConnection.signalingState !== 'stable') return true; // an offer is already in flight
        restartAttempts++;
        reportState('reconnecting');
        try { if (peerConnection.restartIce) peerConnection.restartIce(); } catch { }
        sendOffer({ iceRestart: true }).catch(e => console.warn('[IPop WebRTC] ICE restart offer failed:', e));
        armConnectTimer();
        return true;
    }

    function onConnectionState() {
        if (!peerConnection) return;
        const s = peerConnection.connectionState;
        if (s === 'connected') {
            clearConnectTimer();
            clearRestartTimer();
            restartAttempts = 0;
            reportState('connected');
        } else if (s === 'connecting' || s === 'new') {
            reportState('connecting');
        } else if (s === 'disconnected') {
            // Usually a blip that ICE recovers from on its own; if it doesn't within a few
            // seconds, restart ICE instead of waiting for "failed".
            reportState('reconnecting');
            clearRestartTimer();
            restartTimer = setTimeout(() => {
                restartTimer = null;
                if (peerConnection && peerConnection.connectionState === 'disconnected') restartIce();
            }, DISCONNECT_RESTART_MS + (isCallerSide ? 0 : 1500)); // caller first: avoids two offers crossing
            armConnectTimer();
        } else if (s === 'failed') {
            clearRestartTimer();
            if (!restartIce()) {
                clearConnectTimer();
                reportState('failed');
                reportError('The audio connection dropped. Try calling again.');
            }
        }
    }

    /// The other side reloaded or reconnected and will send a fresh offer: drop our dead
    /// peer connection (keep the mic) and answer as a new one.
    async function resetPeer(callId) {
        if (currentCallId && currentCallId !== callId) return;
        clearConnectTimer();
        clearRestartTimer();
        candidateQueue = [];
        if (peerConnection) { try { peerConnection.close(); } catch { } peerConnection = null; }
        await initCall(callId, false, dotNetRef);
        reportState('reconnecting');
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
        restartAttempts = 0;

        if (typeof RTCPeerConnection === 'undefined') {
            const err = 'This browser doesn\'t support voice calls (WebRTC).';
            reportError(err);
            reportState('failed');
            return { success: false, error: err };
        }

        const gen = generation;
        initPromise = (async () => {
            try {
                reportState('connecting');
                const ctx = getAudioContext();
                if (ctx && ctx.state === 'suspended') await ctx.resume().catch(() => { });

                const mic = await prepareMic();
                if (gen !== generation) return { success: false, error: 'ended' };   // hung up meanwhile
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
                startStats();
                if (isCaller) await sendOffer({ offerToReceiveAudio: true, offerToReceiveVideo: false });
                return { success: true };
            } catch (err) {
                const msg = err.message || 'Couldn\'t start audio.';
                reportError(msg);
                reportState('failed');
                return { success: false, error: msg };
            }
        })();

        const mine = initPromise;
        try {
            return await mine;
        } finally {
            if (initPromise === mine) initPromise = null;
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
                // Both sides restarted ICE at once: roll ours back and take theirs.
                if (peerConnection.signalingState === 'have-local-offer') {
                    try { await peerConnection.setLocalDescription({ type: 'rollback' }); } catch { }
                }
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
        generation++;
        stopAudioTones();
        clearConnectTimer();
        clearRestartTimer();
        stopStats();
        restartAttempts = 0;
        candidateQueue = [];
        initPromise = null;
        if (localStream) { localStream.getTracks().forEach(track => track.stop()); localStream = null; }
        if (peerConnection) { peerConnection.close(); peerConnection = null; }
        if (remoteAudioEl) { remoteAudioEl.srcObject = null; remoteAudioEl.pause(); }
        detachMeter('local');
        detachMeter('remote');
        levels.local = 0;
        levels.remote = 0;
        document.querySelectorAll('.avatar-local-speaker, .avatar-remote-speaker').forEach(el => el.classList.remove('speaking'));
        currentCallId = null;
    }

    // ── Link quality (getStats every 5 s) ────────────────────────────────────
    // Packet loss, jitter and round trip of the received audio give "good" | "fair" | "poor",
    // reported only on change (OnQualityChanged): the in-call "weak connection" hint + history.
    let statsTimer = null;
    let lastStats = null;
    let lastQuality = null;

    function startStats() {
        stopStats();
        statsTimer = setInterval(sampleStats, 5000);
    }

    function stopStats() {
        if (statsTimer) { clearInterval(statsTimer); statsTimer = null; }
        lastStats = null;
        lastQuality = null;
    }

    async function sampleStats() {
        if (!peerConnection || peerConnection.connectionState !== 'connected') return;
        try {
            const report = await peerConnection.getStats();
            let inbound = null, rtt = null;
            report.forEach(s => {
                if (s.type === 'inbound-rtp' && s.kind === 'audio') inbound = s;
                if (s.type === 'candidate-pair' && s.state === 'succeeded' && s.nominated && typeof s.currentRoundTripTime === 'number') rtt = s.currentRoundTripTime;
            });
            if (!inbound) return;
            const prev = lastStats;
            lastStats = { lost: inbound.packetsLost || 0, recv: inbound.packetsReceived || 0 };
            if (!prev) return;
            const lost = Math.max(0, lastStats.lost - prev.lost);
            const recv = Math.max(0, lastStats.recv - prev.recv);
            const q = classifyQuality(lost + recv > 0 ? lost / (lost + recv) : 0, inbound.jitter || 0, rtt);
            if (q !== lastQuality) { lastQuality = q; notify('OnQualityChanged', q); }
        } catch { }
    }

    /// Roughly where a voice call stops sounding fine (Opus hides a little loss).
    function classifyQuality(loss, jitter, rtt) {
        if (loss > 0.08 || jitter > 0.1 || (rtt !== null && rtt > 0.6)) return 'poor';
        if (loss > 0.03 || jitter > 0.04 || (rtt !== null && rtt > 0.3)) return 'fair';
        return 'good';
    }

    // ── Headset plugged / unplugged mid-call ─────────────────────────────────
    // The mic in use disappeared (USB headset pulled): fall back to the default microphone
    // without renegotiating (replaceTrack), and tell the user once.
    async function onDeviceChange() {
        if (!peerConnection || !localStream) return;
        const track = localStream.getAudioTracks()[0];
        const ended = !track || track.readyState === 'ended';
        let present = true;
        if (inputDeviceId) {
            try {
                const all = await navigator.mediaDevices.enumerateDevices();
                present = all.some(d => d.kind === 'audioinput' && d.deviceId === inputDeviceId);
            } catch { }
        }
        if (!ended && present) return;
        const r = await setInputDevice(present ? inputDeviceId : '');
        if (r.ok) notify('OnDeviceFallback', present ? 'restarted' : 'unplugged');
    }
    if (navigator.mediaDevices && navigator.mediaDevices.addEventListener) {
        navigator.mediaDevices.addEventListener('devicechange', () => { onDeviceChange().catch(() => { }); });
    }

    /// "Reconnect audio" after a failure: fresh ICE over the same peer connection, attempts reset.
    function reconnect() {
        if (!peerConnection) return false;
        restartAttempts = 0;
        if (peerConnection.signalingState === 'have-local-offer') {
            peerConnection.setLocalDescription({ type: 'rollback' }).catch(() => { });
        }
        return restartIce();
    }

    /// The hub dropped and came back: is the media itself still fine? Then nothing needs renegotiating.
    function isHealthy(callId) {
        return !!peerConnection && currentCallId === callId
            && (peerConnection.connectionState === 'connected' || peerConnection.connectionState === 'connecting');
    }

    window.ipopAudio = {
        reconnect,
        isHealthy,
        classifyQuality,
        checkSupport,
        prepareMic,
        configure,
        playRingtone,
        playDialTone,
        playEndTone,
        stopAudioTones,
        initCall,
        handleSignal,
        resetPeer,
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
