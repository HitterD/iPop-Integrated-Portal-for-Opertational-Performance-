# Task 6 — client media handoff

Implemented in the existing `C:/Users/IT12/orca/workspaces/meeting-rooms` worktree, branch `meeting-rooms-v2`, starting at `53c7fbed5f0e4967ceb2c51b44931964ee5bfac2`. No other worktree was used for writes. No subagents, production operations, Docker, DB, configuration changes, or process termination. Existing untracked `.scratch` content was preserved.

## Delivered

- Camera capture uses SDK `VideoPresets.h720` (1280×720@30, 1.7 Mbps main encoding) and explicit `[h180, h360]` simulcast; adaptiveStream/dynacast remain enabled.
- Screen capture targets 1920×1080@60, `detail` content hint; publishes 6 Mbps/60 fps main plus SDK `ScreenSharePresets.h720fps30` (1280×720@30, 2 Mbps). Explicit VP8 preserves the simulcast/detail behavior rather than permitting SVC to override the content hint. Publication requests `maintain-resolution`.
- Screen audio requested by default, systemAudio include, selfBrowserSurface exclude, surfaceSwitching include, local-playback suppression requested. See SDK caveat below.
- Capture and publication are separate so late picker completion after leave/replacement can stop captured tracks before publishing. Partial publication failures unpublish/stop all captured tracks. Cooperative takeover, bounded wait, busy result, and post-picker/post-publication competitor checks remain.
- Successful share without a live ScreenShareAudio publication invokes `OnRoomNotice('share_no_audio', null)`; cancellation/failure/stale results do not. Browser video-ended event also stops/unpublishes paired screen audio.
- Remote microphone and screen-share audio attach, explicitly play, and detach/remove on unsubscribe/leave. Stale-room events remain ignored.
- Camera enumeration and flip exposed without acquiring temporary device tracks; off/admin-locked cameras are not enabled. Rechecks across enumeration/device-switch awaits and stops old tracks if a room was replaced.
- Bounded text-only chat parser receives `ipop.chat`, supports undefined server sender, strips extra fields and relays sanitized JSON via `OnRoomChat`. Existing takeover messages still use their previous path. No chat/name value is inserted with innerHTML (existing tile markup and empty-strip clearing are static only).
- PC recording implementation, muted microphone join, session guards, metadata callback, controls, and HTTPS LiveKit URL rewrite preserved.

## SDK confirmation (2.22.3, not assumed)

Inspected the repository's `src/IPop.Web/wwwroot/js/livekit-client.umd.js` exports and method bodies with Node; export `version` is `2.22.3`. Also read the matching published declarations:

`https://unpkg.com/livekit-client@2.22.3/dist/src/room/track/options.d.ts`

Confirmed:

- `VideoPreset(width, height, maxBitrate, maxFramerate)` and `.resolution`/`.encoding`.
- `VideoPresets.h720/h180/h360`, `ScreenSharePresets.h720fps30` (the actual export is **ScreenSharePresets**, not VideoPresetsScreenShare).
- Publish options `simulcast`, `screenShareEncoding`, `screenShareSimulcastLayers`, `degradationPreference`, `videoCodec`.
- Capture options `audio` (boolean or constraints), `systemAudio`, `selfBrowserSurface`, `surfaceSwitching`, `resolution`, `contentHint`, and declared `suppressLocalAudioPlayback`.
- `LocalParticipant.createScreenTracks` applies contentHint and forwards the audio constraints object to getDisplayMedia.
- `Room.switchActiveDevice('videoinput', actualDeviceId)` returns success, switches published camera tracks, and uses exact device selection by default; `LocalVideoTrack.restartTrack` accepts facingMode/resolution.
- SDK manual `publishTrack` installs ended handlers. SDK unpublishes each ended screen track independently; the added video-ended guard closes paired audio even if the browser leaves that track live.

