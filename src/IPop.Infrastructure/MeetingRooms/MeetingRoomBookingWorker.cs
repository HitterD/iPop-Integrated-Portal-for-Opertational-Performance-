using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.MeetingRooms;

/// Meeting rooms v2 §1.5: keeps every hall room inside its booking. Every 30 s (and right at a takeover moment), per room:
/// <list type="number">
/// <item>asks LiveKit who is inside (EG_ recorders ignored);</item>
/// <item>removes everyone RoomAccess does not let in now (no current booking, or "outsider" of the current one). This
/// one rule covers a token reused after the booking (tokens live 10 min), an attendee removed from / declining the
/// booking (Review Focus 1) and the next booking's lock window starting (Review Focus 2: only the people not allowed in
/// the new booking leave; RoomAccess makes the newer booking current exactly at StartAt − 10 min);</item>
/// <item>rewrites the room metadata (<see cref="RoomMetadata"/>): endAt / nextStartAt / takeoverAt and, one minute
/// before a takeover, who will be moved out.</item>
/// </list>
/// After EndAt with no next booking the room stays current while occupied; once empty, RoomAccess has no current
/// booking and nobody can rejoin.
public sealed class MeetingRoomBookingWorker(IServiceProvider services, ILogger<MeetingRoomBookingWorker> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = Interval;
            try
            {
                var now = DateTime.UtcNow;
                var due = await TickAsync(services, now, stoppingToken, log);
                // Wake at the next warning/takeover moment when it comes before the normal tick.
                if (due is { } d && d - now < wait) wait = d - now < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : d - now;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning(e, "Meeting room booking pass failed"); }
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// One pass over the five rooms. Returns the earliest upcoming moment that needs a pass of its own (a takeover's
    /// one-minute warning or the takeover itself), or null.
    public static async Task<DateTime?> TickAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct, ILogger? logger = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var liveKit = scope.ServiceProvider.GetRequiredService<ILiveKitRoomAdmin>();
        if (!liveKit.IsConfigured) return null;
        DateTime? due = null;
        foreach (var (key, _) in HallBooking.Rooms)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RoomAsync(db, liveKit, key, nowUtc, ct) is { } d && (due is null || d < due)) due = d;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException)
            {
                logger?.LogDebug(e, "Meeting room {Room}: LiveKit not reachable", key);
            }
        }
        return due;
    }

    private static async Task<DateTime?> RoomAsync(IPopDbContext db, ILiveKitRoomAdmin liveKit, string key, DateTime nowUtc, CancellationToken ct)
    {
        var lkRoom = MeetingRoomKeys.LiveKitRoom(key);
        var inside = (await liveKit.ListAsync(lkRoom, ct)).Select(p => p.Identity)
            .Where(i => !i.StartsWith("EG_", StringComparison.Ordinal)).ToList();
        if (inside.Count == 0) return NextDue(await RoomAccess.BookingsAsync(db, key, nowUtc, false, ct), nowUtc);

        // occupied = true: an overrunning booking keeps the room until the next booking's window starts.
        var (current, next) = await RoomAccess.BookingsAsync(db, key, nowUtc, true, ct);
        var stay = new List<string>();
        foreach (var identity in inside)
        {
            var allowed = current is not null && Guid.TryParse(identity, out var uid)
                && RoomAccess.MayEnter(await RoomAccess.RoleAsync(db, current, uid, ct));
            if (allowed) { stay.Add(identity); continue; }
            try { await liveKit.RemoveAsync(lkRoom, identity, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { stay.Add(identity); /* next pass */ }
        }
        if (stay.Count > 0)
        {
            var json = await RoomMetadata.ComposeAsync(db, key, nowUtc, stay, ct);
            await liveKit.UpdateRoomMetadataAsync(lkRoom, json, ct);
        }
        return NextDue((current, next), nowUtc);
    }

    private static DateTime? NextDue((Booking? Current, Booking? Next) b, DateTime nowUtc)
    {
        if (b.Next is not { } next) return null;
        var takeover = RoomAccess.WindowStart(next);
        var warn = takeover - RoomMetadata.TakeoverWarning;
        return warn > nowUtc ? warn : takeover > nowUtc ? takeover : null;
    }
}
