# Task 5 report — recording template, Egress and migration

Status: DONE. Implemented in `C:/Users/IT12/orca/workspaces/meeting-rooms`, branch `meeting-rooms-v2`, base `ee68350`.

## Implementation

- Added the plain static `src/IPop.Web/wwwroot/recording/index.html`, `recording.js`, and `recording.css`. No Blazor runtime, application login or CDN dependency. Uses the existing local `../js/livekit-client.umd.js` (2.22.3).
- Own dark iPop layout follows the approved mockup: screen share in a large main panel with up to five right-strip camera/initials tiles; otherwise a grid of up to twenty. Four participants use a 2×2 grid. Names on every tile, speaking border, room-colour swatch and room/title/date badge. Both camera and screen videos use `object-fit: contain`.
- Pure `calculateLayout`, `displayName`, and `initials` helpers are exported for Node tests. Names fall back to “Participant”, never an identity/NIK. All query/name text uses `textContent`; no `innerHTML` insertion.
- Connects using Egress query `url`/`token` with autoSubscribe enabled and without rewriting `ws://livekit:7880` to the WebHost address. Hides `EG_` participants from both rendered tiles and audio. Attaches and plays all remote audio, including screen-share audio and audio from participants outside the visible twenty/five tiles.
- Reconciles track changes, mute/unmute, participants, names and active speakers. Reuses attached tracks where possible and detaches obsolete media and terminal-disconnect media.
- Logs `START_RECORDING` once after decoded video reaches the compositor, or the rendered initials view for audio-only participants. A three-second post-connect fallback covers an empty room or delayed media. Logs `END_RECORDING` once on terminal disconnect/connection failure. Reconnecting is not treated as terminal. LiveKit SDK logging is silenced to avoid credential-bearing error context; our console markers remain enabled. Does not log tokens/connection error objects.
- Changed `ILiveKitEgress.StartRoomCompositeAsync(room, filepath, templateUrl, ct)` and all callers/fakes. RoomComposite request sends `custom_base_url`, `advanced {width:1920,height:1080,framerate:30,video_bitrate:4500,audio_codec:"AAC"}`, MP4 output, and **no preset**.
- `MeetingRecordingService` supplies a safely escaped template URL with room display name, booking/ad-hoc title and ISO recording start date. Preserves existing config query parameters. `MeetingRooms:TemplateUrl` default/config is `http://host.docker.internal:5103/recording/index.html`; the explicit index avoids the Blazor fallback.
- AAC support verified against official protocol sources: `EncodingOptions.audio_codec` is `AudioCodec`, whose `AAC` enum value is 2. RoomComposite `preset` and `advanced` are oneof alternatives.
  - https://raw.githubusercontent.com/livekit/protocol/main/protobufs/livekit_egress.proto
  - https://raw.githubusercontent.com/livekit/protocol/main/protobufs/livekit_models.proto

## TDD evidence

All commands ran with the target worktree as their working directory. Logs are preserved under its `.scratch/` and were not staged.

### RED

1. `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter FullyQualifiedName~LiveKitEgressTests --nologo`
   - 1 failed / 17 passed. Updated request test failed at `Assert.False(preset)` against the old 720p preset. Log: `.scratch/task5-red-dotnet.log`.
2. `node --test tests/meeting-rooms-recording.test.cjs`
   - Initially 4 failed / 0 passed: missing template export/helpers (first assertion explicitly expected a function).
3. `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter FullyQualifiedName~Template_url_uses_config --no-restore --nologo`
   - 2 failed / 0 passed against the empty template URL before service wiring. Log: `.scratch/task5-red-url.log`.
4. `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter 'FullyQualifiedName~MeetingRoomsV2MigrationTests|FullyQualifiedName~LiveKitEgressTests|FullyQualifiedName~Template_url_uses_config' --no-restore --nologo`
   - 1 failed / 20 passed: migration assembly had no MeetingRoomsV2 migration. Log: `.scratch/task5-red-migration.log`.
