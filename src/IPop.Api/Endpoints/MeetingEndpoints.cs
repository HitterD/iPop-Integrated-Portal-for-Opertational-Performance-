using IPop.Api.Rbac;
using IPop.Application.Notifications;
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

        // POST /api/meetings — buat rapat + peserta + pengingat.
        g.MapPost("/", async (CreateMeetingRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var title = req.Title?.Trim() ?? "";
            if (title.Length == 0) return Results.Json(new { code = "invalid_title", message = "Meeting title is required." }, statusCode: 422);
            if (req.EndAt <= req.StartAt) return Results.Json(new { code = "invalid_range", message = "End time must be after start time." }, statusCode: 422);

            var meeting = new Meeting
            {
                Id = Guid.NewGuid(), Title = title,
                StartAt = AsUtc(req.StartAt), EndAt = AsUtc(req.EndAt),
                TimeZone = req.TimeZone, Location = req.Location, Link = req.Link, Agenda = req.Agenda?.Trim(),
                OrganizerId = userId.Value, Status = "aktif", RoomId = req.RoomId,
                SourceMailId = req.SourceMailId,
                ReminderMinutes = req.ReminderMinutes, CreatedAt = DateTime.UtcNow,
            };
            db.Meetings.Add(meeting);

            // Peserta (organizer selalu ikut sebagai wajib). Terima UserId atau NIK.
            var attendeeIds = (req.AttendeeIds ?? []).Append(userId.Value).ToList();
            if (req.AttendeeNiks is { Count: > 0 })
            {
                var nikIds = await db.Users.Where(u => req.AttendeeNiks.Contains(u.Nik)).Select(u => u.Id).ToListAsync(ct);
                attendeeIds.AddRange(nikIds);
            }
            attendeeIds = attendeeIds.Distinct().ToList();
            foreach (var uid in attendeeIds)
            {
                db.MeetingAttendees.Add(new MeetingAttendee
                {
                    Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid,
                    Required = uid == userId.Value || (req.RequiredIds?.Contains(uid) ?? true),
                    RsvpStatus = uid == userId.Value ? "accept" : "pending",
                    RespondedAt = uid == userId.Value ? DateTime.UtcNow : null,
                });
            }

            // Event kalender untuk rapat ini (milik organizer).
            db.CalendarEvents.Add(new CalendarEvent
            {
                Id = Guid.NewGuid(), Title = title, StartAt = meeting.StartAt, EndAt = meeting.EndAt,
                Type = "rapat", OwnerId = userId.Value, RoomId = req.RoomId, MeetingId = meeting.Id, CreatedAt = DateTime.UtcNow,
            });

            // Pengingat terjadwal per peserta.
            if (req.ReminderMinutes is > 0)
            {
                var sendAt = req.StartAt.AddMinutes(-req.ReminderMinutes.Value);
                foreach (var uid in attendeeIds)
                {
                    db.MeetingReminders.Add(new MeetingReminder
                    {
                        Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = uid, SendAt = sendAt,
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(await LoadMeetingDto(meeting.Id, userId.Value, db, ct));
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
        g.MapPost("/{id:guid}/rsvp", async (Guid id, RsvpRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await ApplyRsvpAsync(db, id, userId.Value, req.Response, req.Note, ct);
            return result switch
            {
                RsvpResult.InvalidResponse => Results.Json(new { code = "invalid_response", message = "Response must be accept/maybe/decline." }, statusCode: 422),
                RsvpResult.NotInvited => Results.Json(new { code = "not_invited", message = "You are not an attendee of this meeting." }, statusCode: 403),
                _ => Results.Ok(await LoadMeetingDto(id, userId.Value, db, ct)),
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
        g.MapPost("/calendar", async (CreateCalendarEventRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
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
                var memberIds = await db.ChatRoomMembers.AsNoTracking()
                    .Where(m => m.RoomId == roomId).Select(m => m.UserId).ToListAsync(ct);
                if (!memberIds.Contains(userId.Value))
                    return Results.Json(new { code = "forbidden", message = "Not a room member." }, statusCode: 403);

                var meeting = new Meeting
                {
                    Id = Guid.NewGuid(), Title = title, StartAt = startAt, EndAt = endAt,
                    OrganizerId = userId.Value, Status = "aktif", RoomId = roomId,
                    ReminderMinutes = 30, CreatedAt = DateTime.UtcNow,
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
            return Results.Ok(new CalendarEventDto(ev.Id, ev.Title, ev.StartAt, ev.EndAt, ev.Type, ev.MeetingId, ev.RoomId));
        });

        // GET /api/meetings/room/{roomId} — event kalender untuk satu ruang obrolan.
        g.MapGet("/room/{roomId:guid}", async (Guid roomId, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var isMember = await db.ChatRoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value, ct);
            if (!isMember) return Results.Json(new { code = "forbidden", message = "Bukan anggota ruang." }, statusCode: 403);

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
            var now = DateTime.UtcNow;

            var meetingIds = await db.MeetingAttendees.AsNoTracking()
                .Where(a => a.UserId == userId.Value)
                .Select(a => a.MeetingId)
                .Union(db.Meetings.AsNoTracking().Where(m => m.OrganizerId == userId.Value).Select(m => m.Id))
                .Distinct()
                .ToListAsync(ct);

            var meetings = await db.Meetings.AsNoTracking()
                .Where(m => meetingIds.Contains(m.Id) && m.EndAt >= now && m.Status != "cancelled")
                .OrderBy(m => m.StartAt)
                .Take(50)
                .Select(m => m.Id)
                .ToListAsync(ct);

            var dtos = new List<MeetingDto>();
            foreach (var id in meetings)
            {
                var dto = await LoadMeetingDto(id, userId.Value, db, ct);
                if (dto is not null) dtos.Add(dto);
            }
            return Results.Ok(dtos);
        });

        // POST /api/meetings/{id}/cancel — cancel a meeting and notify attendees
        g.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var meeting = await db.Meetings.Include(m => m.Attendees).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (meeting is null) return Results.NotFound();
            if (meeting.OrganizerId != userId.Value)
                return Results.Json(new { code = "forbidden", message = "Only the organizer can cancel this meeting." }, statusCode: 403);

            meeting.Status = "cancelled";
            await db.SaveChangesAsync(ct);

            var attendeeUserIds = meeting.Attendees.Where(a => a.UserId != userId.Value).Select(a => a.UserId).ToList();
            if (notifs != null && attendeeUserIds.Count > 0)
            {
                _ = notifs.PublishInternalAsync("meetings", "meeting.cancelled",
                    $"Meeting cancelled: {meeting.Title}",
                    $"The meeting \"{meeting.Title}\" scheduled for {meeting.StartAt:dd MMM, HH:mm} has been cancelled.",
                    new NotificationTarget(UserIds: attendeeUserIds), "high", "/calls", ct);
            }

            return Results.Ok(new { ok = true });
        });

        // PUT /api/meetings/{id} — organizer edits the meeting; attendees are notified.
        g.MapPut("/{id:guid}", async (Guid id, UpdateMeetingRequest req, HttpContext http, IPopDbContext db,
            INotificationGatewayService? notifs, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var (result, added, others) = await ApplyUpdateAsync(db, id, userId.Value, req, ct);
            switch (result)
            {
                case UpdateResult.NotFound: return Results.NotFound();
                case UpdateResult.Forbidden: return Results.Json(new { code = "forbidden", message = "Only the organizer can edit this meeting." }, statusCode: 403);
                case UpdateResult.Cancelled: return Results.Json(new { code = "cancelled", message = "This meeting was cancelled." }, statusCode: 409);
                case UpdateResult.InvalidTitle: return Results.Json(new { code = "invalid_title", message = "Meeting title is required." }, statusCode: 422);
                case UpdateResult.InvalidRange: return Results.Json(new { code = "invalid_range", message = "End time must be after start time." }, statusCode: 422);
            }
            var dto = await LoadMeetingDto(id, userId.Value, db, ct);
            if (notifs is not null && dto is not null)
            {
                var link = $"/calls?tab=scheduled&meeting={id}";
                if (added.Count > 0)
                    _ = notifs.PublishInternalAsync("meetings", "meeting.invited", $"Meeting invite: {dto.Title}",
                        $"{dto.OrganizerName} invited you to \"{dto.Title}\" on {dto.StartAt:dd MMM, HH:mm}.",
                        new NotificationTarget(UserIds: added.ToList()), "normal", link, ct);
                if (others.Count > 0)
                    _ = notifs.PublishInternalAsync("meetings", "meeting.updated", $"Meeting updated: {dto.Title}",
                        $"{dto.OrganizerName} updated \"{dto.Title}\".",
                        new NotificationTarget(UserIds: others.ToList()), "normal", link, ct);
            }
            return Results.Ok(dto);
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
        if (m.Status == "cancelled") return (null, "cancelled");

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

    private static async Task<MeetingDto?> LoadMeetingDto(Guid id, Guid viewerId, IPopDbContext db, CancellationToken ct)
    {
        var m = await db.Meetings.AsNoTracking()
            .Include(x => x.Organizer)
            .Include(x => x.Attendees).ThenInclude(a => a.User)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;
        var isParticipant = m.OrganizerId == viewerId || m.Attendees.Any(a => a.UserId == viewerId);
        if (!isParticipant) return null;

        return new MeetingDto(
            m.Id, m.Title, m.StartAt, m.EndAt, m.TimeZone, m.Location, m.Link,
            m.OrganizerId, m.Organizer.Name, m.Organizer.Nik, m.Status,
            m.Attendees.Select(a => new MeetingAttendeeDto(a.UserId, a.User.Name, a.User.Nik, a.Required, a.RsvpStatus, a.Note)).ToList(),
            m.Attendees.Count(a => a.RsvpStatus == "accept"),
            m.Attendees.Count(a => a.RsvpStatus == "maybe"),
            m.Attendees.Count(a => a.RsvpStatus == "decline"),
            m.Attendees.Count(a => a.RsvpStatus == "pending"),
            m.Attendees.FirstOrDefault(a => a.UserId == viewerId)?.RsvpStatus ?? "pending",
            m.Agenda, RoomNameFor(m.Id));
    }

    /// Video room name for a meeting — stable, so the Dashboard/Calls "Join" link and the
    /// LiveKit room agree without storing anything extra.
    public static string RoomNameFor(Guid meetingId) => $"meeting-{meetingId:N}";

    public static Guid? MeetingIdFromRoom(string? roomName) =>
        roomName is not null && roomName.StartsWith("meeting-", StringComparison.Ordinal)
            && Guid.TryParseExact(roomName["meeting-".Length..], "N", out var id) ? id : null;

    public enum UpdateResult { Applied, NotFound, Forbidden, InvalidTitle, InvalidRange, Cancelled }

    /// Organizer-only edit: fields, attendee list (by NIK; organizer always stays), calendar
    /// row and unsent reminders follow the new time. Returns newly invited + all other attendees.
    public static async Task<(UpdateResult Result, IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Others)> ApplyUpdateAsync(
        IPopDbContext db, Guid meetingId, Guid userId, UpdateMeetingRequest req, CancellationToken ct)
    {
        var m = await db.Meetings.Include(x => x.Attendees).FirstOrDefaultAsync(x => x.Id == meetingId, ct);
        if (m is null) return (UpdateResult.NotFound, [], []);
        if (m.OrganizerId != userId) return (UpdateResult.Forbidden, [], []);
        if (m.Status == "cancelled") return (UpdateResult.Cancelled, [], []);
        var title = req.Title?.Trim() ?? "";
        if (title.Length == 0) return (UpdateResult.InvalidTitle, [], []);
        if (req.EndAt <= req.StartAt) return (UpdateResult.InvalidRange, [], []);

        var timeChanged = AsUtc(req.StartAt) != m.StartAt || AsUtc(req.EndAt) != m.EndAt;
        m.Title = title;
        m.StartAt = AsUtc(req.StartAt);
        m.EndAt = AsUtc(req.EndAt);
        m.Location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim();
        m.Link = string.IsNullOrWhiteSpace(req.Link) ? null : req.Link.Trim();
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
        if (timeChanged)
            foreach (var a in m.Attendees.Where(a => a.UserId != userId))
            {
                a.RsvpStatus = "pending";
                a.RespondedAt = null;
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

        await db.SaveChangesAsync(ct);
        var others = m.Attendees.Select(a => a.UserId).Where(id => id != userId && !added.Contains(id)).ToList();
        return (UpdateResult.Applied, added, others);
    }

    private static DateTime AsUtc(DateTime dt) => dt.Kind == DateTimeKind.Utc ? dt : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    /// Terapkan jawaban RSVP peserta (dipisah agar bisa diuji unit tanpa HTTP host).
    public enum RsvpResult { Applied, InvalidResponse, NotInvited }
    public static async Task<RsvpResult> ApplyRsvpAsync(IPopDbContext db, Guid meetingId, Guid userId, string? response, string? note, CancellationToken ct)
    {
        var r = response?.Trim().ToLowerInvariant() ?? "";
        if (r is not ("accept" or "maybe" or "decline")) return RsvpResult.InvalidResponse;
        var att = await db.MeetingAttendees.FirstOrDefaultAsync(a => a.MeetingId == meetingId && a.UserId == userId, ct);
        if (att is null) return RsvpResult.NotInvited;
        att.RsvpStatus = r;
        att.Note = note?.Trim();
        att.RespondedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return RsvpResult.Applied;
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct = default)
    {
        var nik = http.User.FindFirst("nik")?.Value ?? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }

    public record CreateMeetingRequest(string? Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        IReadOnlyList<Guid>? AttendeeIds, IReadOnlyList<Guid>? RequiredIds, int? ReminderMinutes, Guid? RoomId,
        IReadOnlyList<string>? AttendeeNiks = null, Guid? SourceMailId = null, string? Agenda = null);
    public record UpdateMeetingRequest(string? Title, DateTime StartAt, DateTime EndAt, string? Location, string? Link, string? Agenda,
        IReadOnlyList<string>? AttendeeNiks, int? ReminderMinutes);
    public record MeetingJoinDto(Guid MeetingId, string Title, string RoomName, string? ExternalLink, string? LiveKitUrl, string? LiveKitToken);
    public record RsvpRequest(string? Response, string? Note);
    public record CreateCalendarEventRequest(string? Title, DateTime StartAt, DateTime EndAt, string? Type, Guid? RoomId);
    public record MeetingAttendeeDto(Guid UserId, string Name, string Nik, bool Required, string RsvpStatus, string? Note);
    public record MeetingDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        Guid OrganizerId, string OrganizerName, string OrganizerNik, string Status, IReadOnlyList<MeetingAttendeeDto> Attendees,
        int AcceptCount, int MaybeCount, int DeclineCount, int PendingCount, string MyResponse,
        string? Agenda = null, string? RoomName = null);
    public record CalendarEventDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string Type, Guid? MeetingId, Guid? RoomId);
}
