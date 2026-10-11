# Meeting Rooms in Calls & Meetings — design

Grilled with the user 2026-10-11 (Q1–Q19, all "sesuai rekomendasi"). Mockup: `docs/mockups/meeting-rooms.html` — v2 Apple style (user 2026-10-11: v1 "terlalu basic"); v2 awaiting OK.

## Decisions
1. Rooms are the SAME five SJA Hall rooms — Robusta, Arabika, Kalosi, Kapal Api, Toraja (keys `robusta`, `arabika`, `kalosi`, `kapal-api`, `toraja`) — and use the same LiveKit room `hall-{key}`, so people from the 3D Hall (later) and from /calls meet in one meeting. Only the room backend comes to prod now; the 3D Hall stays on `sja-hall`.
2. Free entry. A Calendar booking (meeting Location `SJA Hall · {Name}`) is shown on the card for information only; it does not lock the room. Double booking the same room is refused (409 `room_booked`), as on `sja-hall`.
3. Max 20 people per room. The 21st is refused with a dialog suggesting rooms that have space.
4. One screen share at a time; sharing while someone shares asks "X is sharing — take over?", the presenter's share stops and they are told who took over.
5. Video allowed; camera is OFF on join; 360p/15 fps cap. Admin switches video per room in Administration → Meeting rooms. Switching it off mid-meeting stops everyone's camera at once (toast "Video was turned off for {room} by an admin"); audio and screen share stay.
6. Anyone may record. Everyone sees a red "Recording … · started by X" indicator.
   - **To this PC**: browser composites the meeting view (stage + tiles) and mixes all voices; on Stop a `.webm` downloads.
   - **To server**: LiveKit Egress room-composite, MP4 720p, speaker/screen-large layout; one server recording per room at a time; stored on NAS `/sja/SJA SPJ/ICT/recordings/{room}/{yyyy-MM-dd}/`; visible only to people present while it recorded + admins; "Recording ready" notification; retention admin-set, default 90 days, nightly purge; listed in Calls & Meetings → Meeting Rooms → Recordings (not in Files).
7. UI: "Calls" → "Calls & Meetings" (URL stays `/calls`). Third tab "Meeting Rooms": five Hall-coloured cards (occupants x/20, current/next booking, video on/off, recording/sharing state, Join, Book). The meeting opens full view and minimizes to a floating pill so other modules stay usable.
8. A Calendar meeting located in a room gets "Join room" in its detail and its reminder notification opens the room.
9. English UI copy. Prod only on an explicit "deploy ke prod". Egress is tried on dev first.