5. Node grid refinement: approved four-person test failed (3 columns instead of 2), then corrected grid math.
6. Node logging refinement: lifecycle test failed because SDK logging was not silenced, then added `setLogLevel('silent')`.

### GREEN / final verification

- `dotnet test tests/IPop.Tests/IPop.Tests.csproj --filter 'FullyQualifiedName~MeetingRoomsV2MigrationTests|FullyQualifiedName~LiveKitEgressTests|FullyQualifiedName~MeetingRecordingServiceTests' --no-restore --nologo`
  - **35 passed, 0 failed**. Log: `.scratch/task5-green-focused.log`.
- `node --test tests/meeting-rooms-recording.test.cjs`
  - **7 passed, 0 failed**. Covers EG_ filtering, caps/tile math, 2×2 grid, safe names, Docker URL/auto-subscribe, query text safety (DOM stub throws on innerHTML), lifecycle markers, decoded frame readiness, off-grid audio playback and detach.
- `dotnet test tests/IPop.Tests/IPop.Tests.csproj --nologo`
  - **2199 passed, 0 failed, 0 skipped**. Log: `.scratch/task5-full-dotnet.log`.
- `node --test tests/meeting-rooms-*.test.cjs`
  - **19 passed, 0 failed, 0 skipped**. Log: `.scratch/task5-full-node.log`.
- `dotnet build src/IPop.WebHost/IPop.WebHost.csproj --nologo`
  - Succeeded, 0 warnings/errors in this build. Log: `.scratch/task5-webhost-build.log`.
- Actual WebHost verification: spawned only this worktree's WebHost on `http://127.0.0.1:5197` with Development static assets, fetched the following routes and asserted 200, correct content/MIME type, and not a fallback:
  - `/recording/index.html` → `text/html`, recording document, no Blazor boot.
  - `/recording/recording.js` → `text/javascript`, recording script.
  - `/recording/recording.css` → `text/css`, contain rule.
  - `/js/livekit-client.umd.js` → `text/javascript`, local SDK.
  - HTML response CSP includes `wss: ws:`. Existing WebHost route/CSP code needed no changes.
  - Stopped only the spawned child by its process handle; no image-name kill.
- Verified the real bundled UMD exposes Room, Audio kind, ParticipantNameChanged and `setLogLevel('silent')`; console START_RECORDING remains enabled after setting SDK silence.
- `git diff --check` passed.

## Generated EF migration

Tool installed locally in `.scratch/task5-ef/` (untracked); global tool/config untouched. Generation command:

```sh
ConnectionStrings__IPopDb='Host=127.0.0.1;Port=1;Database=not_used;Username=not_used;Password=not_used' \
Jwt__Key='migration-generation-only-not-a-real-key-1234567890' \
.scratch/task5-ef/dotnet-ef.exe migrations add MeetingRoomsV2 \
  --project src/IPop.Infrastructure/IPop.Infrastructure.csproj \
  --startup-project src/IPop.Api/IPop.Api.csproj --no-build \
  --output-dir Persistence/Migrations
```

Generated `src/IPop.Infrastructure/Persistence/Migrations/20261012082731_MeetingRoomsV2.cs`, its Designer and the model snapshot. **No database update or connection/mutation was performed by the migration command**; connection configuration pointed at unused loopback port 1. Tests use their existing isolated test databases, not dev/prod PostgreSQL.

Inspected Up: exactly **seven** top-level operations, all allowed:

1. Add nullable `Meetings.RoomPassword` varchar(6).
2. Add nullable `MeetingRecordings.MeetingId` uuid (no FK).
3. Create `MeetingChatMessages`: Id PK, MeetingId, RoomKey varchar(32), UserId, Kind varchar(16), Text varchar(2000), SentAt, nullable RecordingId and OffsetSeconds. Deliberately no foreign keys, as Task 4 ruled.
4. Create `MeetingRoomGuests`: composite PK (MeetingId, UserId), AdmittedAt; cascade FKs to Meetings.Id and Users.Id.
5. Create `IX_MeetingChatMessages_MeetingId_SentAt`.
6. Create `IX_MeetingChatMessages_RecordingId`.
7. Create `IX_MeetingRoomGuests_UserId`.

