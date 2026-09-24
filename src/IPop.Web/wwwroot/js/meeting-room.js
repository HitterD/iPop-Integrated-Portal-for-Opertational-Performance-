// Meeting media is separate from one-to-one call signaling. Never fall back to P2P
// for a group room: without a configured SFU it would only connect one participant.
(function () {
    let room = null;
    let microphone = null;
    let camera = null;
    let container = null;

    function attach(track) {
        if (!container || (track.kind !== 'audio' && track.kind !== 'video')) return;
        const element = track.attach();
        element.dataset.meetingTrack = 'true';
        container.appendChild(element);
    }

    function detach(track) {
        track.detach().forEach(element => element.remove());
    }

    async function leave() {
        if (camera) { camera.stop(); camera = null; }
        if (microphone) { microphone.stop(); microphone = null; }
        const previous = room;
        room = null;
        if (previous) await previous.disconnect();
        if (container) {
            container.querySelectorAll('[data-meeting-track]').forEach(el => el.remove());
            container = null;
        }
    }

    window.ipopMeeting = {
        async join(url, token, containerId) {
            await leave();
            if (!window.LivekitClient?.Room) throw new Error('LiveKit client is unavailable');
            container = document.getElementById(containerId);
            if (!container) throw new Error('Meeting container was not found');
            const next = new window.LivekitClient.Room({ adaptiveStream: true, dynacast: true });
            next.on(window.LivekitClient.RoomEvent.TrackSubscribed, attach);
            next.on(window.LivekitClient.RoomEvent.TrackUnsubscribed, detach);
            next.on(window.LivekitClient.RoomEvent.Disconnected, () => {
                if (room === next) void leave();
            });
            try {
                await next.connect(url, token);
                room = next;
                next.remoteParticipants.forEach(participant => {
                    participant.trackPublications.forEach(publication => {
                        if (publication.track) attach(publication.track);
                    });
                });
                microphone = await window.LivekitClient.createLocalAudioTrack();
                await next.localParticipant.publishTrack(microphone);
            } catch (err) {
                await next.disconnect();
                await leave();
                throw err;
            }
        },
        async setMicrophone(enabled) {
            if (microphone) await microphone.setEnabled(enabled);
        },
        async setCamera(enabled) {
            if (!room) return;
            if (enabled && !camera) {
                const track = await window.LivekitClient.createLocalVideoTrack();
                try {
                    await room.localParticipant.publishTrack(track);
                    camera = track;
                    attach(track);
                } catch (err) {
                    track.stop();
                    throw err;
                }
            } else if (!enabled && camera) {
                await room.localParticipant.unpublishTrack(camera);
                detach(camera);
                camera.stop();
                camera = null;
            }
        },
        leave
    };
})();
