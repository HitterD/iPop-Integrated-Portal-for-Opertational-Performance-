# Meeting scheduling v3 — design

Grilled with the user 2026-10-13 (Q1–Q13, all "sesuai rekomendasi"). Approved mockup: `docs/mockups/meeting-scheduling-v3.html`
(screens 1–6 + phone). Builds on meeting rooms v2 (live on prod, 6dad48d). UI text English. NIK never shown.

## 1. Meeting kind
- New `Meeting.Kind` column: `voice` | `room` | `inperson` (nullable = plain personal event with no meeting semantics).
  - **voice** = scheduled LiveKit group call, audio only (today's "Online · iPop Meeting"). No room, no password.
  - **room** = one of the 5 SJA Hall rooms (Location `SJA Hall · {Name}` stays the room key source), v2 booking/password/lock rules unchanged.
  - **inperson** = free-text location, no Join.
- Migration backfills: Location `SJA Hall · X` → room; `Online · iPop Meeting` or empty Location with attendees → voice; other non-empty text → inperson.
- Kind is returned in every meeting/calendar DTO; clients stop sniffing Location strings (keep helpers as fallback only).
- One badge component + colour everywhere (Scheduled, Calendar, detail, invite/reminder notifications): voice = indigo + headphones, room = room colour + video icon + room name, in person = teal + pin.

## 2. Booking sheet (Meeting Rooms tab, Schedule → Meeting room)
- Replaces the Use-now 30/60/90 dialog; one sheet for Use now and Book (Book now prefilled with date/time too).
- Fields: title, date, start, end; chips Now / 30m / 1h / 2h / Until 17:00; duration label.
- Day availability timeline 07:00–19:00 (extends if bookings outside): booked blocks (title only if caller may see it, else "Booked"), selected block draggable + resizable (15-min snap), now line; clash = red outline + "Clashes with X · Try HH:MM–HH:MM" (next free slot of the same length).
- "Other rooms free at this time" chips switch room.
- Repeat row, invite picker (§4), lock note, "More options…" → full Calendar form prefilled (title, room, date/time, people).
- Primary = "Book and join" when start ≤ now (+ joins), else "Book".
- New API: room day availability `GET /api/meeting-rooms/{key}/availability?date=` (and for all rooms at a time range for "other rooms free").
- Limits (server enforced, replaces 90-min cap): 15 min – 8 h, same local day, start not in the past (Use now: now), up to 60 days ahead.

## 3. Repeat for room bookings
- Allowed. Before saving, preview: each occurrence date with free / booked-by (title or "Booked"); summary "N of M dates are booked".
- Per clash: Skip, or Move to a room free at that time. Save creates only the kept occurrences; each occurrence gets its own password.
- Server: preview endpoint + series create accepting per-date room overrides/skips; still validates each occurrence (409 with the date if a race).

## 4. Invite picker (one component everywhere: Calendar form, booking sheet, Scheduled)
- On focus before typing: Suggested (frequent invitees), Your department, Chat groups, Departments.
- Search by name or department; rows show avatar (photo), name, department, free/busy dot for the chosen time (Free / Busy hh:mm–hh:mm / unknown). No NIK, no position needed.
- Add a chat group or department at once with confirm "Add N people from X?".
- Chips with remove; summary "N free · M busy · Find a time".
- Warning above 20 for room meetings ("21 people — Kalosi fits 20").

## 5. Calls → Scheduled
- Groups Today / Tomorrow / This week / Later; cards: time range, title, kind badge, countdown "Starts in X" / "Live now", stacked avatars +N, organizer/"You organise".
- Detail (shared component with Calendar): coloured header (room gradient / indigo / teal), title, date, kind caption, status chip, big Join (disabled with "Join opens 10 min before" when early); action row Copy link · Message host · Open in Calendar · Edit · Cancel; icon rows (time, room/location + Change, organizer, agenda, reminder); room password box (organizer/attendees only); people grouped Going / No response / Declined with avatars.
- Past: collapsed section, last 30 days + Show more; attendance count (rooms) and recording chips the viewer may watch.
- Schedule button → menu Voice call (short form) / Meeting room (booking sheet). New call unchanged.

## 6. Calendar
- Meeting events coloured by kind (room colour / indigo / teal) with icon; other sources unchanged. Legend "Meetings" in sidebar.
- Event detail panel = the shared detail component from §5.
- New event form: kind tiles (Event / Voice call / Meeting room / In person); Meeting room shows 5 room tiles with Free/Busy for the chosen time + the same timeline; participants use the picker.
- Busy popover tidy: avatar, name, "shares free/busy only", Busy + time, Ask to see details, Find another time, Chat.

## 7. Phone
- Booking sheet and New event as bottom sheets; timeline scrolls horizontally. Scheduled list and detail are separate screens.

## Out of scope
- Video in voice calls; changes to v2 room access/locking rules; LiveKit webhooks.
