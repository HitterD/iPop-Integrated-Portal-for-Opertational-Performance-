# Meeting Scheduling v3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or superpowers:executing-plans inline). Every task is TDD: write the listed tests first, run them RED, implement, run GREEN, then the full suite, then commit. Steps use checkbox (`- [ ]`) syntax.

**Goal:** One meeting model with an explicit kind (voice / room / in person), a single booking sheet with a draggable day timeline replacing "Use now", repeatable room bookings with per-date clash handling, one invite picker with suggestions and free/busy, a redesigned Calls › Scheduled (+ Past) with a shared detail component reused by Calendar, kind colours/legend/popover in Calendar, and phone bottom sheets.

**Spec:** `docs/superpowers/specs/2026-10-13-meeting-scheduling-v3-design.md` · **Mockup (binding visuals, screens 1–6 + phone):** `docs/mockups/meeting-scheduling-v3.html` · Builds on v2 (`docs/superpowers/plans/2026-10-12-meeting-rooms-v2.md`, live 6dad48d).

**Tech stack:** .NET 10 minimal API, EF Core Npgsql (SQLite `TestDb` in tests), Blazor WASM, bUnit, xunit, node `--test`.

## Global constraints
- Worktree `C:/Users/IT12/orca/workspaces/meeting-rooms`, branch `meeting-scheduling-v3`. Prod only on an explicit "deploy ke prod".
- **NIK is never rendered.** NIK may travel as an opaque key in request/response bodies where existing APIs already use it (`AttendeeNiks`, `/api/calendar/freebusy?niks=`), but no new DTO adds a visible NIK/position field and no markup prints it. New picker DTOs carry `Key` (= NIK, opaque) + `Name` + `Department` + `PhotoUrl`.
- **v2 room rules are frozen:** `RoomAccess`, lock window `[StartAt−10 min, …)`, passwords, guests, rate limits — semantics unchanged. Room key source stays `Location = "SJA Hall · {Name}"` (`HallBooking.Location/RoomKey`). `Kind` is an addition, not a replacement for the location key.
- **Times:** server stores UTC; "same local day", 07:00–19:00, "Until 17:00" are in the caller's time zone (`User.TimeZoneId` via the existing `ZoneOfAsync`/`ResolveTimeZone` helpers).
- **Limits (server, room kind):** length 15 min – 8 h, start and end on the same local day, start ≥ now (Use now: start = now floored to the minute, as today), start ≤ today + 60 days. Snap 15 min in the UI only (server accepts any minute).
- **Migrations:** `dotnet ef migrations add` only; the API may lock DLLs (memory *dotnet-ef-while-api-running*): `dotnet build src/IPop.Api -o .scratch/efbuild` then `dotnet ef migrations add <Name> --project src/IPop.Infrastructure --startup-project src/IPop.Api --no-build -- --contentRoot src/IPop.Api` (fallback: without `--no-build` if this worktree's API is not running). **Never `database update` against 127.0.0.1:5433 (live prod Postgres).** Apply only to a throwaway copy DB for QA.
- **CSS:** new rules go in `wwwroot/css/v6/calls.css` and `wwwroot/css/v6/calendar.css` (plus one new `wwwroot/css/v6/meeting-kit.css` for the shared components, linked from `index.html` after `calls.css`). Port mockup tokens as-is: `--voice:#5e5ce6 / dark #7d7aff`, `--person:#0f8b8d / dark #2bb3b5`; gradient pairs `.r-robusta{--a:#9a6849;--b:#3d251a}`, `.r-arabika{#e2a965,#8e5228}`, `.r-kalosi{#86bf8b,#255a3e}`, `.r-kapal-api{#f37758,#9c2318}` (mockup class `r-kapal`; use the real key `kapal-api`), `.r-toraja{#8b98ea,#2b3683}`, `.k-voice{#8583ff,#3634a3}`, `.k-person{#45c4c6,#0b5f61}`, `.k-event{#7cb5ff,#1c5fc0}`. Reuse existing v6 tokens (`--surface`, `--ink`, `--sep`, `--fill`) instead of the mockup's `--card` etc. Check existing v2 room colour classes in `calls.css` first and reuse them if they already exist. Lucide icons: `headphones`, `video`, `map-pin`.
- **Copy:** English, exactly as the mockup.
- **Tests:** `dotnet test tests/IPop.Tests/IPop.Tests.csproj` and `node --test tests/*.test.cjs`. Never kill processes by image name.
- **Untracked files:** `.scratch/` is untracked; don't delete other sessions' folders.

## Review focus (check after each relevant task)
1. Backfill: a meeting `SJA Hall · Kalosi` with no attendees → `room`; empty Location with attendees → `voice`; empty Location without attendees → `NULL`; `Online · iPop Meeting` → `voice`; `ICT office` → `inperson`; cancelled rows backfilled too.
2. Use now when the next booking starts in 40 min: sheet offers ≤ 30 min (lock window), server rejects 45 with `room_booked` + `maxMinutes`.
3. Repeat with a clash on date 3 of 6 + Skip → exactly 5 meetings, 5 distinct passwords, one SeriesId; a race on date 4 → 409 `{ code:"room_booked", date }`, nothing saved.
4. Availability for a booking the viewer may not see shows "Booked", never the title (same privacy rule as `MeetingRoomService.BookingDtoAsync` / `PrivateTitle`).
5. Picker free/busy never leaks titles of busy-only colleagues (blocks only).
6. Changing a room meeting to voice clears `RoomPassword`; voice → room generates one.

---

### Task 1: `Meeting.Kind` column, migration with backfill, kind in every DTO

**Goal:** Kind becomes the explicit truth for meeting semantics; all server DTOs expose it.

**Files**
- Modify `src/IPop.Core/Entities/Meeting.cs`: `public string? Kind { get; set; }` + new `public static class MeetingKind { Voice="voice"; Room="room"; InPerson="inperson"; static string? Infer(string? location, bool hasAttendees); static bool IsValid(string? k); }` (`Infer` is the single rule used by the backfill tests, create paths without an explicit kind, and the client fallback).
- Modify `src/IPop.Infrastructure/Persistence/IPopDbContext.cs`: `Kind` max length 16.
- New migration `MeetingKind` in `src/IPop.Infrastructure/Persistence/Migrations/` (generated), then hand-append to `Up` (Postgres SQL, `migrationBuilder.Sql`):
  ```sql
  UPDATE "Meetings" SET "Kind"='room'     WHERE "Location" ILIKE 'SJA Hall%';
  UPDATE "Meetings" SET "Kind"='voice'    WHERE "Kind" IS NULL AND ("Location" = 'Online · iPop Meeting'
         OR (COALESCE(TRIM("Location"),'')='' AND EXISTS (SELECT 1 FROM "MeetingAttendees" a WHERE a."MeetingId"="Meetings"."Id")));
  UPDATE "Meetings" SET "Kind"='inperson' WHERE "Kind" IS NULL AND COALESCE(TRIM("Location"),'')<>'';
  ```
  (`SJA Hall%` rows whose name is not one of the 5 rooms: run `HallBooking.RoomKey` semantics — acceptable to treat any `SJA Hall` prefix as room since `Normalize` rewrites them; note it in the migration comment.) `Down` drops the column.
- `MeetingEndpoints.cs`: `CreateMeetingRequest`/`UpdateMeetingRequest` gain `string? Kind = null`; `ApplyCreateAsync`/update set `Kind = req.Kind ?? MeetingKind.Infer(...)`; reject an invalid kind (400 `invalid_kind`); `kind=room` requires a hall location (400 `room_required`); `kind=voice` forces `Location = "Online · iPop Meeting"`, `Link=null`; password rules: room ⇒ ensure password, non-room ⇒ `RoomPassword=null` (Review focus 6). `MeetingDto` gains `string? Kind = null` and `string? RoomKey = null`.
- `CalendarEndpoints.cs` `SaveEventRequest` gains `string? Kind = null`; `CreateEventAsync` meeting creation sets Kind (same rule); `CalendarEventDetailDto` gains `string? MeetingKind = null, string? RoomKey = null`.
- `src/IPop.Application/Calendar/CalendarFeed.cs` + `CalendarFeedService.cs` + `CalendarEndpoints.CalendarFeedItem`: add `string? MeetingKind = null, string? RoomKey = null` (do **not** reuse the existing `Kind` field — it is the feed sub-type).
- `IRoomBookingWriter` / `HallBookings` create path (used by Use now): sets `Kind = room`.
- Web models mirroring these DTOs (`src/IPop.Web/Models/*` or the page-local records — grep `RoomPassword` in `src/IPop.Web` to find them): add `Kind`, `RoomKey`.
- `src/IPop.Web/Pages/CallsSchedule.cs`: `public static string? KindOf(string? kind, string? location, bool hasPeople)` → server kind first, `IsOnline`/`HallBooking.RoomKey` fallback.

**Tests (RED first)** — new `tests/IPop.Tests/MeetingKindTests.cs`, `MeetingKindMigrationTests.cs`
- `MeetingKind.Infer` table (Review focus 1 cases).
- Migration test (pattern of `MeetingRoomsV2MigrationTests`): exactly one `AddColumnOperation` (`Meetings.Kind`, `character varying(16)`, nullable) + `SqlOperation`s; no other schema ops (catches model drift).
- Backfill SQL test: the generated SQL strings contain the three UPDATEs in order (string assert; SQLite can't run ILIKE).
- Create via `/api/meetings` with `kind=voice` → Location forced, no password; `kind=room` without hall location → 400; update room→voice clears password; voice→room generates 6 digits.
- `/api/meetings/upcoming`, `/api/calendar/feed`, `/api/calendar/events/{id}` return `kind` + `roomKey`.
- Use now booking has `Kind=room`.
- `CallsSchedule.KindOf` fallback cases.

**Acceptance:** every meeting surface can colour by kind without parsing Location; existing tests green.

**Commit:** `feat(meetings): explicit meeting kind with backfill`

---

### Task 2: Room availability + booking limits API

**Goal:** Data for the booking-sheet timeline and "Other rooms free"; one server booking path with v3 limits replacing the 30/60/90 cap.

**Files**
- `src/IPop.Application/MeetingRooms/MeetingRooms.cs`: new records
  ```csharp
  public record RoomBlockDto(Guid? MeetingId, string Title, DateTime StartAt, DateTime EndAt, bool Mine, bool Visible); // Title="Booked" unless Visible
  public record RoomDayDto(string Key, string Name, int Capacity, DateOnly Date, DateTime WindowStart, DateTime WindowEnd, IReadOnlyList<RoomBlockDto> Blocks);
  public record RoomFreeDto(string Key, string Name, int Capacity, bool Free, string? BusyTitle);
  public record BookingLimits(int MinMinutes, int MaxMinutes, int MaxDaysAhead); // 15, 480, 60
  public static class RoomBookingRules { static string? Validate(DateTime startUtc, DateTime endUtc, DateTime nowUtc, TimeZoneInfo tz); static (DateTime S, DateTime E)? NextFree(IReadOnlyList<(DateTime S, DateTime E)> busy, DateTime from, TimeSpan length, DateTime dayEnd); }
  ```
  Error codes: `too_short`, `too_long`, `different_day`, `in_past`, `too_far`.
- `MeetingRoomService`: `DayAsync(viewerId, key, DateOnly date, ct)`; `FreeAtAsync(viewerId, DateTime startUtc, DateTime endUtc, Guid? exceptMeetingId, ct)`; `UseNowAsync` signature becomes `(userId, key, DateTime endUtc, title, niks, ct)` (keep the minutes overload delegating, for v2 clients) and validates with `RoomBookingRules` + lock-window cap (existing `cap` logic stays). Remove `UseNowDurations` gating; keep `UseNowMinMinutes`.
- Window: 07:00–19:00 local, extended to the earliest/latest block of the day (rounded to the hour). Blocks = non-cancelled meetings with that room location overlapping the day; title visibility = same rule as `BookingDtoAsync`.
- `MeetingRoomEndpoints.cs`:
  - `GET /api/meeting-rooms/{key}/availability?date=YYYY-MM-DD` → `RoomDayDto` (404 unknown room, 400 bad date / > 60 days).
  - `GET /api/meeting-rooms/free?from=&to=&except=` → `RoomFreeDto[]` (all 5 rooms).
  - `POST /api/meeting-rooms/{key}/use-now` body `UseNowRequest(int? Minutes, DateTime? EndAt, string? Title, IReadOnlyList<string>? AttendeeNiks)`; error `invalid_range` (400, message per code) replaces `invalid_minutes` text "Choose 30, 60 or 90 minutes.".
  - `GET /api/meeting-rooms/limits` → `BookingLimits` (client never hard-codes).
- `MeetingEndpoints` create/update and `CalendarEndpoints.CreateEventAsync`: when kind=room apply `RoomBookingRules.Validate` (Calendar maps it to a new `SaveResult.InvalidRoomRange` → 400 with the message).

**Edge cases:** booking spanning midnight → `different_day`; DST irrelevant (Asia/Jakarta) but use tz conversions anyway; editing an existing meeting excludes itself from conflict (`exceptMeetingId`); lock window of the next booking caps Use now (Review focus 2); viewer is organizer/attendee → title visible.

**Tests (RED)** — `tests/IPop.Tests/MeetingRoomAvailabilityTests.cs`
- Day blocks ordered, private title → "Booked" for outsider, visible for attendee (Review focus 4); window extends to 20:00 for an 18:30–20:00 booking.
- `FreeAtAsync` marks busy room with title rule, `except` frees the edited meeting.
- `RoomBookingRules.Validate`: 14 min, 8h01, crosses midnight, start in past, 61 days → codes; 15 min and 8 h OK.
- `NextFree` finds the next same-length slot after a clash; null if none before window end.
- Use now with `EndAt` 2 h ahead works (no 90 cap); 45 min with next booking in 40 min → 409 + `maxMinutes ≤ 30`.
- Calendar create of a 9 h room meeting → 400.
- Update existing `MeetingRoomServiceTests`/`MeetingRoomsTabV2Tests` that assert 30/60/90 — change to the new rule, not delete.

**Acceptance:** endpoints return data matching mockup screen 2 (blocks, "Booked", other rooms free).

**Commit:** `feat(meeting-rooms): day availability, free rooms and v3 booking limits`

---

### Task 3: Repeat preview + series create with skips/room overrides

**Goal:** Mockup screen 3 ("Repeat clashes"): preview each occurrence, Skip or Move to another free room, save only kept ones.

**Files**
- `src/IPop.Api/Endpoints/CalendarSeries.cs`: extract `IEnumerable<(DateTime Start, DateTime End)> OccurrencesAsync(rule, start, end, tz)` from `CreateSeriesAsync`; add `PreviewRoomSeriesAsync(db, viewerId, roomKey, start, end, recurrence, ct)`; `CreateSeriesAsync` accepts `IReadOnlyList<OccurrenceOverride>? overrides` — skipped dates are not created, overridden dates use the other room's Location; each occurrence still goes through `CreateEventAsync` (own Meeting, own password, own conflict check). Keep the existing all-or-nothing transaction; on a room clash return the clashing local date.
- Records (CalendarEndpoints.cs):
  ```csharp
  public record SeriesPreviewRequest(string RoomKey, DateTime StartAt, DateTime EndAt, string Recurrence, Guid? ExceptSeriesId = null);
  public record SeriesOccurrenceDto(DateOnly Date, DateTime StartAt, DateTime EndAt, bool Free, string? BookedBy /*title or "Booked"*/, IReadOnlyList<string> FreeRooms /*keys*/);
  public record SeriesPreviewDto(int Total, int Booked, IReadOnlyList<SeriesOccurrenceDto> Occurrences);
  public record OccurrenceOverride(DateOnly Date, bool Skip, string? RoomKey);
  ```
  `SaveEventRequest` gains `IReadOnlyList<OccurrenceOverride>? Overrides = null`. `MeetingRoomEndpoints.UseNowRequest` is not repeatable (Use now = single); the booking sheet's Book path posts to `/api/calendar/events` with `Kind=room` + Recurrence + Overrides.
- Endpoint `POST /api/calendar/series/preview` → `SeriesPreviewDto` (max 100 occurrences, else 400 `too_many`).
- 409 body on race: `{ code:"room_booked", message, date:"2026-10-27" }` (`SaveResult.RoomBooked` carries the date via a new out value).

**Edge cases:** all dates skipped → 400 `nothing_to_save`; override to a room that is busy at that date → 409 with date; recurrence "Until" beyond 60 days → occurrences beyond limit flagged `Free=false, BookedBy=null` and a `TooFar` reason — simpler: preview returns them with `BookedBy:"More than 60 days ahead"`, and create rejects them unless skipped; summary string "N of M dates are booked" computed client-side from `Booked/Total`.

**Tests (RED)** — `tests/IPop.Tests/RoomSeriesTests.cs`
- Preview: weekly ×6 with one clash → `Total 6, Booked 1`, clashing date has `BookedBy` per privacy rule and `FreeRooms` excludes busy rooms.
- Create with Skip → 5 meetings, one SeriesId, 5 distinct passwords, Kind=room (Review focus 3).
- Create with Move → that occurrence's Location is the other room.
- Race: insert a clash between preview and create → 409 with date, zero rows added.
- Non-room series behaviour unchanged (existing `CalendarSeries` tests green).

**Commit:** `feat(calendar): room series preview with skip and move overrides`

---

### Task 4: Picker data APIs — suggestions, groups, departments, batch free/busy

**Goal:** Data for mockup screen 5 ("Invite people": before typing / typing "fin" / Add all confirm).

**Files**
- New `src/IPop.Api/Endpoints/InvitePickerEndpoints.cs` (mapped in `Program.cs` next to `MapMeetings`), group `/api/invite`, `RequireAuthorization` + `RequirePermission("calendar.use")` (match what `CalendarSharingEndpoints` uses — check its group).
  ```csharp
  public record InvitePersonDto(string Key, string Name, string? Department, string? PhotoUrl); // Key = NIK (opaque, never rendered)
  public record InviteGroupDto(string Kind /*chat|department*/, Guid Id, string Name, int Members, string? Color);
  public record InviteSuggestionsDto(IReadOnlyList<InvitePersonDto> Frequent, IReadOnlyList<InvitePersonDto> Department, IReadOnlyList<InviteGroupDto> ChatGroups, IReadOnlyList<InviteGroupDto> Departments);
  public record PersonBusyDto(string Key, string State /*free|busy|unknown*/, DateTime? BusyFrom, DateTime? BusyTo);
  ```
  - `GET /api/invite/suggestions` — Frequent: top 8 co-attendees of meetings I organised or attended in the last 90 days (count desc, excludes me/inactive); Department: up to 12 active colleagues of my department by name; ChatGroups: my `ChatRoom.Type=="group"` active rooms by `LastMessageAt` desc, top 8, with member count; Departments: all departments with active member count > 0 by name.
  - `GET /api/invite/search?q=` — name OR department ILIKE (escaped like `member-search`), active, max 20, people only + matching departments/chat groups (`{ People, Groups }`).
  - `GET /api/invite/expand?kind=chat|department&id=` — members (`InvitePersonDto[]`, excludes me); chat requires membership (403 otherwise).
  - `POST /api/invite/busy` body `{ keys:string[], from, to }` (max 200 keys, range ≤ 24 h) → `PersonBusyDto[]` built on `CalendarSharingEndpoints.BuildFreeBusyAsync` blocks (no titles — Review focus 5); `unknown` when the user has no calendar data source issue (inactive/not found).
- `PhotoUrl`: reuse the existing avatar URL convention (see `AvatarPhotos.Url` / `PersonAvatar.razor`) — if it's client-derived from NIK, return null and let the client use `PersonAvatar Nik=Key`.

**Tests (RED)** — `tests/IPop.Tests/InvitePickerTests.cs`
- Frequent ordering by co-attendance; inactive users excluded; no position/NIK-like visible fields beyond `Key`.
- Search "fin" matches name and "Finance" department; 1-char query → empty.
- Expand chat group I'm not in → 403; department expand excludes me.
- Busy: overlapping private meeting → `busy` with times only; JSON has no `title` property; > 200 keys → 400.

**Commit:** `feat(invite): suggestions, group expand and batch free/busy for the invite picker`

---

### Task 5: Shared Web kit — KindBadge, MeetingDetail, Countdown/AvatarStack

**Goal:** One badge + one detail component used by Calls and Calendar (mockup screens 1 + 4 detail panel).

**Files**
- New `src/IPop.Web/Components/Meetings/KindBadge.razor` — params `Kind`, `RoomKey`, `RoomName`, `Compact`; renders `<span class="kb r-{key}|k-voice|k-person">` with swatch/icon + label ("Kalosi" / "Voice call" / "In person"); `MeetingKinds.cs` static helper (CSS class, icon, label, gradient class) unit-tested.
- New `Components/Meetings/MeetingCountdown.razor` ("Starts in 10 min" `.cd.soon` ≤ 15 min, "Live now" `.cd.live`, "Tomorrow", weekday) — pure label fn in `CallsSchedule.Countdown(start, end, now)`.
- New `Components/Meetings/AvatarStack.razor` (up to 4 `PersonAvatar` + "+N").
- New `Components/Meetings/MeetingDetail.razor` + `MeetingDetailModel.cs` (view model built from `MeetingDto` or `CalendarEventDetailDto`): coloured header (room gradient / indigo / teal), title, date, kind caption, status chip (Live now / Starts in / Ended / Cancelled), big Join (disabled + "Join opens 10 min before" when early; voice → `/meet/{RoomName}`, room → existing `CalendarRoomJoin` logic, inperson → no Join), action row Copy link · Message host · Open in Calendar · Edit · Cancel (Edit/Cancel only organizer/delegate; EventCallbacks `OnEdit`, `OnCancel`, `OnOpenCalendar`, `OnChangeRoom`), icon rows (time + duration, room/location + "Change" for organizer room meetings, organizer, agenda, reminder, repeat text), password box = reuse `CalendarRoomPassword.razor` (organizer/attendees only — it already enforces), people grouped Going / No response / Declined with avatars (Going includes accept+maybe). Never shows NIK.
- New `wwwroot/css/v6/meeting-kit.css` (port `.kb`, `.cd`, `.org`, `.stack`, `.detail*`, `.grad`, colour classes); link in `wwwroot/index.html`.

**Tests (RED)** — bUnit `tests/IPop.Tests/MeetingKitTests.cs`
- KindBadge class/label/icon per kind; unknown room key → voice fallback not crash.
- Countdown labels at −5/+10/+200 min/tomorrow.
- MeetingDetail: Join disabled with caption 15 min before, enabled at 9 min; inperson → no Join; Edit/Cancel hidden for attendee; password box hidden for non-attendee; people grouping counts; markup contains no NIK string from the fixture.

**Commit:** `feat(web): shared meeting kind badge and detail component`

---

### Task 6: Shared Web kit — InvitePicker + RoomTimeline

**Goal:** Picker (screen 5) and timeline (screen 2) as standalone components.

**Files**
- New `Components/Meetings/InvitePicker.razor` (+ `.razor.cs`): params `Selected` (List<InvitePersonDto>, two-way), `From`/`To` (for free/busy), `RoomCapacity` (int?), `OnFindTime`. Focus before typing → sections Suggested / Your department / Chat groups / Departments; typing (debounce 200 ms, ≥2 chars) → results; rows: avatar (`PersonAvatar` by Key), name, department, dot + "Free" / "Busy 10:00–11:00" / "Unknown"; group row → confirm toast "Add N people from X?" (Add all / Cancel) then dedupe-add; chips with remove; summary "N free · M busy · Find a time" (link raises `OnFindTime`); warning above capacity "21 people — Kalosi fits 20". Free/busy refetched (batched) when `From/To` or selection changes. Keyboard: ↑/↓/Enter/Esc, `role="listbox"`, `aria-selected` as `"true"/"false"` strings (memory *directory-aria-checked-bool-bug*).
- `Services/InviteApi.cs` thin client for Task 4 endpoints (cache suggestions per session).
- New `Components/Meetings/RoomTimeline.razor` + `wwwroot/js/room-timeline.js` (pointer drag/resize; returns minutes to .NET via `DotNetObjectReference`): params `Day` (RoomDayDto), `Start`, `End`, `Accent` class, `OnChange(start,end)`. Renders `.tl-track` hours, past shade, busy blocks (title or "Booked"), selected block with label "10:00–11:00", now line, drag body to move / edges to resize, 15-min snap, clamped to window; clash state = red outline + hint "Clashes with {title} · Try HH:MM–HH:MM" (uses `RoomBookingRules.NextFree` mirror in `MeetingKinds.cs` pure C#, unit-tested) with a Try button.
- Pure geometry in `room-timeline.js` exported for node: `toMinutes(px,width,window)`, `snap(m,15)`, `clampMove`, `clampResize`.
- CSS in `meeting-kit.css` (`.tl-*`, picker `.ip-*` per mockup).

**Tests (RED)**
- node `tests/room-timeline.test.cjs`: snap, clamp to window, min 15 min resize, move keeps length.
- bUnit `InvitePickerTests.cs`: suggestions shown on focus; group add asks confirm with count and dedupes; busy dot text; capacity warning at 21 with Kalosi; summary counts; no NIK in markup.
- bUnit `RoomTimelineTests.cs`: blocks positioned by %, "Booked" text for hidden titles, clash class + Try text, Try invokes `OnChange` with the next free slot.

**Commit:** `feat(web): invite picker and room day timeline components`

---

### Task 7: Booking sheet replaces Use now (Meeting Rooms tab)

**Goal:** Mockup screen 2 ("Fits" / "Clash") — one sheet for Use now and Book.

**Files**
- New `Components/Meetings/BookingSheet.razor` (+ `.razor.cs`): params `RoomKey`, `Mode` (`now`|`book`), `PrefillStart?`. Fields: title, date (existing `DatePicker`), start, end; chips Now / 30m / 1h / 2h / Until 17:00 + duration label; `RoomTimeline`; "Other rooms free at this time" chips (`/api/meeting-rooms/free`, switching room reloads the day); Repeat row (reuse Calendar recurrence picker component if one exists, else the same select set as `Calendar.Compose.cs`) → when set, Book opens the repeat check (Task 3 preview list inside the sheet: per row Skip / Move to {room}; "N of M dates are booked"); `InvitePicker` with `RoomCapacity`; lock note ("Invited people join directly; others need the meeting password."); "More options…" → navigate `/calendar?new=1&kind=room&room={key}&start=&end=&title=` with people via `MeetingDraftHandoff` (Calendar reads both in Task 9). Primary: "Book and join" when start ≤ now (posts use-now with `EndAt`, then joins via existing `MeetingRoomState`), else "Book" (posts `/api/calendar/events` Kind=room [+Recurrence+Overrides]). Server errors shown inline (409 date → highlight that row).
- Modify `Components/Calls/MeetingRoomsTab.razor`: remove the Use-now dialog (~:303-351) and `UseNowDurations` (:403) usages; "Use now" opens `BookingSheet Mode=now`, "Book" (`Book()` :857, currently linking via `CalendarLinks`) opens `BookingSheet Mode=book` prefilled with the next free slot of today. Keep `CalendarLinks` only for "More options…" if useful.
- CSS: `.sheet2` per mockup in `calls.css`.

**Edge cases:** room becomes booked while open → 409 → reload day, show clash; Now chip when next booking caps → end clamped and "Until HH:MM (next booking)" hint; outside 07–19 still allowed (window extends).

**Tests (RED)** — bUnit `BookingSheetTests.cs` (+ update `MeetingRoomsTabTests`/`MeetingRoomsTabV2Tests` that referenced 30/60/90)
- Now chip → primary text "Book and join"; future start → "Book".
- Clash fixture → disabled primary + hint; Try fixes it.
- Repeat with preview fixture shows "1 of 6 dates are booked" and Skip removes from payload; Move sets override.
- "More options…" navigates to `/calendar?new=1&kind=room&room=&start=&end=&title=` and passes people via a new scoped in-memory `Services/MeetingDraftHandoff.cs` (never in the URL, so keys never reach the address bar); test both.
- Payload shapes for use-now and calendar create.

**Acceptance:** screens 2 "Fits" and "Clash" reproduced; old 30/60/90 dialog gone.

**Commit:** `feat(meeting-rooms): booking sheet with timeline replaces Use now`

---

### Task 8: Calls › Scheduled redesign + Past + Schedule menu

**Goal:** Mockup screen 1 (list, detail, "Schedule menu open").

**Files**
- `Pages/Calls.razor` (Scheduled list :111-170, detail :234-266, load :729): grouped Today / Tomorrow / This week / Later (`CallsSchedule.Group(start, today)`); card = time range, title, `KindBadge`, `MeetingCountdown`, `AvatarStack` +N, organizer chip or "You organise"; selecting renders `MeetingDetail` (right pane on desktop). Schedule button → menu: Voice call (existing short form, now posting `kind=voice`) / Meeting room (`BookingSheet Mode=book`, room picker first if none chosen — default first free room). New call unchanged.
- Past: collapsed "Past" section; `GET /api/meetings/past?days=30&before=` (new in `MeetingEndpoints.cs`, page size 20, organiser/attendee non-declined, ordered desc) returning `PastMeetingDto(MeetingDto Meeting, int? Attended, IReadOnlyList<RecordingChipDto> Recordings)` where `Attended` = distinct users seen in the room for room meetings (from existing recording/occupancy data if stored; otherwise accepted attendees count — check what v2 persists, document the choice in code) and recordings = `MeetingRecordings` with `MeetingId` the viewer may watch (reuse `MeetingRecordingService` viewer rule). "Show more" pages with `before`.
- `CallsSchedule.cs`: `Group`, `Countdown`, `TimeRange`.
- CSS `calls.css` per mockup (`.sched-*`).

**Tests (RED)**
- xunit `MeetingPastTests.cs`: 31-day-old excluded, declined excluded, recordings only those viewer may watch, paging.
- bUnit `CallsScheduledTests.cs` (+ update `MeetingSchedulingTests`): grouping labels, badge per kind, "You organise", detail opens on click, Join disabled early, Schedule menu has two options, Past collapsed then expands and "Show more" loads next page.

**Commit:** `feat(calls): scheduled list by day with kind badges, shared detail and past meetings`

---

### Task 9: Calendar — kind form, colours, legend, detail, busy popover

**Goal:** Mockup screens 4 (week / month / busy popover) and 6 (New event).

**Files**
- `Pages/Calendar.razor` form (:520-735): kind tiles Event / Voice call / Meeting room / In person replacing the Online/Room segment (:700-723); `form.Kind` in `Calendar.Compose.cs`/`.razor.cs` (`OnlineMeeting` → `Kind=="voice"`); Meeting room → 5 room tiles with Free/Busy for the chosen time (`/api/meeting-rooms/free`) + `RoomTimeline` for the chosen room/day; In person → location text; participants → `InvitePicker` (replaces `PeoplePicker` in this form only); room kind + Repeat → repeat check (Task 3) before save; save posts `kind`. Read `MeetingDraftHandoff` / query `?new=1&kind=room&room=` from Task 7.
- Event chips: meeting items coloured by `MeetingKind` (`r-{roomKey}` / `k-voice` / `k-person`) with icon; other sources unchanged.
- Sidebar legend "Meetings" above My calendars (Voice call / Meeting room "Colour of the room" swatches / In person).
- Detail panel (:399-475) → `MeetingDetail` for meeting items (personal/external keep current panel); `CalendarRoomJoin`/`CalendarRoomPassword` reused inside it.
- Busy popover `.cal-pop` (:745-765): avatar, name, "shares free/busy only", Busy + time, buttons Ask to see details (existing `/api/calendar/people/{nik}/ask`), Find another time (opens Find a time from `Calendar.Sharing.cs` with that person), Chat (`/chat?with=`… existing DM route).
- CSS `calendar.css`.

**Tests (RED)** — bUnit `CalendarKindTests.cs` (+ keep `CalendarHallBookingTests`, `MeetingRoomCalendarTests` green, adjust selectors)
- Kind tiles switch sections; voice save payload `kind=voice` with Online location; room save payload has hall Location + `kind=room`.
- Room tiles show Free/Busy from fixture.
- Feed item with `MeetingKind=room, RoomKey=kalosi` renders class `r-kalosi` + video icon; personal event unchanged class.
- Legend present; popover has the three actions and no title for busy-only.
- Editing an old meeting with null Kind infers via `KindOf`.

**Commit:** `feat(calendar): meeting kinds in form, colours, legend, shared detail and busy popover`

---

### Task 10: Phone layout

**Goal:** Mockup "Phone" screens.

**Files:** `meeting-kit.css`, `calls.css`, `calendar.css` media queries (≤ 640 px, matching existing v6 breakpoint — grep `@media (max-width` in calls.css): BookingSheet and Calendar New event as bottom sheets (`.sheet2.bottom`, drag handle, safe-area padding); timeline scrolls horizontally (`.tl-track/.tl-hours/.tl-tags` width 960 px, auto-scroll to selection on open); Calls Scheduled list and detail become separate screens (detail with back button, `Calls.razor` state `showDetail`); picker full-height.
**Tests:** bUnit asserts `bottom` class when the existing viewport service reports narrow; detail back button returns to list; node test for "scroll selection into view" offset fn in `room-timeline.js`.
**Commit:** `feat(web): phone bottom sheets and list/detail screens for scheduling`

---

### Task 11: End-to-end QA (copy DB only)

- Create a throwaway copy DB (pg_dump prod → restore as `ipop_qa_v3` on a non-prod port/db name; **never** migrate 127.0.0.1:5433 `ipop` itself), apply migrations there, verify backfill counts per kind with SQL and spot-check 10 rows (Review focus 1).
- Run API + WebHost from this worktree against the copy; Playwright two browsers (memory *ipop-two-browser-call-test*): Use now via sheet (Now → Book and join), Book future with drag/resize, clash + Try, Other rooms free switch, Repeat 6 weeks with one Skip + one Move (verify 5–6 meetings, distinct passwords), picker suggestions/group add/busy dots/capacity warning, Scheduled grouping + Past + recordings chip, Calendar colours/legend/detail/popover, phone viewport 390×844 sheets. Screenshot each mockup screen side by side into `.scratch/v3-qa/`.
- Full `dotnet test` + `node --test tests/*.test.cjs`; selector parity check of CSS added vs mockup classes used.
- Clean up QA data and the copy DB; report. No deploy.

**Commit:** none unless fixes (each fix its own `fix(...)` commit with a regression test).
