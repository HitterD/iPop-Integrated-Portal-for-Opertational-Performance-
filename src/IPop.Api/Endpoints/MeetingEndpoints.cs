using IPop.Application.MeetingRooms;
using IPop.Api.Rbac;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Endpoint Kalender & Rapat (Mail Outlook & Kalender Meeting): undangan rapat,
/// RSVP peserta, rekap kehadiran, dan event kalender. Pengingat diproses worker.
public static class MeetingEndpoints
{
    public static IEndpointRouteBuilder MapMeetings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/meetings").RequireAuthorization();

        // POST /api/meetings — buat rapat + peserta + pengingat, lalu kabari undangan (meeting.invited).
        g.MapPost("/", async (CreateMeetingRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var issue = new CalendarEndpoints.SaveIssue();
            var (result, meetingId, invited) = await ApplyCreateAsync(db, userId.Value, req, ct, issue);
            switch (result)
            {
                case CreateResult.InvalidRoomRange: return MeetingRoomEndpoints.RangeError(issue.Reason);
                case CreateResult.InvalidTitle: return Results.Json(new { code = "invalid_title", message = "Meeting title is required (max 200 characters)." }, statusCode: 422);
                case CreateResult.InvalidRange: return Results.Json(new { code = "invalid_range", message = "End time must be after start time." }, statusCode: 422);
                case CreateResult.NotRoomMember: return Results.Json(new { code = "forbidden", message = "Not a member of that group." }, statusCode: 403);
                case CreateResult.RoomBooked: return Results.Json(new { code = "room_booked", message = "That meeting room is already booked at this time." }, statusCode: 409);
                case CreateResult.InvalidKind: return KindError(KindResult.InvalidKind);
                case CreateResult.RoomRequired: return KindError(KindResult.RoomRequired);
            }
            await NotifyMeetingChangedAsync(db, realtime, meetingId!.Value, [], ct);
            var dto = await LoadMeetingDto(meetingId!.Value, userId.Value, db, ct);
            // A mail invitation (SourceMailId) already notified its recipients through mail.new; a hall room still sends the
            // invite so the room password reaches them.
            if (notifs is not null && dto is not null)
                await PublishCreatedAsync(db, notifs, dto, invited, req.SourceMailId, NotifyLog(http), ct);
            return Results.Ok(dto);
        }).RequirePermission("mail.send");

