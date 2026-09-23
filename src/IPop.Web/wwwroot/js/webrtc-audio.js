// webrtc-audio.js — WebRTC Voice Call & Audio Synthesizer for IPop
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

    let localAnalyser = null;
    let remoteAnalyser = null;
    let visualizerTimer = null;

    function getAudioContext() {
        if (!audioCtx || audioCtx.state === 'closed') {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        }
        if (audioCtx.state === 'suspended') {
            audioCtx.resume();
        }
        return audioCtx;
    }

    // --- Audio Tone Synthesizer ---
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

    function playRingtone() {
        stopAudioTones();
        const ctx = getAudioContext();
        let ringState = false;

        const pulseRing = () => {
            try {
                if (ringOsc1) { ringOsc1.stop(); ringOsc1.disconnect(); }
                if (ringOsc2) { ringOsc2.stop(); ringOsc2.disconnect(); }
                if (ringGain) { ringGain.disconnect(); }

                if (!ringState) {
                    ringState = true;
                    ringGain = ctx.createGain();
                    ringGain.gain.setValueAtTime(0.18, ctx.currentTime);

                    ringOsc1 = ctx.createOscillator();
                    ringOsc1.type = 'sine';
                    ringOsc1.frequency.setValueAtTime(440, ctx.currentTime);

                    ringOsc2 = ctx.createOscillator();
                    ringOsc2.type = 'sine';
                    ringOsc2.frequency.setValueAtTime(480, ctx.currentTime);

                    ringOsc1.connect(ringGain);
                    ringOsc2.connect(ringGain);
                    ringGain.connect(ctx.destination);

                    ringOsc1.start();
                    ringOsc2.start();

                    // Ring pulse: 1.5s active, then silence until 3.5s cycle
                    setTimeout(() => {
                        try {
                            if (ringGain) {
                                ringGain.gain.setValueAtTime(0, ctx.currentTime);
                            }
                        } catch (e) { }
                    }, 1500);
                } else {
                    ringState = false;
                }
            } catch (e) {
                console.warn('Ringtone error:', e);
            }
        };

        pulseRing();
        ringInterval = setInterval(pulseRing, 3500);
    }

    function playDialTone() {
        stopAudioTones();
        const ctx = getAudioContext();
        try {
            ringGain = ctx.createGain();
            ringGain.gain.setValueAtTime(0.1, ctx.currentTime);

            ringOsc1 = ctx.createOscillator();
            ringOsc1.type = 'sine';
            ringOsc1.frequency.setValueAtTime(440, ctx.currentTime);

            ringOsc2 = ctx.createOscillator();
            ringOsc2.type = 'sine';
            ringOsc2.frequency.setValueAtTime(480, ctx.currentTime);

            ringOsc1.connect(ringGain);
            ringOsc2.connect(ringGain);
            ringGain.connect(ctx.destination);

            ringOsc1.start();
            ringOsc2.start();

            // Intermittent dial beep: 1.2s tone, 2.5s silence
            const beep = () => {
                try {
                    if (ringGain) ringGain.gain.setValueAtTime(0.1, ctx.currentTime);
                    setTimeout(() => {
                        if (ringGain) ringGain.gain.setValueAtTime(0, ctx.currentTime);
                    }, 1200);
                } catch (e) { }
            };
            beep();
            ringInterval = setInterval(beep, 3700);
        } catch (e) {
            console.warn('Dial tone error:', e);
        }
    }

    // --- Audio Visualizer & Level Meter ---
    function setupVisualizer(stream, type) {
        try {
            const ctx = getAudioContext();
            const source = ctx.createMediaStreamSource(stream);
            const analyser = ctx.createAnalyser();
            analyser.fftSize = 64;
            source.connect(analyser);

            if (type === 'local') localAnalyser = analyser;
            else remoteAnalyser = analyser;

            if (!visualizerTimer) {
                const dataArray = new Uint8Array(32);
                visualizerTimer = setInterval(() => {
                    if (localAnalyser) {
                        localAnalyser.getByteFrequencyData(dataArray);
                        let sum = 0;
                        for (let i = 0; i < dataArray.length; i++) sum += dataArray[i];
                        const avg = sum / dataArray.length;
                        const isSpeaking = avg > 14;
                        document.querySelectorAll('.avatar-local-speaker').forEach(el => {
                            el.classList.toggle('speaking', isSpeaking);
                        });
                    }
                    if (remoteAnalyser) {
                        remoteAnalyser.getByteFrequencyData(dataArray);
                        let sum = 0;
                        for (let i = 0; i < dataArray.length; i++) sum += dataArray[i];
                        const avg = sum / dataArray.length;
                        const isSpeaking = avg > 14;
                        document.querySelectorAll('.avatar-remote-speaker').forEach(el => {
                            el.classList.toggle('speaking', isSpeaking);
                        });
                    }
                }, 100);
            }
        } catch (e) {
            console.warn('Visualizer setup error:', e);
        }
    }

    // --- WebRTC Peer Connection ---
    const rtcConfig = {
        iceServers: [
            { urls: 'stun:stun.l.google.com:19302' },
            { urls: 'stun:stun1.l.google.com:19302' },
            { urls: 'stun:stun2.l.google.com:19302' }
        ]
    };

    let initPromise = null;
    let candidateQueue = [];

    function ensureRemoteAudio(stream) {
        if (!remoteAudioEl) {
            remoteAudioEl = document.getElementById('ipop-remote-audio');
            if (!remoteAudioEl) {
                remoteAudioEl = document.createElement('audio');
                remoteAudioEl.id = 'ipop-remote-audio';
                remoteAudioEl.autoplay = true;
                remoteAudioEl.playsInline = true;
                // Position off-screen so modern browsers never pause or throttle playback
                remoteAudioEl.style.position = 'fixed';
                remoteAudioEl.style.left = '-9999px';
                remoteAudioEl.style.top = '-9999px';
                remoteAudioEl.style.width = '1px';
                remoteAudioEl.style.height = '1px';
                remoteAudioEl.style.opacity = '0';
                document.body.appendChild(remoteAudioEl);
            }
        }
        remoteAudioEl.srcObject = stream;
        remoteAudioEl.volume = 1.0;
        remoteAudioEl.muted = false;

        const playPromise = remoteAudioEl.play();
        if (playPromise !== undefined) {
            playPromise.then(() => {
                console.log('[IPop WebRTC] Remote audio playback active.');
            }).catch(e => {
                console.warn('[IPop WebRTC] Remote audio autoplay blocked by browser:', e);
                const resumeOnGesture = () => {
                    if (remoteAudioEl) remoteAudioEl.play();
                    document.removeEventListener('click', resumeOnGesture);
                };
                document.addEventListener('click', resumeOnGesture, { once: true });
            });
        }
    }

    async function drainCandidateQueue() {
        if (!peerConnection || !peerConnection.remoteDescription) return;
        while (candidateQueue.length > 0) {
            const cand = candidateQueue.shift();
            try {
                await peerConnection.addIceCandidate(new RTCIceCandidate(cand));
                console.log('[IPop WebRTC] Added queued ICE candidate.');
            } catch (err) {
                console.warn('[IPop WebRTC] Failed to add queued candidate:', err);
            }
        }
    }

    async function initCall(callId, isCaller, helper) {
        dotNetRef = helper || dotNetRef;
        currentCallId = callId;
        stopAudioTones();

        if (initPromise) {
            console.log('[IPop WebRTC] initCall already in progress, awaiting existing promise...');
            return initPromise;
        }

        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            const err = 'Microphone access is not supported on this browser context (requires HTTPS or localhost).';
            console.error('[IPop WebRTC]', err);
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnWebRtcError', err);
            return { success: false, error: err };
        }

        initPromise = (async () => {
            try {
                const ctx = getAudioContext();
                if (ctx && ctx.state === 'suspended') {
                    await ctx.resume();
                }

                if (!localStream) {
                    console.log('[IPop WebRTC] Requesting local microphone stream...');
                    try {
                        localStream = await navigator.mediaDevices.getUserMedia({
                            audio: {
                                echoCancellation: true,
                                noiseSuppression: true,
                                autoGainControl: true
                            },
                            video: false
                        });
                    } catch (e1) {
                        console.warn('[IPop WebRTC] Failed with advanced audio constraints, retrying basic audio...', e1);
                        try {
                            localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
                        } catch (e2) {
                            if (e2.name === 'NotFoundError' || e2.name === 'DevicesNotFoundError') {
                                console.warn('[IPop WebRTC] No microphone hardware found on this machine. Entering listen-only mode.');
                                if (dotNetRef) {
                                    dotNetRef.invokeMethodAsync('OnWebRtcError', 'Tidak ada mikrofon terdeteksi (mode hanya mendengar). Colokkan headset/mic jika ingin berbicara.');
                                }
                            } else {
                                throw e2;
                            }
                        }
                    }
                    if (localStream) {
                        console.log('[IPop WebRTC] Local microphone acquired.');
                        setupVisualizer(localStream, 'local');
                    }
                }

                if (localStream) {
                    localStream.getAudioTracks().forEach(track => { track.enabled = true; });
                }

                if (peerConnection) {
                    console.log('[IPop WebRTC] Closing existing peer connection.');
                    peerConnection.close();
                    peerConnection = null;
                }

                console.log('[IPop WebRTC] Initializing RTCPeerConnection...');
                peerConnection = new RTCPeerConnection(rtcConfig);
                candidateQueue = [];

                if (localStream) {
                    localStream.getAudioTracks().forEach(track => {
                        peerConnection.addTrack(track, localStream);
                    });
                }

                peerConnection.ontrack = (event) => {
                    console.log('[IPop WebRTC] Remote audio track received!', event);
                    const stream = (event.streams && event.streams[0])
                        ? event.streams[0]
                        : new MediaStream([event.track]);
                    ensureRemoteAudio(stream);
                    setupVisualizer(stream, 'remote');
                };

                peerConnection.onicecandidate = (event) => {
                    if (event.candidate && dotNetRef && currentCallId) {
                        console.log('[IPop WebRTC] Sending local ICE candidate to peer...');
                        dotNetRef.invokeMethodAsync('OnIceCandidateGenerated', currentCallId, JSON.stringify(event.candidate));
                    }
                };

                peerConnection.onconnectionstatechange = () => {
                    console.log('[IPop WebRTC] Peer connection state:', peerConnection.connectionState);
                    if (dotNetRef && peerConnection) {
                        dotNetRef.invokeMethodAsync('OnConnectionStateChanged', peerConnection.connectionState);
                    }
                };

                peerConnection.oniceconnectionstatechange = () => {
                    console.log('[IPop WebRTC] ICE connection state:', peerConnection.iceConnectionState);
                };

                if (isCaller) {
                    console.log('[IPop WebRTC] Creating offer (caller)...');
                    const offer = await peerConnection.createOffer({
                        offerToReceiveAudio: true,
                        offerToReceiveVideo: false
                    });
                    await peerConnection.setLocalDescription(offer);
                    console.log('[IPop WebRTC] Local offer set, dispatching to peer...');
                    if (dotNetRef) {
                        await dotNetRef.invokeMethodAsync('OnOfferGenerated', currentCallId, JSON.stringify(offer));
                    }
                }

                return { success: true };
            } catch (err) {
                console.error('[IPop WebRTC] Error initializing call:', err);
                const msg = err.name === 'NotAllowedError'
                    ? 'Microphone permission denied by user.'
                    : err.message || 'Failed to acquire microphone.';
                if (dotNetRef) dotNetRef.invokeMethodAsync('OnWebRtcError', msg);
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
        console.log(`[IPop WebRTC] Received signal "${type}" for call:`, callId);

        if (!peerConnection) {
            if (type === 'offer' || type === 'answer') {
                console.log('[IPop WebRTC] Peer connection not ready, initializing as callee...');
                await initCall(callId, false, dotNetRef);
            } else if (type === 'candidate') {
                try {
                    const c = JSON.parse(payload);
                    candidateQueue.push(c);
                    console.log('[IPop WebRTC] Early candidate queued.');
                } catch (e) { }
                return;
            }
        }

        if (initPromise) {
            await initPromise;
        }

        if (!peerConnection) {
            console.warn('[IPop WebRTC] Cannot handle signal: peerConnection is null');
            return;
        }

        try {
            if (type === 'offer') {
                const offer = JSON.parse(payload);
                console.log('[IPop WebRTC] Setting remote description from offer...');
                await peerConnection.setRemoteDescription(new RTCSessionDescription(offer));
                await drainCandidateQueue();

                console.log('[IPop WebRTC] Creating and setting answer...');
                const answer = await peerConnection.createAnswer();
                await peerConnection.setLocalDescription(answer);

                if (dotNetRef) {
                    console.log('[IPop WebRTC] Dispatching answer to caller...');
                    await dotNetRef.invokeMethodAsync('OnAnswerGenerated', callId, JSON.stringify(answer));
                }
            } else if (type === 'answer') {
                const answer = JSON.parse(payload);
                console.log('[IPop WebRTC] Setting remote description from answer...');
                await peerConnection.setRemoteDescription(new RTCSessionDescription(answer));
                await drainCandidateQueue();
            } else if (type === 'candidate') {
                const candidate = JSON.parse(payload);
                if (peerConnection.remoteDescription && peerConnection.remoteDescription.type) {
                    await peerConnection.addIceCandidate(new RTCIceCandidate(candidate));
                    console.log('[IPop WebRTC] Added ICE candidate.');
                } else {
                    candidateQueue.push(candidate);
                    console.log('[IPop WebRTC] Queued ICE candidate (waiting for remote description).');
                }
            }
        } catch (err) {
            console.error('[IPop WebRTC] Error handling signal:', type, err);
        }
    }

    function setMute(isMuted) {
        if (localStream) {
            localStream.getAudioTracks().forEach(track => {
                track.enabled = !isMuted;
            });
            console.log('[IPop WebRTC] Microphone muted state:', isMuted);
        }
    }

    function endCall() {
        console.log('[IPop WebRTC] Ending call, releasing resources...');
        stopAudioTones();
        candidateQueue = [];
        initPromise = null;

        if (visualizerTimer) {
            clearInterval(visualizerTimer);
            visualizerTimer = null;
        }
        if (localStream) {
            localStream.getTracks().forEach(track => track.stop());
            localStream = null;
        }
        if (peerConnection) {
            peerConnection.close();
            peerConnection = null;
        }
        if (remoteAudioEl) {
            remoteAudioEl.srcObject = null;
            remoteAudioEl.pause();
        }
        localAnalyser = null;
        remoteAnalyser = null;
        currentCallId = null;
        console.log('[IPop WebRTC] Call resources released.');
    }

    // Expose on global window object
    window.ipopAudio = {
        playRingtone,
        playDialTone,
        stopAudioTones,
        initCall,
        handleSignal,
        setMute,
        endCall
    };
})();
