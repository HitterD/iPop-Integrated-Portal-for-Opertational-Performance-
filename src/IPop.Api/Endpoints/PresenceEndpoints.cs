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
            BeatRequest? req, IPopDbContext db, PresenceBroadcaster presence, PresenceDevices devices,
            HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (nik is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            // Away only when every open device is idle: one active computer keeps the user on.
            var idle = devices.Beat(nik, req?.Device, req?.Idle == true, now);
            var row = await db.Users.AsNoTracking()
                .Where(u => u.Nik == nik)
                .Select(u => new { u.PresenceManualStatus, u.PresenceBusyUntilUtc })
                .FirstOrDefaultAsync(ct);
            var manual = PresenceStatus.EffectiveManual(row?.PresenceManualStatus, row?.PresenceBusyUntilUtc, now);
            await db.Users.Where(u => u.Nik == nik)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PresenceLastSeenAt, now)
                    .SetProperty(u => u.PresenceLastBeatAt, now)
                    .SetProperty(u => u.PresenceIsIdle, idle), ct);

            var status = PresenceStatus.ComputeStatus(now, manual, now, idle);
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status, ct);
            return Results.Ok(new { status, busyUntil = manual == "busy" ? row?.PresenceBusyUntilUtc : null });
        }).RequireAuthorization();

        // Sign-out from one device: drop its lease now instead of waiting out the heartbeat. If no
        // other device is still beating, the user is offline immediately (last seen = last beat).
        app.MapPost("/api/presence/leave", async (
            BeatRequest? req, IPopDbContext db, PresenceBroadcaster presence, PresenceDevices devices,
            HttpContext http, CancellationToken ct) =>
        {
            var nik = http.User.FindFirst("nik")?.Value;
            if (nik is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;
            var remaining = devices.Leave(nik, req?.Device, now);
            string status;
            if (remaining is { } idle)
            {
                await db.Users.Where(u => u.Nik == nik)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.PresenceIsIdle, idle), ct);
                var row = await db.Users.AsNoTracking().Where(u => u.Nik == nik)
                    .Select(u => new { u.PresenceLastSeenAt, u.PresenceManualStatus, u.PresenceBusyUntilUtc }).FirstAsync(ct);
                status = PresenceStatus.ComputeStatus(row.PresenceLastSeenAt,
                    PresenceStatus.EffectiveManual(row.PresenceManualStatus, row.PresenceBusyUntilUtc, now), now, idle);
            }
            else
            {
                var cutoff = now - PresenceStatus.OfflineAfter;
                await db.Users.Where(u => u.Nik == nik && u.PresenceLastSeenAt > cutoff)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.PresenceLastSeenAt, cutoff), ct);
                status = "off";
            }
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status, ct);
            return Results.Ok(new { status });
        }).RequireAuthorization();

        // Manual override: "busy" or null (back to automatic). Anything else is rejected —
        // "on"/"away"/"off" are derived, never set by hand. Busy takes an optional duration
        // ("1h", "2h", "today"; none = until turned off); the end is computed here, never trusted
        // from the client, and the sweep returns the user to automatic once it passes.
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
                .Select(u => new { u.PresenceLastSeenAt, u.PresenceIsIdle, u.TimeZoneId })
                .FirstAsync(ct);
            DateTime? until = null;
            if (req.Status == "busy"
                && !PresenceStatus.TryBusyUntil(req.Duration, now, UserTimeZone(before.TimeZoneId), out until))
                return Results.BadRequest(new { code = "invalid_duration", message = "Duration must be 1h, 2h, today or empty." });
            await db.Users.Where(u => u.Nik == nik)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.PresenceManualStatus, req.Status)
                    .SetProperty(u => u.PresenceBusyUntilUtc, until), ct);

            var status = PresenceStatus.ComputeStatus(before.PresenceLastSeenAt, req.Status, now, before.PresenceIsIdle);
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status, ct);
            return Results.Ok(new { status, busyUntil = until });
        }).RequireAuthorization();

        return app;
    }

    internal static TimeZoneInfo UserTimeZone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id ?? "SE Asia Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
    }

    public record SetStatusRequest(string? Status, string? Duration = null);
    /// Device = a random id per open tab, so idle is tracked per device.
    public record BeatRequest(bool Idle, string? Device = null);
}