        // GET /api/meetings/{id} — detail rapat + rekap RSVP.
        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var dto = await LoadMeetingDto(id, userId.Value, db, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        // GET /api/meetings/by-mail/{mailId} — rapat yang dibawa surat undangan ini.
        // Dipakai panel baca surat untuk menampilkan panel RSVP kepada penerima.
        g.MapGet("/by-mail/{mailId:guid}", async (Guid mailId, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var meeting = await db.Meetings.AsNoTracking().FirstOrDefaultAsync(m => m.SourceMailId == mailId, ct);
            if (meeting is null) return Results.NotFound();
            var dto = await LoadMeetingDto(meeting.Id, userId.Value, db, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        // POST /api/meetings/{id}/rsvp — jawab undangan (accept/maybe/decline) + catatan.
        g.MapPost("/{id:guid}/rsvp", async (Guid id, RsvpRequest req, HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // onBehalfOf = a full delegate answering for the invited owner.
            var (attendee, actedBy, denied) = await CalendarDelegateEndpoints.ResolveOwnerAsync(db, userId.Value, req.OnBehalfOf, DelegateLevel.Full, ct);
            if (denied is not null) return denied;
            var result = await ApplyRsvpAsync(db, id, attendee, req.Response, req.Note, ct, actedBy);
            if (result == RsvpResult.Applied) await NotifyMeetingChangedAsync(db, realtime, id, [userId.Value], ct);
            return result switch
            {
                RsvpResult.InvalidResponse => Results.Json(new { code = "invalid_response", message = "Response must be accept/maybe/decline." }, statusCode: 422),
                RsvpResult.NotInvited => Results.Json(new { code = "not_invited", message = "You are not an attendee of this meeting." }, statusCode: 403),
                _ => Results.Ok(await RsvpDtoAsync(db, id, attendee, actedBy, ct)),
            };
        });

        // GET /api/meetings/calendar?from=&to= — event kalender dalam rentang (milik sendiri + yang diundang).
        g.MapGet("/calendar", async (DateTime? from, DateTime? to, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // Paksa UTC — kolom timestamptz PostgreSQL menolak Kind=Unspecified.
            var f = DateTime.SpecifyKind(from ?? DateTime.UtcNow.Date.AddDays(-7), DateTimeKind.Utc);
            var t = DateTime.SpecifyKind(to ?? DateTime.UtcNow.Date.AddDays(35), DateTimeKind.Utc);

            var own = db.CalendarEvents.AsNoTracking().Where(e => e.OwnerId == userId.Value);
            var invited = db.CalendarEvents.AsNoTracking()
                .Where(e => e.MeetingId != null && db.MeetingAttendees.Any(a => a.MeetingId == e.MeetingId && a.UserId == userId.Value));
            var rows = await own.Concat(invited)
                .Where(e => e.StartAt < t && e.EndAt > f)
                .Distinct()
                .OrderBy(e => e.StartAt)
                .Select(e => new CalendarEventDto(e.Id, e.Title, e.StartAt, e.EndAt, e.Type, e.MeetingId, e.RoomId))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // POST /api/meetings/calendar — tambah event kalender. Bila RoomId ada, ini adalah
        // event ruang obrolan: buat Meeting dengan peserta = anggota ruang (otomatis),
        // sehingga RSVP & rekap berfungsi; kalau bukan, event berdiri sendiri.
        g.MapPost("/calendar", async (CreateCalendarEventRequest req, HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var title = req.Title?.Trim() ?? "";
            if (title.Length == 0) return Results.Json(new { code = "invalid_title", message = "Event title is required." }, statusCode: 422);

            var startAt = AsUtc(req.StartAt);
            var endAt = AsUtc(req.EndAt > req.StartAt ? req.EndAt : req.StartAt.AddHours(1));
            Guid? meetingId = null;

            if (req.RoomId is { } roomId)
            {
                // Event ruang: peserta = anggota ruang (otomatis).
                // Current members only: people who left the group are not invited.
                var memberIds = await db.ChatRoomMembers.AsNoTracking()
                    .Where(m => m.RoomId == roomId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct);
                if (!memberIds.Contains(userId.Value))
                    return Results.Json(new { code = "forbidden", message = "Not a room member." }, statusCode: 403);

                var meeting = new Meeting
                {
                    Id = Guid.NewGuid(), Title = title, StartAt = startAt, EndAt = endAt,
                    OrganizerId = userId.Value, Status = "aktif", RoomId = roomId,
                    ReminderMinutes = 30, CreatedAt = DateTime.UtcNow, Kind = MeetingKind.Infer(null, hasAttendees: true),
                };
                db.Meetings.Add(meeting);
                foreach (var uid in memberIds)
                {
                    db.MeetingAttendees.Add(new MeetingAttendee
                    {
                        Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, Required = true,
                        RsvpStatus = uid == userId.Value ? "accept" : "pending",
                        RespondedAt = uid == userId.Value ? DateTime.UtcNow : null,
                    });
                }
                // Pengingat per peserta (30 menit sebelum) — diproses MeetingReminderWorker.
                var sendAt = startAt.AddMinutes(-30);
                foreach (var uid in memberIds)
                {
                    db.MeetingReminders.Add(new MeetingReminder
                    {
                        Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, SendAt = sendAt,
                    });
                }
                meetingId = meeting.Id;
            }

            var ev = new CalendarEvent
            {
                Id = Guid.NewGuid(), Title = title, StartAt = startAt, EndAt = endAt,
                Type = req.Type ?? "kerja", OwnerId = userId.Value, RoomId = req.RoomId, MeetingId = meetingId, CreatedAt = DateTime.UtcNow,
            };
            db.CalendarEvents.Add(ev);
            await db.SaveChangesAsync(ct);
            if (meetingId is { } mid) await NotifyMeetingChangedAsync(db, realtime, mid, [], ct);
            else await NotifyCalendarChangedAsync(db, realtime, [userId.Value], ct);
            return Results.Ok(new CalendarEventDto(ev.Id, ev.Title, ev.StartAt, ev.EndAt, ev.Type, ev.MeetingId, ev.RoomId));
        });

        // GET /api/meetings/room/{roomId} — event kalender untuk satu ruang obrolan.
        g.MapGet("/room/{roomId:guid}", async (Guid roomId, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Not a room member." }, statusCode: 403);

            var rows = await db.CalendarEvents.AsNoTracking()
                .Where(e => e.RoomId == roomId)
                .OrderBy(e => e.StartAt)
                .Select(e => new CalendarEventDto(e.Id, e.Title, e.StartAt, e.EndAt, e.Type, e.MeetingId, e.RoomId))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // GET /api/meetings/upcoming — upcoming meetings for current user
        g.MapGet("/upcoming", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await LoadUpcomingAsync(db, userId.Value, DateTime.UtcNow, ct));
        });

        // GET /api/meetings/past?days=30&before= — ended meetings I organised or attended (not declined), newest first,
        // 20 per page; attendance count and the recordings I may watch (v3 §5 Past).
        g.MapGet("/past", async (int? days, DateTime? before, HttpContext http, IPopDbContext db,
            IPop.Application.MeetingRooms.IMeetingRecordingService recs, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // Admins are not merged in (same as the recordings list): they audit from Administration.
            return Results.Ok(await LoadPastAsync(db, userId.Value, DateTime.UtcNow, days ?? PastDefaultDays, before, PastPageSize,
                id => recs.CanViewAsync(userId.Value, false, id, ct), ct));
        });

        // POST /api/meetings/{id}/cancel — cancel a meeting and notify attendees
        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var (result, meeting, attendeeUserIds) = await ApplyCancelAsync(db, id, userId.Value, ct);
            if (result == CancelResult.NotFound) return Results.NotFound();
            if (result == CancelResult.Forbidden)
                return Results.Json(new { code = "forbidden", message = "Only the organizer can cancel this meeting." }, statusCode: 403);

            if (result == CancelResult.Applied) await NotifyMeetingChangedAsync(db, realtime, id, [], ct);

            if (result == CancelResult.Applied && notifs != null && attendeeUserIds.Count > 0)
            {
                await PublishBestEffortAsync(notifs, NotifyLog(http), "meeting.cancelled",
                    $"Meeting cancelled: {meeting!.Title}",
                    $"The meeting \"{meeting.Title}\" scheduled for {meeting.StartAt.ToLocalTime():dd MMM, HH:mm} has been cancelled.",
                    attendeeUserIds.ToList(), "high", $"/calls?tab=scheduled&meeting={id}");
            }

            return Results.Ok(new { ok = true });
        });

        // PUT /api/meetings/{id} — organizer edits the meeting; attendees are notified.
        g.MapPut("/{id:guid}", async (Guid id, UpdateMeetingRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            // Attendees removed by this edit must also drop the meeting from their lists.
            var before = await db.MeetingAttendees.AsNoTracking().Where(a => a.MeetingId == id).Select(a => a.UserId).ToListAsync(ct);
            var issue = new CalendarEndpoints.SaveIssue();
            var (result, added, others) = await ApplyUpdateAsync(db, id, userId.Value, req, ct, issue: issue);
            switch (result)
            {
                case UpdateResult.InvalidRoomRange: return MeetingRoomEndpoints.RangeError(issue.Reason);
                case UpdateResult.NotFound: return Results.NotFound();
                case UpdateResult.Forbidden: return Results.Json(new { code = "forbidden", message = "Only the organizer can edit this meeting." }, statusCode: 403);
                case UpdateResult.Cancelled: return Results.Json(new { code = "cancelled", message = "This meeting was cancelled." }, statusCode: 409);
                case UpdateResult.InvalidTitle: return Results.Json(new { code = "invalid_title", message = "Meeting title is required." }, statusCode: 422);
                case UpdateResult.InvalidRange: return Results.Json(new { code = "invalid_range", message = "End time must be after start time." }, statusCode: 422);
                case UpdateResult.RoomBooked: return Results.Json(new { code = "room_booked", message = "That meeting room is already booked at this time." }, statusCode: 409);
                case UpdateResult.InvalidKind: return KindError(KindResult.InvalidKind);
                case UpdateResult.RoomRequired: return KindError(KindResult.RoomRequired);
            }
            await NotifyMeetingChangedAsync(db, realtime, id, before, ct);
            var dto = await LoadMeetingDto(id, userId.Value, db, ct);
            if (notifs is not null && dto is not null)
            {
                var link = $"/calls?tab=scheduled&meeting={id}";
                // Awaited, in sequence: both run on the request's DbContext (see PublishBestEffortAsync).
                var log = NotifyLog(http);
                await PublishBestEffortAsync(notifs, log, "meeting.invited", $"Meeting invite: {dto.Title}",
                    InviteMessage(dto.OrganizerName, dto.Title, dto.StartAt, dto.Location, dto.RoomPassword),
                    added.ToList(), "normal", link);
                await PublishBestEffortAsync(notifs, log, "meeting.updated", $"Meeting updated: {dto.Title}",
                    $"{dto.OrganizerName} updated \"{dto.Title}\".",
                    others.ToList(), "normal", link);
                await CalendarDelegateEndpoints.ForwardInvitesAsync(db, notifs, log, added, dto.Title, dto.StartAt, dto.OrganizerName, null, ct);
            }
            return Results.Ok(dto);
        });

        // POST /api/meetings/{id}/room-password — organizer sets a new hall-room password (old one stops working).
        g.MapPost("/{id:guid}/room-password", async (Guid id, HttpContext http, IPopDbContext db, IRealtime realtime, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var (result, password) = await ApplyNewRoomPasswordAsync(db, id, userId.Value, ct);
            switch (result)
            {
                case RoomPasswordResult.NotFound: return Results.NotFound();
                case RoomPasswordResult.Forbidden: return Results.Json(new { code = "forbidden", message = "Only the organizer can set a new password." }, statusCode: 403);
                case RoomPasswordResult.Cancelled: return Results.Json(new { code = "cancelled", message = "This meeting was cancelled." }, statusCode: 409);
                case RoomPasswordResult.NotHallRoom: return Results.Json(new { code = "not_hall_room", message = "This meeting is not in a meeting room." }, statusCode: 409);
            }
            await NotifyMeetingChangedAsync(db, realtime, id, [], ct);
            return Results.Ok(new RoomPasswordDto(password!));
        });

        // GET /api/meetings/join/{roomName} — resolve a /meet/{room} link to its meeting and,
        // when LiveKit is configured, mint a room token. Only the organizer/attendees may join.
        g.MapGet("/join/{roomName}", async (string roomName, HttpContext http, IPopDbContext db,
            IPop.Application.Calls.ILiveKitTokenService? liveKit, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await ResolveJoinAsync(db, liveKit, roomName, userId.Value, ct);
            return result.Code switch
            {
                null => Results.Ok(result.Join),
                "not_found" => Results.NotFound(),
                "not_invited" => Results.Json(new { code = "not_invited", message = "You are not an attendee of this meeting." }, statusCode: 403),
                _ => Results.Json(new { code = "cancelled", message = "This meeting was cancelled." }, statusCode: 409),
            };
        });

        return app;
    }

    public static async Task<(MeetingJoinDto? Join, string? Code)> ResolveJoinAsync(IPopDbContext db,
        IPop.Application.Calls.ILiveKitTokenService? liveKit, string roomName, Guid userId, CancellationToken ct)
    {
        if (MeetingIdFromRoom(roomName) is not { } meetingId) return (null, "not_found");
        var m = await db.Meetings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == meetingId, ct);
        if (m is null) return (null, "not_found");
        var allowed = m.OrganizerId == userId || await db.MeetingAttendees.AnyAsync(a => a.MeetingId == meetingId && a.UserId == userId, ct);
        if (!allowed) return (null, "not_invited");
        if (m.Status == MeetingStatus.Cancelled) return (null, "cancelled");

        var me = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Nik, u.Name }).FirstAsync(ct);
        var room = RoomNameFor(m.Id);
        string? url = null, token = null;
        if (liveKit is { IsConfigured: true })
        {
            url = liveKit.ServerUrl;
            token = liveKit.GenerateToken(room, me.Nik, me.Name, TimeSpan.FromHours(4));
        }
        return (new MeetingJoinDto(m.Id, m.Title, room, m.Link, url, token), null);
    }