**SDK bug/workaround:** The 2.22.3 display-constraints adapter does not forward top-level `suppressLocalAudioPlayback`, despite declaring it. Capture therefore includes top-level `suppressLocalAudioPlayback: true` **and** requests audio with `{ suppressLocalAudioPlayback: true }` instead of literal `audio: true`. This is audio-enabled capture with the standard browser audio constraint. A test invokes the actual SDK createScreenTracks implementation against mocked getDisplayMedia and confirms suppression arrives inside `constraints.audio`, all surface options arrive, 1080p60 constraints arrive, and the real SDK applies `detail`. No SDK file was patched.

## Interfaces for Task 7

Existing methods remain compatible; `setShare(on, takeover)` still resolves `ok | busy | denied`.

- `window.ipopRooms.listCameras(): Promise<number>` — video-input count; 0 on unavailable enumeration/error or replaced room during enumeration. Uses navigator.mediaDevices.enumerateDevices only, no permission prompt/getUserMedia. Call after camera permission/join as appropriate; browser privacy can hide devices before permission.
- `window.ipopRooms.flipCamera(): Promise<{ success: boolean, deviceId?: string, facingMode?: string, label?: string }>` — false if camera off, locked, unavailable, busy, stale or failed; success returns current camera info. Chooses the next distinct actual device; blank-ID/blank-label mobile fallback restarts the same published track with toggled user/environment facingMode and h720 capture. Never turns an off camera on. UI should show Flip only when camera count >1; no UI was added here.
- `.NET OnRoomChat(string json)` — sanitized JSON fields `{ id, userId, name, text, sentAt, kind }`, only for topic `ipop.chat`. Accepts server events with participant undefined. Limits: payload 16,384 bytes, string Guid IDs, name ≤256 UTF-16 units, nonblank text ≤2000 UTF-16 units, bounded ISO timestamp, kind text/system. Empty system name and all-zero system userId allowed. Task 7 owns history merge/dedup/unread/rendering/API send; render text via Razor/textContent, not HTML.
- `.NET OnRoomNotice(string code, string? detail)` — new `share_no_audio`, detail null. Task 7 maps to: "This window shares no sound. To share audio, choose a Tab or Entire screen."
- `.NET OnRoomMetadata(string json)` unchanged; initial metadata and RoomMetadataChanged carry booking info through without client filtering. Task 7 parses booking/timer/extend state.

Task 7 must add the OnRoomChat callback to MeetingRoomState before expecting visible chat; this task deliberately does not edit .NET or UI/state. Unhandled interop rejection is caught, so existing UI does not crash while that callback is absent.

## RED / GREEN evidence

Commands executed from the target worktree with Node v24.14.0.

### Initial real RED (before production edits)

```text
node --test tests/meeting-rooms-*.test.cjs
Exit code 1
ℹ tests 46
ℹ suites 0
ℹ pass 32
ℹ fail 14
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 305.9826
```

All 14 Task 6 tests failed against v1; the 32 existing layout/media/recording regressions passed. Failing behavior names/output:

```text
✖ chat parser validates bounded fields and returns plain text, ignoring extra fields
✖ camera choice cycles distinct actual devices and never selects current or blank IDs
✖ join configures h720 camera and explicit h180/h360 simulcast while retaining adaptation
✖ successful silent share requests HD60 screen audio and publishes 720p30 layer, then notices once
✖ share with actual audio does not warn, cancellation does not warn
✖ capture finishing after room replacement stops tracks and never publishes or notices in either room
✖ partial share publication failure stops and unpublishes captured tracks
✖ remote screen audio plays and detaches on unsubscribe and leave; stale-room events do nothing
✖ server-origin chat with undefined participant relays safe JSON only on ipop.chat, takeover still works
✖ listCameras returns count without opening devices and flip preserves camera off and video lock
✖ flip switches to actual other device and returns current camera info without enabling camera
✖ mobile blank labels fallback restarts same camera publication with facingMode and quality
✖ enumeration await cannot flip a replaced room or a newly locked/off camera
✖ flip device restart finishing after leave stops the old camera track
```

Expected failures included helpers/camera APIs undefined, capture still 640-wide, no guarded createScreenTracks invocation, stale/failed share returning ok instead of denied, no explicit audio play, and no OnRoomChat relay. Missing flip methods caused TypeErrors in two behavior tests as well as explicit missing-function assertions in interface tests.