Down drops the two tables and two added columns. Snapshot changes contain only those four entity/schema additions. The migration test asserts operation count, column types/nullability, table/column sets, keys, cascading guest FKs, absence of chat FKs and the exact indexes. **No unrelated drift found.**

## Concerns / boundaries

- No real LiveKit/Egress/Chromium recording or encoded frame/audio extraction performed: Docker, NAS, prod and persistent databases were outside this task's authorized boundaries. Task 9 must verify actual media readiness, playback/capture and resulting 1080p MP4 with AAC.
- The supplied Docker internal LiveKit URL must be resolvable/reachable from Egress; the page intentionally preserves it.
- EF CLI 10.0.0 reported that the project runtime is 10.0.12. Generation succeeded; no model drift. Existing ProjectTask global-query-filter model warnings also appeared during generation.
- Full .NET restore reports pre-existing ImageSharp 3.1.12 vulnerability advisories; initial compilation also reported existing unrelated nullability/analyzer warnings. Not changed in this scoped task.
- Existing untracked scratch files were preserved; only Task 5 source/tests/generated migration/report were staged. No other worktree or prod/Docker configuration was modified.

## Review fix round 1 (base 4163c83)

Both Important findings were verified and addressed only in the recording script and its Node tests.

### Changes

- Distinguish unmuted published video from subscribed video: a camera/share publication counts as expected media even when `publication.track` is not yet set. A pending share selects the share layout rather than being mistaken for camera-off.
- Start readiness now checks the current rendered media: expected visible video must have decoded its first frame. Audio-only populated rooms start after the initials view renders. Removed the populated-room timeout bypass: the three-second fallback rechecks current non-EG_ participants and may start only a genuinely empty room. Arrival during the empty fallback wait cannot trigger premature start.
- Frame callbacks verify that their tile/track is still current; a decoded callback from detached/unsubscribed video cannot satisfy readiness.
- Handle TrackUnsubscribed explicitly using a WeakSet of unsubscribed track objects, because the SDK publication may still reference the old track during the event. Render reconciliation immediately detaches stale audio/video and will not reattach that object on intervening events. TrackSubscribed re-enables the actual subscribed object.
- Audio reconciliation compares stored track objects as well as SIDs: a same-SID replacement detaches/removes the old media, attaches the replacement and plays it. Covers both an unsubscribe transition and replacement without an unsubscribe. Video reconciliation also clears/replaces stale tiles.
- Ten additional regressions cover camera/share publications without tracks; subscribed video without decoded frames; current versus stale first-frame callbacks; genuinely empty/EG_-only fallback; arrival during empty fallback; audio-only initials; audio and video unsubscribe-before-publication-clear ordering; and same-SID audio replacement.

### RED/GREEN

- RED: `node --test tests/meeting-rooms-recording.test.cjs` before implementation — **8 passed, 6 failed**. Camera/share tests observed premature START_RECORDING; arrival/fallback test observed premature start; audio/video unsubscribe tests observed missing detach; same-SID replacement test observed missing detach/attach. Evidence: `.scratch/task5-fix1-red.log`.
- An intermediate expanded run found one test-harness assertion mismatch: removing a video tile removes its parent, not the video child directly. Corrected the assertion to check the actual detached tile parent; the real detach assertion remains intact.
- Final GREEN: `node --test tests/meeting-rooms-recording.test.cjs` — **17 passed, 0 failed**.
- Final full relevant Node suite: `node --test tests/meeting-rooms-*.test.cjs` — **29 passed, 0 failed**.
- `git diff --check` passed.
- No .NET source/schema changed, so the full .NET suite was not rerun, as requested. No prod/Docker/database or other-worktree changes. Existing scratch files preserved.
- Remaining concern unchanged: real Egress Chromium/encoded output verification belongs to Task 9; published video that never subscribes/decodes deliberately cannot force recording to start via a timeout.
