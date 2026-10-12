using IPop.Api.Rbac;
using IPop.Application.Rbac;
using IPop.Application.MeetingRooms;
using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Application.Settings;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.MeetingRooms;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// Calls & Meetings → Meeting Rooms: the five SJA Hall rooms (LiveKit "hall-{key}"), server recordings and the admin switches.
public static class MeetingRoomEndpoints
{
    public record VideoRequest(bool Allowed);
    public record RetentionRequest(int Days);
    public record JoinRequest(string? Password);
    public record UseNowRequest(int Minutes, string? Title, IReadOnlyList<string>? AttendeeNiks);
    public record UseNowResponse(Guid MeetingId, MeetingRoomJoinDto? Join);
    public record ExtendResponse(DateTime EndAt);

    public static IEndpointRouteBuilder MapMeetingRooms(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/meeting-rooms").RequireAuthorization().RequirePermission("calls.use");

        g.MapGet("", async (HttpContext http, IPopDbContext db, IMeetingRoomService rooms, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await rooms.ListAsync(me, ct)) : Results.Unauthorized());

        // Body { password? } is optional: invited people (and the v1 client) post nothing.
        g.MapPost("/{key}/join", async (string key, HttpContext http, IPopDbContext db, IMeetingRoomService rooms,
            CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            var req = await OptionalBodyAsync<JoinRequest>(http, ct);
            return JoinResponse(await rooms.JoinAsync(me, key, req?.Password, ct));
        });

        // "Use now": book a free room from this minute (a real meeting: invites, calendar, password) and join it.
        g.MapPost("/{key}/use-now", async (string key, UseNowRequest req, HttpContext http, IPopDbContext db, IMeetingRoomService rooms,
            INotificationGatewayService? notifs, IRealtime realtime, CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            var (meetingId, error, max) = await rooms.UseNowAsync(me, key, req.Minutes, req.Title, req.AttendeeNiks ?? [], ct);
            switch (error)
            {
                case "unknown_room": return Results.Json(new { code = error, message = "That room does not exist." }, statusCode: 404);
                case "invalid_minutes":
                    return Results.Json(new { code = error, message = "Choose 30, 60 or 90 minutes." }, statusCode: 400);
                case not null:
                    return Results.Json(new { code = "room_booked", message = "This room is booked at that time.", maxMinutes = max }, statusCode: 409);
            }
            await MeetingEndpoints.NotifyMeetingChangedAsync(db, realtime, meetingId!.Value, [], ct);
            var invited = await db.MeetingAttendees.AsNoTracking().Where(a => a.MeetingId == meetingId && a.UserId != me).Select(a => a.UserId).ToListAsync(ct);
            if (notifs is not null && invited.Count > 0)
            {
                var title = await db.Meetings.AsNoTracking().Where(m => m.Id == meetingId).Select(m => m.Title).FirstAsync(ct);
                var name = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.Name).FirstAsync(ct);
                var start = await db.Meetings.AsNoTracking().Where(m => m.Id == meetingId).Select(m => m.StartAt).FirstAsync(ct);
                var log = MeetingEndpoints.NotifyLog(http);
                // Same notification and delegate forwarding as an invite from the meeting form (awaited in sequence: one DbContext).
                await MeetingEndpoints.PublishBestEffortAsync(notifs, log, "meeting.invited", $"Meeting invite: {title}",
                    $"{name} invited you to \"{title}\" on {start.ToLocalTime():dd MMM, HH:mm}.", invited, "normal",
                    $"/calls?tab=scheduled&meeting={meetingId}");
                await CalendarDelegateEndpoints.ForwardInvitesAsync(db, notifs, log, invited, title, start, name, null, ct);
            }
            // The booking stands even when joining fails (e.g. media down): the client can join from the card.
            var join = await rooms.JoinAsync(me, key, null, ct);
            return Results.Ok(new UseNowResponse(meetingId.Value, join.Join));
        });

        // "Extend 15 min" (organizer of the current booking): the meeting ends 15 minutes later via the normal update path.
        g.MapPost("/{key}/extend", async (string key, HttpContext http, IPopDbContext db, IMeetingRoomService rooms, IRealtime realtime,
            CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            var (meetingId, endAt, error) = await rooms.ExtendAsync(me, key, ct);
            switch (error)
            {
                case "unknown_room": return Results.Json(new { code = error, message = "That room does not exist." }, statusCode: 404);
                case "no_booking": return Results.Json(new { code = error, message = "This room has no booking right now." }, statusCode: 409);
                case "forbidden": return Results.Json(new { code = error, message = "Only the organizer can extend this booking." }, statusCode: 403);
                case "cancelled": return Results.Json(new { code = error, message = "This booking was cancelled." }, statusCode: 409);
                case "media_unavailable":
                    return Results.Json(new { code = error, message = "The meeting server is not reachable right now." }, statusCode: 503);
                case not null: return Results.Json(new { code = "room_booked", message = "The room is booked right after this meeting." }, statusCode: 409);
            }
            // Calendars and meeting lists of everyone involved follow the new end.
            await MeetingEndpoints.NotifyMeetingChangedAsync(db, realtime, meetingId!.Value, [], ct);
            return Results.Ok(new ExtendResponse(endAt!.Value));
        });

