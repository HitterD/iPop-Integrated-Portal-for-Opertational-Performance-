# Meeting Rooms v2 — design

Grilled with the user 2026-10-12 (Q1–Q18, all "sesuai rekomendasi", plus the user's override "a room can only be used when it is booked"). Mockup approved: `docs/mockups/meeting-rooms-v2.html`. Builds on v1 (live on prod since 2026-10-12, 2893123; spec `2026-10-11-meeting-rooms-design.md`).

## 1. Booking-only rooms
1. A room can only be used inside a booking (a Meeting whose Location is `SJA Hall · {Name}`, status active). No booking = nobody can join (v1 free entry is removed).
2. **Use now**: on a free room the card offers "Use now" → sheet: duration 30/60/90 (default 60), title (default "{Room} meeting"), optional invitees → creates a real Meeting (organizer = caller, StartAt = now rounded down to the minute, EndAt = start + duration) through the normal meeting create path (invites, calendar rows, reminders, double-booking check) and joins. Refused (409 `room_booked`) when it would overlap the next booking; the sheet then offers the time until the next booking if ≥ 15 min.
3. **Lock window** of a booking = [StartAt − 10 min, max(EndAt, last moment the room still had people)). During it only these may join:
   - the organizer and attendees who did not decline → join directly (no password);
   - anyone else who enters the booking's 6-digit password → "guest" for that booking.
   - Admin has NO bypass.
4. **Password**: 6 random digits generated per booking when the meeting is created or when its Location becomes a room; stored on the Meeting; organizer can regenerate (old one stops working; already-admitted guests stay). Shown only to the organizer and attendees: Calendar meeting detail, the meeting invite notification/mail, and the reminder. The "Join room" link from the invite needs no password for invitees. Wrong password: generic error, rate-limited (5 tries / 5 min per user per booking).
5. **End of booking**: warning banner at 5 min before EndAt; organizer gets "Extend 15 min" (extends the Meeting EndAt via the normal update path; refused if it would overlap the next booking). If no next booking, people may stay after EndAt (room stays locked while occupied). When the next booking's lock window starts, everyone still inside from the previous booking is removed with a notice ("The next booking starts now"), 1-minute warning before.
6. Room card states (mockup screen "Rooms tab"): free → Use now + Book; booked & invited → Join (from StartAt − 10 min: "Starts HH:mm · Join early"); booked & not invited → "Locked" capsule + "Booked HH:mm–HH:mm" + "Enter with password"; titles stay hidden from non-invitees (v1 rule).

## 2. Recordings
7. Visible only to people who were in the room while it recorded (sampled from LiveKit, incl. password guests). Admins no longer see others' recordings in their own Recordings list; Administration → Meeting rooms keeps the admin list (audit/delete).
8. Delete: the person who started the recording, the booking organizer, or an admin. Two steps: Delete → confirm "Delete for everyone" (iOS alert, red). The NAS file is deleted immediately (not at the 02:30 purge); the row is marked Deleted.
9. Format: iPop's own recording template (served by iPop WebHost, used by Egress via `custom_base_url`) — no dependency on recorder.livekit.io. 1080p30, ~4.5 Mbps video, AAC audio. Layout:
   - someone sharing → shared screen fills the main area (object-fit: contain, never cropped), right strip of up to 5 camera/initials tiles (object-fit: contain for cameras, names on every tile, active speaker ring);
   - nobody sharing → grid of everyone (camera or initials + name tile), up to 20;
   - corner badge: room swatch, room name, meeting title, date/time;
   - Egress' own recorder participant is never shown.
10. In-meeting chat is saved with a server recording when one runs (messages sent while recording, with offset seconds) and shown next to the video in the player with clickable timestamps.

## 3. In-meeting chat
11. Right panel (desktop) / full-screen sheet (phone) like Zoom; text, emoji, auto-linked URLs; unread badge on the Chat button; system lines (joined/left, recording started/stopped). Max 2000 chars per message. Messages go through the iPop API (persisted per booking for the meeting's lifetime) and are relayed to the room; history loads on join. Without a recording, chat is deleted when the booking ends + room empty (no long-term storage). No file sending.

## 4. Quality
12. Camera: 1280×720 @30 with simulcast layers (360p, 180p); adaptiveStream + dynacast stay on.
13. Screen share: up to 1920×1080 @60, contentHint "detail", high bitrate (~6 Mbps top layer) with a 720p30 simulcast layer for weak links; degradation keeps resolution (drops fps first).
14. Screen-share audio on by default (tab or entire-screen audio, system audio where the browser allows). If the chosen surface has no audio track → toast "This window shares no sound. To share audio, choose a Tab or Entire screen." Remote share audio plays for everyone; local playback of the sharer's own tab audio suppressed (no echo).
15. Admin video switch (v1) unchanged.

## 5. Phone
16. Flip button (front/back) shown only when >1 camera; keeps publishing while switching. Phone layout: main speaker tile, own camera as picture-in-picture, horizontal strip, compact dock (Mute, Camera, Flip, Chat, Leave), chat full screen. Camera on phones needs the https address.

## Out of scope
File sending in chat; breakout rooms; waiting room; admin bypass; recording at 60 fps.