    private static async Task<MeetingDto?> LoadMeetingDto(Guid id, Guid viewerId, IPopDbContext db, CancellationToken ct) =>
        (await ProjectAsync(db.Meetings.Where(m => m.Id == id
            && (m.OrganizerId == viewerId || m.Attendees.Any(a => a.UserId == viewerId))), viewerId, ct)).FirstOrDefault();

    /// Upcoming (not ended, not cancelled) meetings I organise or attend, soonest first — one
    /// SQL query with attendees projected in (was one query per meeting).
    public static Task<List<MeetingDto>> LoadUpcomingAsync(IPopDbContext db, Guid userId, DateTime nowUtc, CancellationToken ct) =>
        ProjectAsync(db.Meetings
            .Where(m => m.EndAt >= nowUtc && m.Status != MeetingStatus.Cancelled
                && (m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId)))
            .OrderBy(m => m.StartAt)
            .Take(50), userId, ct);

    public const int PastDefaultDays = 30, PastMaxDays = 90, PastPageSize = 20;

    /// Past meetings (v3 §5): ended, not cancelled, started within <paramref name="days"/> (1–90) before now, that I
    /// organised or attend without having declined; newest first, <paramref name="take"/> per page, older than
    /// <paramref name="before"/> (the last StartAt of the previous page). Room passwords are never returned for them.
    /// <para><b>Attended</b>: v2 persists no join log, only who was in the room while a server recording ran
    /// (MeetingRecordingViewers) and who entered with the password (MeetingRoomGuests). So: when the meeting has a
    /// recording, the distinct recording viewers plus guests; otherwise the organizer, the invitees who said yes and the
    /// guests. Voice and in-person meetings have no observation at all and use the second rule.</para>
    /// <para><b>Recordings</b>: Ready recordings of the meeting I was a viewer of, and that <paramref name="canView"/>
    /// (MeetingRecordingService.CanViewAsync, non-admin) still allows — the same rule as the recordings list.</para>
    public static async Task<PastMeetingsPage> LoadPastAsync(IPopDbContext db, Guid userId, DateTime nowUtc, int days, DateTime? before,
        int take, Func<Guid, Task<bool>>? canView, CancellationToken ct)
    {
        days = Math.Min(days, PastMaxDays);
        if (days <= 0) return new([], 0, false);
        take = Math.Clamp(take, 1, 50);
        var from = nowUtc.AddDays(-days);
        var mine = db.Meetings.Where(m => m.EndAt <= nowUtc && m.StartAt >= from && m.Status != MeetingStatus.Cancelled
            && (m.OrganizerId == userId || m.Attendees.Any(a => a.UserId == userId && a.RsvpStatus != "decline")));
        var total = await mine.CountAsync(ct);
        var paged = before is { } b ? mine.Where(m => m.StartAt < b) : mine;
        var ids = await paged.OrderByDescending(m => m.StartAt).Select(m => m.Id).Take(take + 1).ToListAsync(ct);
        var hasMore = ids.Count > take;
        if (hasMore) ids.RemoveAt(take);
        if (ids.Count == 0) return new([], total, false);

        var dtos = (await ProjectAsync(db.Meetings.Where(m => ids.Contains(m.Id)), userId, ct)).ToDictionary(m => m.Id);
        var recordings = await db.MeetingRecordings.AsNoTracking()
            .Where(r => r.MeetingId != null && ids.Contains(r.MeetingId.Value) && r.Status != MeetingRecordingStatus.Deleted)
            .Select(r => new { r.Id, MeetingId = r.MeetingId!.Value, r.Status, r.StartedAt, r.DurationSeconds,
                Viewers = r.Viewers.Select(v => v.UserId).ToList() })
            .ToListAsync(ct);
        var guests = await db.MeetingRoomGuests.AsNoTracking().Where(g => ids.Contains(g.MeetingId))
            .Select(g => new { g.MeetingId, g.UserId }).ToListAsync(ct);

        var items = new List<PastMeetingDto>();
        foreach (var id in ids)
        {
            if (!dtos.TryGetValue(id, out var dto)) continue;
            var recs = recordings.Where(r => r.MeetingId == id).ToList();
            var seen = guests.Where(g => g.MeetingId == id).Select(g => g.UserId).ToHashSet();
            if (recs.Count > 0) seen.UnionWith(recs.SelectMany(r => r.Viewers));
            else
            {
                seen.Add(dto.OrganizerId);
                seen.UnionWith(dto.Attendees.Where(a => a.RsvpStatus == "accept").Select(a => a.UserId));
            }
            var chips = new List<RecordingChipDto>();
            foreach (var r in recs.Where(r => r.Status == MeetingRecordingStatus.Ready && r.Viewers.Contains(userId)).OrderBy(r => r.StartedAt))
                if (canView is null || await canView(r.Id)) chips.Add(new(r.Id, r.StartedAt, r.DurationSeconds));
            items.Add(new(dto with { RoomPassword = null }, seen.Count, chips));
        }
        return new(items, total, hasMore);
    }

    private static async Task<List<MeetingDto>> ProjectAsync(IQueryable<Meeting> query, Guid viewerId, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .Select(m => new
            {
                m.Id, m.Title, m.StartAt, m.EndAt, m.TimeZone, m.Location, m.Link, m.OrganizerId, m.RoomPassword, m.Kind,
                OrganizerName = m.Organizer.Name, OrganizerNik = m.Organizer.Nik, m.Status, m.Agenda, m.RoomId, m.ReminderMinutes,
                Attendees = m.Attendees.OrderBy(a => a.User.Name)
                    .Select(a => new MeetingAttendeeDto(a.UserId, a.User.Name, a.User.Nik, a.Required, a.RsvpStatus, a.Note)).ToList(),
            })
            .ToListAsync(ct);
        return rows.OrderBy(m => m.StartAt).Select(m => new MeetingDto(
            m.Id, m.Title, m.StartAt, m.EndAt, m.TimeZone, m.Location, m.Link,
            m.OrganizerId, m.OrganizerName, m.OrganizerNik, m.Status, m.Attendees,
            m.Attendees.Count(a => a.RsvpStatus == "accept"),
            m.Attendees.Count(a => a.RsvpStatus == "maybe"),
            m.Attendees.Count(a => a.RsvpStatus == "decline"),
            m.Attendees.Count(a => a.RsvpStatus == "pending"),
            m.Attendees.FirstOrDefault(a => a.UserId == viewerId)?.RsvpStatus ?? "pending",
            m.Agenda, RoomNameFor(m.Id), m.RoomId, m.ReminderMinutes,
            // Room password: organizer and attendees who did not decline only (v2 §1).
            m.OrganizerId == viewerId || m.Attendees.Any(a => a.UserId == viewerId && a.RsvpStatus != "decline") ? m.RoomPassword : null,
            KindOf(m.Kind, m.Location, m.Attendees.Count > 0), RoomKeyOf(m.Kind, m.Location, m.Attendees.Count > 0))).ToList();
    }

    /// The kind a DTO reports: the stored one, else the inference rule (rows written before the MeetingKind migration
    /// reached them, e.g. by an older binary during a rollout).
    public static string? KindOf(string? kind, string? location, bool hasAttendees) =>
        MeetingKind.Of(kind, location, hasAttendees);

    /// The hall room key ("kalosi") of a room meeting, else null.
    public static string? RoomKeyOf(string? kind, string? location, bool hasAttendees) =>
        KindOf(kind, location, hasAttendees) == MeetingKind.Room ? IPop.Application.Hall.HallBooking.RoomKey(location) : null;

    public enum KindResult { Ok, InvalidKind, RoomRequired }

    /// v3 room booking limits (<see cref="RoomBookingRules.Validate"/>) in <paramref name="userId"/>'s time zone; null = allowed.
    /// <paramref name="nowUtc"/>: when the booking is made (tests seeding a booking that is already running); default now.
    /// <paramref name="seriesFirstUtc"/>: a later date of a series is within 60 days when the series' first date is.
    public static async Task<string?> RoomRangeAsync(IPopDbContext db, Guid userId, DateTime startUtc, DateTime endUtc, bool keepsStart,
        CancellationToken ct, DateTime? nowUtc = null, DateTime? seriesFirstUtc = null)
    {
        var tz = RoomBookingRules.ResolveZone(await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.TimeZoneId).FirstOrDefaultAsync(ct));
        var now = nowUtc ?? DateTime.UtcNow;
        var bad = RoomBookingRules.Validate(startUtc, endUtc, now, tz, keepsStart);
        if (bad == RoomBookingRules.TooFar && seriesFirstUtc is { } first)
            bad = RoomBookingRules.Validate(first, first + (endUtc - startUtc), now, tz);
        return bad;
    }

    /// The one kind rule for every meeting write (v3 §1). <paramref name="kind"/> null = infer from the place.
    /// voice: an explicit voice kind sets the place to "Online · iPop Meeting" and drops any link; room: the place must
    /// be an SJA Hall room; a hall-room place is always a room (the location stays the room key, v2 rules are frozen),
    /// so an in-person kind there becomes room. <paramref name="location"/> must already be cleaned and normalised.
    public static (KindResult Result, string? Kind, string? Location, string? Link) ResolveKind(string? kind, string? location, string? link,
        bool hasAttendees)
    {
        if (kind is not null && !MeetingKind.IsValid(kind)) return (KindResult.InvalidKind, null, location, link);
        var hall = IPop.Application.Hall.HallBooking.RoomKey(location) is not null;
        var k = kind ?? MeetingKind.Infer(location, hasAttendees);
        if (k == MeetingKind.Voice && kind is not null) return (KindResult.Ok, k, MeetingKind.OnlineLocation, null);
        if (k == MeetingKind.Room && !hall) return (KindResult.RoomRequired, null, location, link);
        if (k == MeetingKind.InPerson && hall) k = MeetingKind.Room;
        return (KindResult.Ok, k, location, link);
    }

    public static IResult KindError(KindResult r) => r switch
    {
        KindResult.InvalidKind => Results.Json(new { code = "invalid_kind", message = "Choose a voice call, a meeting room or in person." }, statusCode: 400),
        _ => Results.Json(new { code = "room_required", message = "Choose a meeting room." }, statusCode: 400),
    };

    /// InvalidRoomRange: a room meeting outside the v3 booking limits (reason in the SaveIssue).
    public enum CreateResult { Created, InvalidTitle, InvalidRange, NotRoomMember, RoomBooked, InvalidKind, RoomRequired, InvalidRoomRange }

    /// Create a meeting: attendees (ids and/or NIKs, organizer always in and accepted), the
    /// organizer's calendar row and one reminder per attendee when ReminderMinutes > 0
    /// (null/0 = no reminder). Returns the invitees to notify (everyone but the organizer).
    /// Room meetings must meet the v3 booking limits as of <paramref name="nowUtc"/> (default now).
    public static async Task<(CreateResult Result, Guid? MeetingId, IReadOnlyList<Guid> Invited)> ApplyCreateAsync(
        IPopDbContext db, Guid userId, CreateMeetingRequest req, CancellationToken ct, CalendarEndpoints.SaveIssue? issue = null,
        DateTime? nowUtc = null)
    {
        var title = req.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) return (CreateResult.InvalidTitle, null, []);
        var start = AsUtc(req.StartAt);
        var end = AsUtc(req.EndAt);
        if (end <= start) return (CreateResult.InvalidRange, null, []);
        if (req.RoomId is { } roomId
            && !await db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, ct))
            return (CreateResult.NotRoomMember, null, []);
        // A meeting always has its organizer as an attendee.
        var (kindResult, kind, location, link) = ResolveKind(req.Kind,
            string.IsNullOrWhiteSpace(req.Location) ? null : IPop.Application.Hall.HallBooking.Normalize(req.Location.Trim()),
            string.IsNullOrWhiteSpace(req.Link) ? null : req.Link.Trim(), hasAttendees: true);
        if (kindResult == KindResult.InvalidKind) return (CreateResult.InvalidKind, null, []);
        if (kindResult == KindResult.RoomRequired) return (CreateResult.RoomRequired, null, []);
        if (kind == MeetingKind.Room && await RoomRangeAsync(db, userId, start, end, false, ct, nowUtc) is { } bad)
        {
            if (issue is not null) issue.Reason = bad;
            return (CreateResult.InvalidRoomRange, null, []);
        }
        // SJA Hall meeting rooms ("SJA Hall · Robusta") can only be booked once at a time.
        if (await IPop.Infrastructure.Hall.HallBookings.ConflictAsync(db, location, start, end, null, ct) is not null)
            return (CreateResult.RoomBooked, null, []);

        var reminder = req.ReminderMinutes is > 0 and <= 10080 ? req.ReminderMinutes : null;
        var meeting = new Meeting
        {
            Id = Guid.NewGuid(), Title = title,
            StartAt = start, EndAt = end,
            TimeZone = req.TimeZone,
            Location = location, Link = link, Kind = kind,
            Agenda = string.IsNullOrWhiteSpace(req.Agenda) ? null : req.Agenda.Trim(),
            OrganizerId = userId, Status = MeetingStatus.Active, RoomId = req.RoomId,
            SourceMailId = req.SourceMailId,
            ReminderMinutes = reminder, CreatedAt = DateTime.UtcNow,
            Visibility = await CalendarSharingEndpoints.DefaultEventVisibilityAsync(db, userId, ct),
        };
        SyncRoomPassword(meeting);
        db.Meetings.Add(meeting);

        // Match Calendar's current group membership; explicit attendees are unioned below.
        // Peserta (organizer selalu ikut sebagai wajib). Terima UserId atau NIK; hanya akun aktif.
        var attendeeIds = new List<Guid>();
        if (req.RoomId is { } attendeeRoomId)
            attendeeIds.AddRange(await db.ChatRoomMembers.AsNoTracking()
                .Where(m => m.RoomId == attendeeRoomId && m.LeftAt == null).Select(m => m.UserId).ToListAsync(ct));
        if (req.AttendeeIds is { Count: > 0 })
            attendeeIds.AddRange(await db.Users.Where(u => req.AttendeeIds.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct));
        if (req.AttendeeNiks is { Count: > 0 })
            attendeeIds.AddRange(await db.Users.Where(u => req.AttendeeNiks.Contains(u.Nik) && u.IsActive).Select(u => u.Id).ToListAsync(ct));
        attendeeIds.Add(userId);
        attendeeIds = attendeeIds.Distinct().ToList();
        foreach (var uid in attendeeIds)
        {
            db.MeetingAttendees.Add(new MeetingAttendee
            {
                Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid,
                Required = uid == userId || (req.RequiredIds?.Contains(uid) ?? true),
                RsvpStatus = uid == userId ? "accept" : "pending",
                RespondedAt = uid == userId ? DateTime.UtcNow : null,
            });
        }

        // Event kalender untuk rapat ini (milik organizer).
        db.CalendarEvents.Add(new CalendarEvent
        {
            Id = Guid.NewGuid(), Title = title, StartAt = meeting.StartAt, EndAt = meeting.EndAt,
            Type = "rapat", OwnerId = userId, RoomId = req.RoomId, MeetingId = meeting.Id, CreatedAt = DateTime.UtcNow,
            Visibility = meeting.Visibility, Location = meeting.Location, Link = meeting.Link,
            Notes = meeting.Agenda, ReminderMinutes = reminder,
        });

        // Pengingat terjadwal per peserta, sesuai pilihan pembuat.
        if (reminder is { } minutes && meeting.StartAt.AddMinutes(-minutes) > DateTime.UtcNow)
        {
            var sendAt = meeting.StartAt.AddMinutes(-minutes);
            foreach (var uid in attendeeIds)
                db.MeetingReminders.Add(new MeetingReminder { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, SendAt = sendAt });
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (IsRoomOverlap(e))
        {
            // Someone booked the same room for an overlapping time between our check and this insert
            // (two "Use now" at the same moment): the database refused the second one.
            db.ChangeTracker.Clear();
            return (CreateResult.RoomBooked, null, []);
        }
        return (CreateResult.Created, meeting.Id, attendeeIds.Where(id => id != userId).ToList());
    }

    /// The meeting.invited text. A meeting in an SJA Hall room adds its password ("Room password: 123 456"), so a guest
    /// the invitee forwards it to can get in; the recipients are the invitees themselves (v2 §1.4). Never in the link.
    public static string InviteMessage(string who, string title, DateTime startUtc, string? location, string? roomPassword) =>
        $"{who} invited you to \"{title}\" on {startUtc.ToLocalTime():dd MMM, HH:mm}." + RoomPasswordLine(location, roomPassword);

    /// " Room password: 123 456" for a hall-room meeting with a password, else "".
    public static string RoomPasswordLine(string? location, string? roomPassword) =>
        IPop.Application.Hall.HallBooking.RoomKey(location) is not null && !string.IsNullOrEmpty(roomPassword)
            ? $" Room password: {IPop.Application.Hall.RoomPasswords.Format(roomPassword)}"
            : "";

    /// The RSVP answer. A delegate answering for the invited owner (actedBy set) sees the meeting as the owner does, but
    /// never its room password: the password is only for the organizer and the invitees themselves (v2 §1.4).
    public static async Task<MeetingDto?> RsvpDtoAsync(IPopDbContext db, Guid id, Guid attendee, Guid? actedBy, CancellationToken ct)
    {
        var dto = await LoadMeetingDto(id, attendee, db, ct);
        return dto is not null && actedBy is { } a && a != attendee ? dto with { RoomPassword = null } : dto;
    }

    /// Notifications after a create. A mail invitation (SourceMailId) already told its recipients through mail.new, so only a
    /// hall-room meeting still sends meeting.invited: the mail does not carry the room password.
    public static async Task PublishCreatedAsync(IPopDbContext db, INotificationGatewayService notifs, MeetingDto dto, IReadOnlyList<Guid> invited,
        Guid? sourceMailId, ILogger? log, CancellationToken ct)
    {
        if (sourceMailId is null)
        {
            await PublishInvitedAsync(notifs, dto, invited, ct, log);
            await CalendarDelegateEndpoints.ForwardInvitesAsync(db, notifs, log, invited, dto.Title, dto.StartAt, dto.OrganizerName, null, ct);
        }
        else if (IPop.Application.Hall.HallBooking.RoomKey(dto.Location) is not null && !string.IsNullOrEmpty(dto.RoomPassword))
        {
            await PublishInvitedAsync(notifs, dto, invited, ct, log);
        }
    }

    /// meeting.invited to every invitee (not the organizer), deep-linked to the meeting.
    /// Best-effort (see <see cref="PublishBestEffortAsync"/>): the meeting is already saved.
    public static Task PublishInvitedAsync(INotificationGatewayService notifs, MeetingDto dto, IReadOnlyList<Guid> invited, CancellationToken ct, ILogger? log = null)
    {
        return PublishBestEffortAsync(notifs, log, "meeting.invited", $"Meeting invite: {dto.Title}",
            InviteMessage(dto.OrganizerName, dto.Title, dto.StartAt, dto.Location, dto.RoomPassword),
            invited, "normal", $"/calls?tab=scheduled&meeting={dto.Id}");
    }

    /// A meetings notification after a write that has already been saved. It runs on the
    /// request's scoped DbContext, so it must be awaited — a `_ =` fire-and-forget publish
    /// races the next query on the same context ("A second operation was started on this
    /// context") and turns a successful save into a 500. CancellationToken.None so a client
    /// disconnect can't abort it halfway; any failure is logged and never fails the request.
    public static async Task PublishBestEffortAsync(INotificationGatewayService? notifs, ILogger? log,
        string eventType, string title, string message, IReadOnlyCollection<Guid> userIds, string priority, string? targetUrl)
    {
        if (notifs is null || userIds.Count == 0) return;
        try
        {
            await notifs.PublishInternalAsync("meetings", eventType, title, message,
                new NotificationTarget(UserIds: userIds.ToList()), priority, targetUrl, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Publishing {EventType} notification to {Count} user(s) failed; the change itself was saved.", eventType, userIds.Count);
        }
    }

    internal static ILogger? NotifyLog(HttpContext http) =>
        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("IPop.Api.Endpoints.MeetingNotifications");

    public enum CancelResult { Applied, AlreadyCancelled, NotFound, Forbidden }

    /// Live refresh for a meeting: meetingChanged(meetingId) + calendarChanged to the organizer,
    /// every current attendee and alsoNotify (e.g. attendees an edit just removed). Meetings
    /// write calendar rows, so both views resync. Best-effort: a hub failure never fails the request.
    public static async Task NotifyMeetingChangedAsync(IPopDbContext db, IRealtime realtime, Guid meetingId,
        IEnumerable<Guid> alsoNotify, CancellationToken ct)
    {
        try
        {
            var ids = await db.MeetingAttendees.AsNoTracking().Where(a => a.MeetingId == meetingId).Select(a => a.UserId).ToListAsync(ct);
            ids.AddRange(await db.Meetings.AsNoTracking().Where(m => m.Id == meetingId).Select(m => m.OrganizerId).ToListAsync(ct));
            ids.AddRange(alsoNotify);
            var niks = await NiksAsync(db, ids, ct);
            if (niks.Count == 0) return;
            await realtime.ToUsersAsync(niks, RealtimeEvents.MeetingChanged, meetingId, ct);
            await realtime.ToUsersAsync(niks, RealtimeEvents.CalendarChanged, ct: ct);
        }
        catch { /* realtime is a hint; clients re-fetch on next load */ }
    }

    /// calendarChanged to the given users (personal calendar events have no attendees).
    public static async Task NotifyCalendarChangedAsync(IPopDbContext db, IRealtime realtime, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        try
        {
            var niks = await NiksAsync(db, userIds, ct);
            if (niks.Count > 0) await realtime.ToUsersAsync(niks, RealtimeEvents.CalendarChanged, ct: ct);
        }
        catch { /* realtime is a hint */ }
    }

    private static async Task<List<string>> NiksAsync(IPopDbContext db, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => u.Nik).ToListAsync(ct);
    }

    /// Organizer-only cancel: status becomes Cancelled, unsent reminders and the meeting's
    /// calendar rows are deleted. Returns the attendees to notify (everyone but the organizer).
    public static async Task<(CancelResult Result, Meeting? Meeting, IReadOnlyList<Guid> Attendees)> ApplyCancelAsync(
        IPopDbContext db, Guid meetingId, Guid userId, CancellationToken ct)
    {
        var meeting = await db.Meetings.Include(m => m.Attendees).FirstOrDefaultAsync(m => m.Id == meetingId, ct);
        if (meeting is null) return (CancelResult.NotFound, null, []);
        if (meeting.OrganizerId != userId) return (CancelResult.Forbidden, meeting, []);
        if (meeting.Status == MeetingStatus.Cancelled) return (CancelResult.AlreadyCancelled, meeting, []);

        if (IPop.Application.Hall.HallBooking.RoomKey(meeting.Location) is { } originalRoom)
            await IPop.Infrastructure.Hall.RoomAccess.BookingsAsync(db, originalRoom, DateTime.UtcNow,
                IPop.Infrastructure.Hall.RoomOccupancy.Empty, ct);
        meeting.Status = MeetingStatus.Cancelled;
        db.MeetingReminders.RemoveRange(await db.MeetingReminders.Where(r => r.MeetingId == meetingId && r.SentAt == null).ToListAsync(ct));
        db.CalendarEvents.RemoveRange(await db.CalendarEvents.Where(e => e.MeetingId == meetingId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return (CancelResult.Applied, meeting, meeting.Attendees.Where(a => a.UserId != userId).Select(a => a.UserId).ToList());
    }

    /// Video room name for a meeting — stable, so the Dashboard/Calls "Join" link and the
    /// LiveKit room agree without storing anything extra.
    public static string RoomNameFor(Guid meetingId) => $"meeting-{meetingId:N}";

    public static Guid? MeetingIdFromRoom(string? roomName) =>
        roomName is not null && roomName.StartsWith("meeting-", StringComparison.Ordinal)
            && Guid.TryParseExact(roomName["meeting-".Length..], "N", out var id) ? id : null;

    /// A meeting in a hall room ("SJA Hall · …") has a 6-digit room password; it is made when the meeting is created
    /// there or moved there, kept while it stays, and dropped when the meeting moves elsewhere.
    public static void SyncRoomPassword(Meeting m)
    {
        if (IPop.Application.Hall.HallBooking.RoomKey(m.Location) is null) m.RoomPassword = null;
        else m.RoomPassword ??= IPop.Application.Hall.RoomPasswords.New();
    }

    public enum RoomPasswordResult { Applied, NotFound, Forbidden, Cancelled, NotHallRoom }

    /// Organizer only: a new room password. The old one stops working; guests already admitted stay admitted.
    public static async Task<(RoomPasswordResult Result, string? Password)> ApplyNewRoomPasswordAsync(IPopDbContext db, Guid meetingId,
        Guid userId, CancellationToken ct)
    {
        var m = await db.Meetings.FirstOrDefaultAsync(x => x.Id == meetingId, ct);
        if (m is null) return (RoomPasswordResult.NotFound, null);
        if (m.OrganizerId != userId) return (RoomPasswordResult.Forbidden, null);
        if (m.Status == MeetingStatus.Cancelled) return (RoomPasswordResult.Cancelled, null);
        if (IPop.Application.Hall.HallBooking.RoomKey(m.Location) is null) return (RoomPasswordResult.NotHallRoom, null);
        string next;
        do next = IPop.Application.Hall.RoomPasswords.New(); while (next == m.RoomPassword);
        m.RoomPassword = next;
        await db.SaveChangesAsync(ct);
        return (RoomPasswordResult.Applied, next);
    }

    public record RoomPasswordDto(string Password);

    public enum UpdateResult { Applied, NotFound, Forbidden, InvalidTitle, InvalidRange, Cancelled, RoomBooked, InvalidKind, RoomRequired, InvalidRoomRange }

    /// Organizer-only edit: fields, attendee list (by NIK; organizer always stays), calendar
    /// row and unsent reminders follow the new time. Returns newly invited + all other attendees.
    /// <paramref name="keepAnswers"/>: a new time normally sends everyone's RSVP back to pending; a meeting-room
    /// "Extend 15 min" keeps them (and so keeps a decline a decline — declined people stay out of the room).
    public static async Task<(UpdateResult Result, IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Others)> ApplyUpdateAsync(
        IPopDbContext db, Guid meetingId, Guid userId, UpdateMeetingRequest req, CancellationToken ct, bool keepAnswers = false,
        CalendarEndpoints.SaveIssue? issue = null, DateTime? seriesFirstUtc = null)
    {
        var m = await db.Meetings.Include(x => x.Attendees).FirstOrDefaultAsync(x => x.Id == meetingId, ct);
        if (m is null) return (UpdateResult.NotFound, [], []);
        if (m.OrganizerId != userId) return (UpdateResult.Forbidden, [], []);
        if (m.Status == MeetingStatus.Cancelled) return (UpdateResult.Cancelled, [], []);
        var title = req.Title?.Trim() ?? "";
        if (title.Length == 0) return (UpdateResult.InvalidTitle, [], []);
        if (req.EndAt <= req.StartAt) return (UpdateResult.InvalidRange, [], []);
        var (kindResult, kind, location, link) = ResolveKind(req.Kind,
            string.IsNullOrWhiteSpace(req.Location) ? null : IPop.Application.Hall.HallBooking.Normalize(req.Location.Trim()),
            string.IsNullOrWhiteSpace(req.Link) ? null : req.Link.Trim(), hasAttendees: true);
        if (kindResult == KindResult.InvalidKind) return (UpdateResult.InvalidKind, [], []);
        if (kindResult == KindResult.RoomRequired) return (UpdateResult.RoomRequired, [], []);
        // An edit that keeps the start in the same room (title edits, Extend 15 min, a far date of a series) only checks length
        // and same day;
        // a new start must not be in the past nor more than 60 days ahead.
        if (kind == MeetingKind.Room
            && await RoomRangeAsync(db, userId, AsUtc(req.StartAt), AsUtc(req.EndAt),
                AsUtc(req.StartAt) == m.StartAt && IPop.Application.Hall.HallBooking.Normalize(m.Location) == location, ct,
                seriesFirstUtc: seriesFirstUtc) is { } bad)
        {
            if (issue is not null) issue.Reason = bad;
            return (UpdateResult.InvalidRoomRange, [], []);
        }
        if (await IPop.Infrastructure.Hall.HallBookings.ConflictAsync(db, location, AsUtc(req.StartAt), AsUtc(req.EndAt), m.Id, ct) is not null)
            return (UpdateResult.RoomBooked, [], []);

        // Claim the original physical room BEFORE mutable fields are rewritten, even if no client/worker observed it.
        if (IPop.Application.Hall.HallBooking.RoomKey(m.Location) is { } originalRoom)
            await IPop.Infrastructure.Hall.RoomAccess.BookingsAsync(db, originalRoom, DateTime.UtcNow,
                IPop.Infrastructure.Hall.RoomOccupancy.Empty, ct);
        var timeChanged = AsUtc(req.StartAt) != m.StartAt || AsUtc(req.EndAt) != m.EndAt;
        m.Title = title;
        m.StartAt = AsUtc(req.StartAt);
        m.EndAt = AsUtc(req.EndAt);
        m.Location = location;
        m.Kind = kind;
        // Room: a password (kept while it stays a room); any other kind: none (v3 review focus 6).
        SyncRoomPassword(m);
        m.Link = link;
        m.Agenda = string.IsNullOrWhiteSpace(req.Agenda) ? null : req.Agenda.Trim();
        if (req.ReminderMinutes is not null) m.ReminderMinutes = req.ReminderMinutes > 0 ? req.ReminderMinutes : null;

        var added = new List<Guid>();
        var attendeeIds = m.Attendees.Select(a => a.UserId).ToHashSet();
        if (req.AttendeeNiks is not null)
        {
            var wanted = await db.Users.Where(u => req.AttendeeNiks.Contains(u.Nik)).Select(u => u.Id).ToListAsync(ct);
            wanted.Add(userId);
            attendeeIds = wanted.ToHashSet();
            foreach (var gone in m.Attendees.Where(a => !attendeeIds.Contains(a.UserId)).ToList())
            {
                db.MeetingAttendees.Remove(gone);
                m.Attendees.Remove(gone);
            }
            foreach (var uid in wanted.Distinct().Where(uid => m.Attendees.All(a => a.UserId != uid)).ToList())
            {
                var att = new MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = uid, Required = true, RsvpStatus = "pending" };
                db.MeetingAttendees.Add(att);
                m.Attendees.Add(att);
                added.Add(uid);
            }
        }

        // A new time invalidates earlier answers — everyone but the organizer answers again.
        if (timeChanged && !keepAnswers)
            foreach (var a in m.Attendees.Where(a => a.UserId != userId))
            {
                a.RsvpStatus = "pending";
                a.RespondedAt = null;
                a.RespondedById = null;
            }

        foreach (var ev in await db.CalendarEvents.Where(e => e.MeetingId == m.Id).ToListAsync(ct))
        {
            ev.Title = title;
            ev.StartAt = m.StartAt;
            ev.EndAt = m.EndAt;
        }

        db.MeetingReminders.RemoveRange(await db.MeetingReminders.Where(r => r.MeetingId == m.Id && r.SentAt == null).ToListAsync(ct));
        if (m.ReminderMinutes is > 0)
        {
            var sendAt = m.StartAt.AddMinutes(-m.ReminderMinutes.Value);
            if (sendAt > DateTime.UtcNow)
                foreach (var uid in attendeeIds)
                    db.MeetingReminders.Add(new MeetingReminder { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = uid, SendAt = sendAt });
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (IsRoomOverlap(e))
        {
            db.ChangeTracker.Clear();
            return (UpdateResult.RoomBooked, [], []);
        }
        var others = m.Attendees.Select(a => a.UserId).Where(id => id != userId && !added.Contains(id)).ToList();
        return (UpdateResult.Applied, added, others);
    }

    /// The database's exclusion constraint on hall rooms (migration HallRoomNoOverlap) refused an overlapping booking.
    internal static bool IsRoomOverlap(DbUpdateException e) =>
        e.InnerException is Npgsql.PostgresException { SqlState: "23P01" } pg && pg.ConstraintName == HallRoomNoOverlapConstraint;

    public const string HallRoomNoOverlapConstraint = "EX_Meetings_HallRoom_NoOverlap";

    private static DateTime AsUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    /// Terapkan jawaban RSVP peserta (dipisah agar bisa diuji unit tanpa HTTP host).
    public enum RsvpResult { Applied, InvalidResponse, NotInvited }
    public static async Task<RsvpResult> ApplyRsvpAsync(IPopDbContext db, Guid meetingId, Guid userId, string? response, string? note, CancellationToken ct,
        Guid? respondedBy = null)
    {
        var r = response?.Trim().ToLowerInvariant() ?? "";
        if (r is not ("accept" or "maybe" or "decline")) return RsvpResult.InvalidResponse;
        var att = await db.MeetingAttendees.FirstOrDefaultAsync(a => a.MeetingId == meetingId && a.UserId == userId, ct);
        if (att is null) return RsvpResult.NotInvited;
        att.RsvpStatus = r;
        att.Note = note?.Trim();
        att.RespondedAt = DateTime.UtcNow;
        att.RespondedById = respondedBy;
        await db.SaveChangesAsync(ct);
        return RsvpResult.Applied;
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct = default)
    {
        // The access token already carries the user id (sub); only tokens without it fall back to the NIK lookup.
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    public record CreateMeetingRequest(string? Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        IReadOnlyList<Guid>? AttendeeIds, IReadOnlyList<Guid>? RequiredIds, int? ReminderMinutes, Guid? RoomId,
        IReadOnlyList<string>? AttendeeNiks = null, Guid? SourceMailId = null, string? Agenda = null, string? Kind = null);
    /// Kind null = infer from Location (MeetingKind.Infer).
    public record UpdateMeetingRequest(string? Title, DateTime StartAt, DateTime EndAt, string? Location, string? Link, string? Agenda,
        IReadOnlyList<string>? AttendeeNiks, int? ReminderMinutes, string? Kind = null);
    public record MeetingJoinDto(Guid MeetingId, string Title, string RoomName, string? ExternalLink, string? LiveKitUrl, string? LiveKitToken);
    public record RsvpRequest(string? Response, string? Note, string? OnBehalfOf = null);
    public record CreateCalendarEventRequest(string? Title, DateTime StartAt, DateTime EndAt, string? Type, Guid? RoomId);
    public record MeetingAttendeeDto(Guid UserId, string Name, string Nik, bool Required, string RsvpStatus, string? Note);
    public record MeetingDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        Guid OrganizerId, string OrganizerName, string OrganizerNik, string Status, IReadOnlyList<MeetingAttendeeDto> Attendees,
        int AcceptCount, int MaybeCount, int DeclineCount, int PendingCount, string MyResponse,
        string? Agenda = null, string? RoomName = null, Guid? RoomId = null, int? ReminderMinutes = null, string? RoomPassword = null,
        string? Kind = null, string? RoomKey = null);
    public record CalendarEventDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string Type, Guid? MeetingId, Guid? RoomId);
    /// A recording of a past meeting the viewer may watch (Calls › Meeting Rooms ?recording={id} plays it).
    public record RecordingChipDto(Guid Id, DateTime StartedAt, int? DurationSeconds);
    /// <paramref name="Attended"/>: see <see cref="LoadPastAsync"/>.
    public record PastMeetingDto(MeetingDto Meeting, int? Attended, IReadOnlyList<RecordingChipDto> Recordings);
    /// <paramref name="Total"/>: all past meetings in the window; <paramref name="HasMore"/>: another page exists.
    public record PastMeetingsPage(IReadOnlyList<PastMeetingDto> Items, int Total, bool HasMore);
}
