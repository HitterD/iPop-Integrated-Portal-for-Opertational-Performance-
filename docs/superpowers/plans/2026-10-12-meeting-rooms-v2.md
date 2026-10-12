# Meeting Rooms v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Booking-only meeting rooms with passwords, participant-only recordings with 2-step delete, in-meeting chat, HD/60fps screen share with audio, a custom 1080p recording layout and phone camera flip.

**Architecture:** Extends v1 (branch `meeting-rooms`, live 2893123). Access is decided server-side from the room's current booking (a Meeting located `SJA Hall · {Name}`) in one place (`RoomAccess`), used by join, chat, the room list and the booking worker. Chat goes client → iPop API (persist) → LiveKit RoomService `SendData` (topic `ipop.chat`) so only stored messages reach the room. Recordings use a page served by IPop.WebHost (`/recording/`) via Egress `custom_base_url`.

**Tech Stack:** .NET 10, EF Core Npgsql, Blazor WASM, livekit-client 2.22.3 UMD, LiveKit server 1.13.9 + Egress (Docker Desktop, reaches host via `host.docker.internal`).

**Spec:** `docs/superpowers/specs/2026-10-12-meeting-rooms-v2-design.md` · Mockup (binding visuals): `docs/mockups/meeting-rooms-v2.html` · v1 code reference: `docs/superpowers/plans/2026-10-11-meeting-rooms.md`.

## Global Constraints
- Branch `meeting-rooms-v2` in worktree `C:/Users/IT12/orca/workspaces/meeting-rooms` (off 78f2fd3). Prod only on "deploy ke prod".
- Never expose NIK; LiveKit identity = user Guid string.
- Password: exactly 6 digits, random (RandomNumberGenerator), per Meeting; shown only to organizer + non-declined attendees; never in room-list DTOs for others; never logged.
- Lock window = [StartAt − 10 min, max(EndAt, last occupied)); Admin has no bypass.
- Wrong password rate limit: 5 attempts / 5 min per (user, meeting) → `too_many_attempts`.
- Camera 1280×720@30 simulcast [h180, h360]; screen share up to 1920×1080@60 contentHint "detail", top layer ~6 Mbps + 720p30 layer, degradationPreference maintain-resolution; screen audio requested by default.
- Recording: Egress `advanced` {width 1920, height 1080, framerate 30, video_bitrate 4500 (kbps)}, `custom_base_url` from config `MeetingRooms:TemplateUrl` (default `http://host.docker.internal:5103/recording/`).
- Chat: max 2000 chars, text only, LiveKit data topic `ipop.chat`.
- English UI copy exactly as the mockup.
- Tests: `dotnet test tests/IPop.Tests/IPop.Tests.csproj`; node `node --test tests/meeting-rooms-*.test.cjs`. Never kill processes by image name.

