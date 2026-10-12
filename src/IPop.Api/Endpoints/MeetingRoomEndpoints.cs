using IPop.Api.Rbac;
using IPop.Application.Rbac;
using IPop.Application.MeetingRooms;
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

    public static IEndpointRouteBuilder MapMeetingRooms(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/meeting-rooms").RequireAuthorization().RequirePermission("calls.use");

        g.MapGet("", async (HttpContext http, IPopDbContext db, IMeetingRoomService rooms, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await rooms.ListAsync(me, ct)) : Results.Unauthorized());

        g.MapPost("/{key}/join", async (string key, HttpContext http, IPopDbContext db, IMeetingRoomService rooms,
            CancellationToken ct) =>
        {
            if (await UserIdAsync(http, db, ct) is not { } me) return Results.Unauthorized();
            return JoinResponse(await rooms.JoinAsync(me, key, ct));
        });

        g.MapPost("/{key}/recordings", async (string key, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StartAsync(me, key, ct)) : Results.Unauthorized());

        g.MapPost("/recordings/{id:guid}/stop", async (Guid id, HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? RecordingResponse(await recs.StopAsync(me, id, ct)) : Results.Unauthorized());

        g.MapGet("/recordings", async (HttpContext http, IPopDbContext db, IMeetingRecordingService recs, CancellationToken ct) =>
            await UserIdAsync(http, db, ct) is { } me ? Results.Ok(await recs.ListAsync(me, false, ct)) : Results.Unauthorized());

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
                recordings = await recs.ListAsync(me, true, ct),
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

        a.MapDelete("/recordings/{id:guid}", async (Guid id, IMeetingRecordingService recs, CancellationToken ct) =>
            await recs.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    public static IResult JoinResponse(MeetingRoomJoinResult r) => r.Error switch
    {
        null => Results.Ok(r.Join),
        "unknown_room" => Results.Json(new { code = r.Error, message = "That room does not exist." }, statusCode: 404),
        "room_full" => Results.Json(new { code = r.Error, message = "This room is full (20 people).", suggestions = r.Suggestions }, statusCode: 409),
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

    private static async Task<Guid?> UserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