        g.MapPost("/{key}/recordings", async (string key, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StartAsync(me, key, ct)) : Results.Unauthorized());

        g.MapPost("/recordings/{id:guid}/stop", async (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StopAsync(me, id, ct)) : Results.Unauthorized());

        // Only recordings the user was in the room for, Admins included (they audit from Administration).
        g.MapGet("/recordings", async (HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await recs.ListAsync(me, http.User.IsInRole("Admin"), ct)) : Results.Unauthorized());

        // Delete for everyone: who started it, the booking organizer, or an Admin. The NAS file goes right away.
        g.MapDelete("/recordings/{id:guid}", (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            DeleteRecordingAsync(id, http, db, recs, ct));

        // <video src> / <a download> cannot send the Authorization header: the client asks for a ticket scoped to this
        // recording and user (6 h) and uses the URL it returns. No JWT ever goes into a URL.
        g.MapPost("/recordings/{id:guid}/ticket", (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs,
            RecordingTicketSigner tickets, CancellationToken ct) => TicketAsync(id, http, db, recs, tickets, ct));

        // Outside the authorized group: a ticket (?t=) stands in for the header. Without one the normal JWT applies.
        app.MapGet("/api/meeting-rooms/recordings/{id:guid}/file", (Guid id, string? t, string? download, HttpContext http,
            IPopDbContext db, IMeetingRecordingService recs, RecordingTicketSigner tickets, IPermissionEvaluator permissions,
            RecordingFileCache cache, CancellationToken ct) => FileAsync(id, t, download, http, db, recs, tickets, permissions, cache, ct))
            .AllowAnonymous();

        var a = app.MapGroup("/api/admin/meeting-rooms").RequireAuthorization("AdminOnly");

        a.MapGet("", async (IMeetingRoomService rooms, IMeetingRecordingService recs, ISettings settings, ILiveKitEgress egress,
            HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var me = await UserIdAsync(http, db, ct) ?? Guid.Empty;
            string status;
            if (!egress.IsConfigured) status = "not_configured";
            else status = await egress.PingAsync(ct) ? "online" : "offline";   // the Egress service itself, not the room service
            return Results.Ok(new
            {
                video = await rooms.VideoAsync(ct),
                retentionDays = await settings.GetAsync(SettingKeys.MeetingRoomsRetentionDays, 90, ct),
                egress = status,
                recordings = await recs.ListAllAsync(ct),
            });
        });

        a.MapPut("/{key}/video", async (string key, VideoRequest req, IMeetingRoomService rooms, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            if (!await db.HallRooms.AnyAsync(r => r.Key == key, ct)) return Results.NotFound();
            await rooms.SetVideoAsync(key, req.Allowed, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        a.MapPut("/retention", async (RetentionRequest req, ISettings settings, HttpContext http, CancellationToken ct) =>
        {
            if (req.Days is < 1 or > 3650) return Results.BadRequest(new { code = "invalid_days", message = "Use 1 to 3650 days." });
            await settings.SetAsync(SettingKeys.MeetingRoomsRetentionDays, req.Days, http.User.FindFirst("nik")?.Value, ct);
            return Results.NoContent();
        });

        a.MapDelete("/recordings/{id:guid}", (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            DeleteRecordingAsync(id, http, db, recs, ct));

        return app;
    }

    public static IResult JoinResponse(MeetingRoomJoinResult r) => r.Error switch
    {
        null => Results.Ok(r.Join),
        "unknown_room" => Results.Json(new { code = r.Error, message = "That room does not exist." }, statusCode: 404),
        "room_full" => Results.Json(new { code = r.Error, message = "This room is full (20 people).", suggestions = r.Suggestions }, statusCode: 409),
        "no_booking" => Results.Json(new { code = r.Error, message = "Book a room to use it." }, statusCode: 409),
        "password_required" => Results.Json(new { code = r.Error, message = "Enter the 6-digit password from the iPop meeting invite." }, statusCode: 403),
        // Generic: never says whether the booking or the user exists.
        "wrong_password" => Results.Json(new { code = r.Error, message = "That password is not right." }, statusCode: 403),
        "too_many_attempts" => Results.Json(new { code = r.Error, message = "Too many tries. Wait 5 minutes and try again." }, statusCode: 429),
        _ => Results.Json(new { code = "media_unavailable", message = "The meeting server is not reachable right now." }, statusCode: 503),
    };

    public static IResult RecordingResponse(MeetingRecordingResult r) => r.Error switch
    {
        null => Results.Ok(r.Recording),
        "unknown_room" or "not_found" => Results.Json(new { code = r.Error, message = "Not found." }, statusCode: 404),
        "not_in_room" => Results.Json(new { code = r.Error, message = "Join the room first." }, statusCode: 403),
        "already_recording" => Results.Json(new { code = r.Error, message = "This room is already being recorded to the server." }, statusCode: 409),
        _ => Results.Json(new { code = "egress_unavailable", message = "Server recording is not available right now. Try Record to this PC." }, statusCode: 503),
    };

    public record TicketResponse(string Url);

    /// DELETE …/recordings/{id} (user and admin routes): 204; 404 when it is gone or the caller may not even see it;
    /// 403 when the caller sees it but may not delete it; 409 while it is still recording.
    public static async Task<IResult> DeleteRecordingAsync(Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs,
        CancellationToken ct)
    {
        if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
        return await recs.DeleteAsync(me, http.User.IsInRole("Admin"), id, ct) switch
        {
            null => Results.NoContent(),
            "forbidden" => Results.Json(new { code = "forbidden", message = "Only the person who started the recording, the meeting organizer or an admin can delete it." }, statusCode: 403),
            "recording" => Results.Json(new { code = "still_recording", message = "Stop the recording first." }, statusCode: 409),
            _ => Results.Json(new { code = "not_found", message = "This recording was deleted." }, statusCode: 404),
        };
    }

    /// POST …/recordings/{id}/ticket (calls.use): a file URL for this recording and this user, valid 6 h. 404 when the
    /// user may not see it (same check as the list, Admin role included).
    public static async Task<IResult> TicketAsync(Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs,
        RecordingTicketSigner tickets, CancellationToken ct)
    {
        if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
        if (!await recs.CanViewAsync(me, http.User.IsInRole("Admin"), id, ct)) return Results.NotFound();
        return Results.Ok(new TicketResponse($"/api/meeting-rooms/recordings/{id}/file?t={tickets.Create(id, me)}"));
    }

    /// GET …/recordings/{id}/file: a valid ticket (?t=) or the Authorization header; 401 without either, 404 when that
    /// user may not see the recording (or it has no file).
    public static async Task<IResult> FileAsync(Guid id, string? ticket, string? download, HttpContext http, IPopDbContext db,
        IMeetingRecordingService recs, RecordingTicketSigner tickets, IPermissionEvaluator permissions, RecordingFileCache cache,
        CancellationToken ct)
    {
        if (await FileViewerAsync(id, ticket, http, db, tickets, permissions, ct) is not { } v) return Results.Unauthorized();
        if (!await recs.CanViewAsync(v.UserId, v.Admin, id, ct)) return Results.NotFound();
        var row = await db.MeetingRecordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.NotFound();
        if (await cache.OpenAsync(row, ct) is not { } path) return Results.NotFound();
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string($"{row.Title} {row.StartedAt.ToLocalTime():yyyy-MM-dd HHmm}.mp4"
            .Where(c => !invalid.Contains(c)).ToArray());
        return Results.File(path, "video/mp4", download is "1" or "true" ? name : null, enableRangeProcessing: true);
    }

    public readonly record struct FileViewer(Guid UserId, bool Admin);

    /// Who is fetching a recording file: the ticket's user (re-checked: active account, calls.use, Admin role from the
    /// database), or the JWT user when there is no ticket. A ticket that is present but invalid never falls back.
    public static async Task<FileViewer?> FileViewerAsync(Guid recordingId, string? ticket, HttpContext http, IPopDbContext db,
        RecordingTicketSigner tickets, IPermissionEvaluator permissions, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(ticket))
        {
            if (tickets.Verify(recordingId, ticket) is not { } userId) return null;
            var now = DateTime.UtcNow;
            if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive && !u.MustChangePassword
                    && (u.LockedUntil == null || u.LockedUntil <= now), ct)) return null;
            if (!await permissions.IsAllowedAsync(userId, "calls.use", null, ct)) return null;
            var admin = await db.UserRoles.AsNoTracking().AnyAsync(r => r.UserId == userId && r.Role.Name == "Admin", ct);
            return new FileViewer(userId, admin);
        }
        if (http.User.Identity?.IsAuthenticated != true
            || http.User.HasClaim(IPop.Infrastructure.Auth.AccessTokenGuard.PasswordChangeClaim, "1")) return null;
        if (await UserIdAsync(http, db, ct) is not { } me) return null;
        if (!await permissions.IsAllowedAsync(me, "calls.use", null, ct)) return null;
        return new FileViewer(me, http.User.IsInRole("Admin"));
    }

    /// A JSON body when one was sent; null for an empty body (no Content-Type / Content-Length 0) or one that is not JSON.
    private static async Task<T?> OptionalBodyAsync<T>(HttpContext http, CancellationToken ct) where T : class
    {
        if (http.Request.ContentLength == 0 || !http.Request.HasJsonContentType()) return null;
        try { return await http.Request.ReadFromJsonAsync<T>(ct); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