## Review Focus
1. A user removed from a booking's attendees (or who declined) after joining must not rejoin without the password.
2. Two overlapping lock windows (booking B starts 10 min before A's end): A's people are removed only when B's window starts, and B's invitees can enter early.
3. Password regenerated while a guest is inside → guest stays; a new guest needs the new password.
4. "Use now" on a room with a booking in 20 minutes → must offer at most 20 minutes, never overlap.
5. Recording deleted while someone plays it → player shows "This recording was deleted", no 500.

---

### Task 1: Room access model, passwords, Use now, guests
**Files:** Core `Meeting.cs` (+`RoomPassword string?`), new `src/IPop.Core/Entities/MeetingRoomGuest.cs` (MeetingId, UserId, AdmittedAt; PK both), `IPopDbContext` config; `src/IPop.Infrastructure/Hall/RoomAccess.cs` (new); `MeetingEndpoints.cs` (generate password when Location is a hall room on create/update; regenerate endpoint `POST /api/meetings/{id}/room-password` organizer-only → new password); `MeetingRoomService.JoinAsync(userId, key, password?)`; `MeetingRoomEndpoints` join body `{ password? }`; `POST /api/meeting-rooms/{key}/use-now {minutes, title?, attendeeNiks[]}`; migration `MeetingRoomsV2` (also covers Task 4 and 5 entities — create the migration in Task 5 after all entities exist; Tasks 1–4 rely on TestDb EnsureCreated).
**Interfaces (produce):**
- `RoomAccess.ForAsync(IPopDbContext db, string roomKey, Guid userId, DateTime nowUtc, CancellationToken ct) → RoomAccessInfo(Booking? Current, Booking? Next, string Role)` where `Role` ∈ `"organizer"|"invited"|"guest"|"outsider"|"free"`; `Booking(Guid MeetingId, string Title, DateTime StartAt, DateTime EndAt, Guid OrganizerId, string? Password)`. Current = active booking whose lock window contains now (StartAt−10min ≤ now < EndAt, OR now ≥ EndAt but room still occupied and no later booking's window started — pass `bool occupied` from the caller).
- JoinAsync errors added: `no_booking` (409), `password_required` (403), `wrong_password` (403), `too_many_attempts` (429). A correct password inserts MeetingRoomGuest so later joins skip the password.
- Use now: `MeetingRoomService.UseNowAsync(userId, key, minutes, title, niks, ct) → (Guid? MeetingId, string? Error, int? MaxMinutes)`; creates the meeting via `MeetingEndpoints.ApplyCreateAsync` (Location = HallBooking.Location(name), StartAt = now floored to minute) so invites/reminders/password happen as for any meeting; error `room_booked` returns MaxMinutes = minutes until the next booking's lock window (≥15 else null).
**Tests (xunit, TestDb):** invited joins without password; outsider gets password_required; wrong password ×5 → too_many_attempts; correct password → guest, rejoin without password; declined attendee = outsider; no booking → no_booking; join allowed from StartAt−10min; regenerate changes password, guest stays admitted; Use now creates meeting + password + joins; Use now with next booking in 20 min → room_booked + MaxMinutes ≤ 20 (Review Focus 4); password never returned to outsiders.

### Task 2: Room list v2 + booking-end enforcement + extend
**Files:** `MeetingRoomService.ListAsync` (per-viewer card state), `MeetingRooms.cs` DTOs, new `src/IPop.Infrastructure/MeetingRooms/MeetingRoomBookingWorker.cs` (every 30 s), endpoint `POST /api/meeting-rooms/{key}/extend` (organizer; +15 min via ApplyUpdateAsync; 409 room_booked).
**Interfaces:** `MeetingRoomDto` gains `string Access` (`free|invited|locked|starting`), `MeetingRoomBookingDto` gains `bool IsOrganizer`; Now/Next titles keep the v1 privacy rule. Room metadata JSON gains `booking: { endAt, nextStartAt?, organizerId }` (set by the worker / join) so clients show "Ends in m:ss", the 5-minute banner, Extend (organizer only) and the 1-minute "next booking" warning.
- Worker: (a) keep metadata booking info fresh; (b) when a booking's lock window starts while participants of another booking are inside → RemoveParticipant for everyone not allowed in the new booking (Review Focus 2); (c) after EndAt with no next booking: allow staying, but when the room becomes empty the booking is over (no rejoin).
**Tests:** card Access per role; extend +15; extend refused on overlap; worker removes only disallowed people at the next window; metadata contains endAt/nextStartAt; Review Focus 1 (attendee removed from booking → outsider on next join).

### Task 3: Recordings — participant-only list, user delete, immediate NAS delete
**Files:** `MeetingRecording` (+`MeetingId Guid?` set at start from RoomAccess current booking), `MeetingRecordingService` (ListAsync(userId) viewer-only — no admin merge; `CanDeleteAsync(userId, admin, id)`: starter, booking organizer, admin; `DeleteAsync` deletes NAS file immediately via IFileAccessService.DeleteAsServiceAsync, then Status=Deleted; NAS failure → keep Deleted and let the nightly purge retry), endpoints `DELETE /api/meeting-rooms/recordings/{id}` (user; 403/404), admin delete uses the same immediate path; RecordingDto gains `bool CanDelete`, `int ChatCount`.
**Tests:** admin not a viewer doesn't see it in user list but sees it in admin list; starter/organizer/admin can delete, others 403; NAS delete called immediately; player after delete → 404 (Review Focus 5).

### Task 4: In-meeting chat (API + relay + recording link)
**Files:** new entity `MeetingChatMessage(Id, MeetingId, RoomKey, UserId, Text, SentAt, RecordingId Guid?, OffsetSeconds int?)`; `ILiveKitRoomAdmin.SendDataAsync(room, byte[] payload, string topic, ct)` (RoomService `SendData`, reliable); `MeetingChatService`; endpoints `GET /api/meeting-rooms/{key}/chat` (current booking history, allowed roles only), `POST /api/meeting-rooms/{key}/chat {text}` (sender must currently be allowed AND be a LiveKit participant of the room), `GET /api/meeting-rooms/recordings/{id}/chat` (viewers only; messages with RecordingId = id ordered by OffsetSeconds). System lines (recording started/stopped) are stored as messages with UserId = Guid.Empty and Text prefixed — or a `Kind` column (`text|system`); choose `Kind`. Cleanup in MeetingRoomBookingWorker: delete chat of bookings that ended > 1 h ago and are empty, except messages attached to a recording.
**Payload** published to topic `ipop.chat`: `{ id, userId, name, text, sentAt, kind }` (JSON UTF-8).
**Tests:** post requires presence; >2000 chars → 400; outsider can't read; message during recording gets RecordingId + offset; recording chat endpoint viewer-only; cleanup keeps recording-linked messages; SendData called with topic ipop.chat.

### Task 5: Recording template (1080p, own layout) + Egress request + migration
**Files:** `src/IPop.Web/wwwroot/recording/index.html` + `recording.js` + `recording.css` (static, served by WebHost; no Blazor); `LiveKitEgress.StartRoomCompositeAsync(room, filepath, templateUrl, ct)` → request with `custom_base_url` and `advanced {width:1920,height:1080,framerate:30,video_bitrate:4500,audio_codec AAC if supported else default}`; template URL from config `MeetingRooms:TemplateUrl` + query `room`, `title`, `date` (Egress appends url/token/layout). Template: reads `url`/`token`, connects with livekit-client UMD (copy/serve the same file), subscribes all, renders per spec §9 (share → main contain + right strip of up to 5; else grid; initials tiles; names; speaker ring; corner badge), hides identities starting `EG_`, logs `START_RECORDING` once first frame rendered (or after 3 s with nobody), `END_RECORDING` on disconnect. Also add the EF migration `MeetingRoomsV2` (Meeting.RoomPassword, MeetingRoomGuests, MeetingRecordings.MeetingId, MeetingChatMessages) — only those changes. WebHost CSP: the template page must be allowed to connect ws/wss to LiveKit (existing CSP already allows ws: wss:). Node test for the pure layout function (share/grid tile math, EG_ filtering).
**Tests:** Egress request JSON has custom_base_url + advanced 1920x1080 30 4500 and no preset; migration contains only the listed operations; node layout tests.

### Task 6: Client media — quality, screen audio, flip, chat relay
**Files:** `wwwroot/js/meeting-rooms.js`: camera h720 + simulcast [h180,h360]; screen share VideoPreset(1920,1080,6_000_000,60) contentHint 'detail', audio:true, systemAudio:'include', suppressLocalAudioPlayback:true, selfBrowserSurface:'exclude', surfaceSwitching:'include', screenShareSimulcastLayers [720p30], degradationPreference 'maintain-resolution'; after publish, if no screen_share_audio track → notify .NET `OnRoomNotice('share_no_audio')`; remote screen-share audio attached + played; `listCameras()` → count; `flipCamera()` → switchActiveDevice to the other camera (or restartTrack facingMode toggle on mobile when labels are empty); receive DataReceived topic `ipop.chat` → `OnRoomChat(json)`; metadata booking info → existing OnRoomMetadata. Node tests for pure helpers (camera pick next, payload parse).

### Task 7: In-meeting UI — chat panel, booking timer/banner/extend, flip, share-audio toast, phone layout
**Files:** `MeetingRoomState` (chat list, unread count, SendChatAsync → POST, LoadChatAsync on join, OnRoomChat, booking info from metadata, ExtendAsync, CanFlip/FlipAsync), `MeetingRoomOverlay.razor` (+ chat panel, Chat dock button with badge, time chip "Ends in m:ss", 5-min banner with Extend (organizer) / OK, 1-min "next booking" notice, flip button when CanFlip, share_no_audio toast, "removed: next booking" notice), CSS per mockup "In meeting + chat", "Time running out", "Phone". bUnit tests for chat panel, badge, Extend visibility, flip visibility, phone class at narrow width.

### Task 8: Rooms tab v2 + Use now + password + recordings delete/player chat + Calendar password
**Files:** `MeetingRoomsTab.razor` card states (free/invited/locked/starting per Access), "Use now" sheet, "Enter with password" sheet (6-digit input, errors), recordings row Delete (CanDelete) → iOS confirm "Delete for everyone", player with chat side panel and clickable timestamps (seek video), "Full HD" + chat count meta; Calendar meeting detail (component `CalendarRoomJoin` or new `CalendarRoomPassword`) shows password box with Copy + New password (organizer) for hall-room meetings; meeting invite + reminder notification text includes "Room password: 123 456" for hall-room meetings (server side in MeetingEndpoints invite + MeetingReminderWorker). CSS per mockup. bUnit + xunit tests.

### Task 9: End-to-end QA (copy DB + real LiveKit/Egress + NAS QA folder)
Two/three browsers: booking-only flow (Use now, invited join, outsider password, wrong password limit), extend + next-booking removal, chat live + persisted with recording + player timestamps, screen share 1080p60 stats via getStats (report fps/resolution actually sent), share audio heard by receiver, flip on emulated mobile (two fake cameras if possible), recording 1080p layout frames extracted (share and grid), 2-step delete removes NAS file. Clean up everything.
