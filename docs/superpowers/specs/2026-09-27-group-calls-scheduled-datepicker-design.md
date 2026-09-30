# Group calls, Scheduled meetings, shared date/time picker

Date: 2026-09-27 · Branch: v6-port · Status: approved in grill session (not committed)

## 1. Group calls (LiveKit SFU, audio)

- **Media server.** LiveKit 1.13.7 runs as the native Windows binary `infra/livekit/bin/livekit-server.exe`, started by `start-ipop.bat`. It is not run in Docker, because Docker Desktop's per-port UDP forwarding hung Docker before. Keys come from `LIVEKIT_KEYS` in `.env`.
- **Public access.** NPM gets a custom location `/rtc` pointing to `http://10.10.6.13:7880` with websockets on. The client connects to `wss://<page host>` when the page is https, and to the configured `Calls:LiveKit:Url` otherwise.
- **Who can start.** Any member of a chat group (`ChatRoom.Type == "group"`).
- **Ringing.** Starting a call rings every online member for 45 s, reusing the incoming-call overlay with the group name and avatar.
- **Late join.** Members who miss the ring join later through a banner in the group chat: "Panggilan grup sedang berlangsung · N orang · Gabung".
- **One call per group.** A group has at most one active call. Starting when one exists joins it instead.
- **Ending.** The call ends when the last participant leaves. Ringing members who never joined get Missed in their history.
- **Data.**
  - `CallRoom` uses `Kind = "group"` plus a new nullable `ChatRoomId`, which needs a migration.
  - Participants are all group members, with status `invited`, `accepted` (joined), `declined` or `left`.
  - `CallHistory` gets one row per participant, as today.
- **Server.**
  - `POST /api/calls/group/{chatRoomId}` creates the call, or returns the active one.
  - `POST /api/calls/{id}/join` returns a LiveKit token, room name `call-{id:N}`.
  - The existing decline/leave endpoints are reused.
  - The hub broadcasts `callStateChanged` to all group members.
- **Client.**
  - A `livekit-call.js` module handles audio only: publish mic, subscribe remotes, report active speakers and levels.
  - `CallState` gains a group mode. The call window shows a participant grid of avatars with speaking rings and mute state, plus mute, device, leave and minimise controls.
  - 1:1 calls stay on the P2P path unchanged.
- **Entry points.**
  - The phone button in the group chat header, next to Members and Settings.
  - The group drawer hero.
  - The active-call banner in the chat.

## 2. Scheduled meetings (Calls › Scheduled)

1. **Modal editor.**
   - Title.
   - Start date and time, using the shared picker.
   - Duration chips of 15, 30, 60 or 90 min, or a custom end. The end is never before the start.
   - Location: a text field, or a toggle "Online · iPop Meeting".
   - Agenda.
   - Reminder: none, 5, 15, 30 or 60 min, or 1 day.
2. **Invites.** Use `PeoplePicker` (search by name or NIK, with avatars) plus "Undang grup", which picks a chat group and expands it to its members. `RoomId` links the meeting to that group.
3. **Notifications on create.** Creating a meeting sends `meeting.invited` to every attendee except the organizer.
4. **List.**
   - Grouped by day: Hari ini, Besok, then dated.
   - Each row shows time and duration, attendee avatars, and my RSVP (Hadir / Mungkin / Tidak) with translated labels.
   - Gabung is enabled from 10 minutes before the start until the end.
5. **Meeting room.** It reuses the group-call LiveKit client and UI: participants, speaking, mute, leave.
6. **Fixes.**
   - Cancel uses one status constant, deletes pending reminders and calendar rows, and the reminder worker skips cancelled meetings.
   - `/upcoming` becomes one query with no N+1.
   - The detail view refreshes after a cancel.

## 3. Shared date/time picker

- **Components.** `Components/AppDatePicker.razor` and `AppTimePicker.razor`, plus an `AppDateTimePicker` wrapper, alongside `AppSelect`. Styles live in `css/components/datepicker.css`, loaded after the primitives.
- **Style.** A macOS-like popover with a month grid, and a 15-minute time list beside it for datetime.
  - Quick chips: Hari ini, Besok, Senin depan. The clear chip is only for optional fields.
  - On mobile (under 640 px) it becomes a bottom sheet.
- **Behaviour.**
  - The field is still typeable (dd/MM/yyyy, HH:mm) and validated on blur.
  - Keyboard: arrows move days, PageUp/PageDown move months, Enter picks, Esc closes.
  - Focus is trapped while the popover is open and returns to the field on close.
  - Supports `Min`/`Max` and disabled dates.
- **Locale.** 24-hour clock, Indonesian month and day names, weeks start on Monday. Follows light/dark tokens.
- **API.** `@bind-Value` supports `DateTime?`, `DateOnly?` and `TimeOnly?`, with `Min`, `Max`, `Placeholder`, `Disabled`, `AllowClear`, `Step` (minutes) and `Id`/`AriaLabel`.
- **Adoption.** Replace every native date, time and datetime-local input in IPop.Web, about 25 sites:
  - Calls, Calendar, Chat.
  - Mail: search, reschedule, composer, settings.
  - News.
  - Projects: create, edit, extension, reports, table, task drawer, quick viewer.
  - Notifications.
  - The unused `ChatLayout.razor` is left alone.

## Constraints

- No commits.
- Edit in place with assert-unique patches, and back up to `.scratch/backup-groupcall/` first.
- No Add/Remove clicks, real chat, or mail against the live DB during browser tests.
- The migration is additive only, one nullable column.
