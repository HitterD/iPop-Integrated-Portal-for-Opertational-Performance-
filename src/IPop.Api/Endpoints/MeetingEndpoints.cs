using IPop.Api.Rbac;
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
                TimeZone = req.TimeZone, Location = req.Location, Link = req.Link,
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

        return app;
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
            m.Attendees.FirstOrDefault(a => a.UserId == viewerId)?.RsvpStatus ?? "pending");
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
        IReadOnlyList<string>? AttendeeNiks = null, Guid? SourceMailId = null);
    public record RsvpRequest(string? Response, string? Note);
    public record CreateCalendarEventRequest(string? Title, DateTime StartAt, DateTime EndAt, string? Type, Guid? RoomId);
    public record MeetingAttendeeDto(Guid UserId, string Name, string Nik, bool Required, string RsvpStatus, string? Note);
    public record MeetingDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string? TimeZone, string? Location, string? Link,
        Guid OrganizerId, string OrganizerName, string OrganizerNik, string Status, IReadOnlyList<MeetingAttendeeDto> Attendees,
        int AcceptCount, int MaybeCount, int DeclineCount, int PendingCount, string MyResponse);
    public record CalendarEventDto(Guid Id, string Title, DateTime StartAt, DateTime EndAt, string Type, Guid? MeetingId, Guid? RoomId);
}