### Intermediate runs (not hidden)

- `node --test tests/meeting-rooms-media.test.cjs`: 14 tests, 13 pass, 1 fail. Remaining failure was cross-VM array prototype equality, not production behavior; normalized the assertion with Array.from.
- Full suite next: 46 tests, 45 pass, 1 fail in legacy "share rechecks after picker and retracts when another presenter wins". v2 now discards an unpublished capture before any publication, so its legacy SDK harness/expectation was adapted to guarded createScreenTracks (not an on/off publication). Retained all takeover/stale/PC-recording checks and updated v1 quality expectations to the binding v2 values.
- Full suite: 50 tests, 50 pass after adding exact-SDK capture plumbing and late-publication/admin-lock/off-switch cases.

### Additional real RED regressions

```text
node --test --test-name-pattern='non-string Guid' tests/meeting-rooms-media.test.cjs
Exit code 1; tests 1; pass 0; fail 1; duration_ms 191.0062
Assertion: parser returned an object containing id: [Guid] instead of null.
```

Added explicit string-type checks before Guid regex validation.

```text
node --test --test-name-pattern='browser stop-sharing' tests/meeting-rooms-media.test.cjs
Exit code 1; tests 1; pass 0; fail 1; duration_ms 191.7944
Assertion: expected ended handler function, received undefined.
```

Added paired-audio cleanup on browser screen-video end, then made fake tracks implement the native addEventListener interface.

### Final GREEN

```text
node --test tests/meeting-rooms-*.test.cjs
Exit code 0
ℹ tests 52
ℹ suites 0
ℹ pass 52
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 279.1297

node --check src/IPop.Web/wwwroot/js/meeting-rooms.js
Exit code 0, no output

git diff --check
Exit code 0, no whitespace errors
```

Git emitted only its existing LF→CRLF normalization warning for the modified layout test. No .NET edits: per instruction, no .NET build/test rerun or claim of new build verification.

Coverage: 20 Task 6 tests plus 12 existing layout/v1 behavior tests and 20 recording-template tests = 52. This is mocked browser/SDK plumbing coverage, not real-device/WebRTC stats or real audible receiver verification.

## Remaining concerns / honest browser limits

1. 1080p60/6 Mbps and 720p30 are targets, not measured delivery guarantees. Display surface size, browser, hardware, network, dynacast and encoder policies can reduce actual fps/resolution/layers. Real getStats QA belongs to Task 9.
2. System audio and suppression are browser/OS/surface-dependent; mobile share and Safari support differ. Requests can be ignored. No physical-device audible/audio-suppression claim is made. Actual SDK capture options are verified with a fake browser capture, not a real picker.
3. Autoplay can reject play(); cleanup remains safe but audible playback may need a user gesture/browser permission. Existing meeting audio flow is retained; no new UI unlock control added.
4. Enumeration may expose only one camera or hidden IDs/labels before permission; HTTPS is required on phones. FacingMode fallback relies on browser support and at least two enumerated cameras; no physical phone flip verified here.
5. Receive parser validates shape/bounds, not cryptographic server origin. Topic is a routing marker, not authorization; persisted sends/access remain server responsibilities from Task 4. Task 7 should deduplicate by id against history and avoid rendering HTML.
6. Existing PC recording behavior is preserved, including its v1 720p15 canvas and voice mixer; v2 1080p30 server recording belongs to Task 5. This task does not claim that PC recording now mixes screen audio.

## Changed files

- `C:/Users/IT12/orca/workspaces/meeting-rooms/src/IPop.Web/wwwroot/js/meeting-rooms.js`
- `C:/Users/IT12/orca/workspaces/meeting-rooms/tests/meeting-rooms-media.test.cjs`
- `C:/Users/IT12/orca/workspaces/meeting-rooms/tests/meeting-rooms-layout.test.cjs`
- `C:/Users/IT12/orca/workspaces/meeting-rooms/.superpowers/sdd/2026-10-12-meeting-rooms-v2/task-6-report.md`
