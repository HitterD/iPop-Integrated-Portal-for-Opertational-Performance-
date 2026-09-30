using IPop.Api.Presence;
using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresence(this IEndpointRouteBuilder app)
    {
        // Heartbeat: sent by every open iPop tab (visible or not) every 30s and whenever the
        // computer's idle state flips. Stamps LastSeenAt server-side (never trust the client's
        // clock), records idle, then broadcasts only if the effective status actually changed.
        app.MapPost("/api/presence/beat", async (
            BeatRequest? req, IPopDbContext db, PresenceBroadcaster presence, HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (nik is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var idle = req?.Idle == true;
            var manual = await db.Users.AsNoTracking()
                .Where(u => u.Nik == nik)
                .Select(u => u.PresenceManualStatus)
                .FirstOrDefaultAsync(ct);
            await db.Users.Where(u => u.Nik == nik)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PresenceLastSeenAt, now)
                    .SetProperty(u => u.PresenceIsIdle, idle), ct);

            var status = PresenceStatus.ComputeStatus(now, manual, now, idle);
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status, ct);
            return Results.Ok(new { status });
        }).RequireAuthorization();

        // Manual override: "busy" or null (back to automatic). Anything else is rejected —
        // "on"/"away"/"off" are derived, never set by hand.
        app.MapPost("/api/presence/status", async (
            SetStatusRequest req, IPopDbContext db, PresenceBroadcaster presence,
            HttpContext http, CancellationToken ct) =>
        {
            if (req.Status is not null and not "busy")
                return Results.BadRequest(new { code = "invalid_status", message = "Status must be \"busy\" or null." });

            var nik = http.User.FindFirst("nik")?.Value;
            if (nik is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var before = await db.Users.AsNoTracking()
                .Where(u => u.Nik == nik)
                .Select(u => new { u.PresenceLastSeenAt, u.PresenceIsIdle })
                .FirstAsync(ct);
            await db.Users.Where(u => u.Nik == nik)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PresenceManualStatus, req.Status), ct);

            var status = PresenceStatus.ComputeStatus(before.PresenceLastSeenAt, req.Status, now, before.PresenceIsIdle);
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status, ct);
            return Results.Ok(new { status });
        }).RequireAuthorization();

        return app;
    }

    public record SetStatusRequest(string? Status);
    public record BeatRequest(bool Idle);
}
