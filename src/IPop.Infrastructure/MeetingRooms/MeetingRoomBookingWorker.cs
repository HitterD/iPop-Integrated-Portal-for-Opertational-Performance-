using IPop.Application.Hall;
using IPop.Application.MeetingRooms;
using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
/// Then it deletes the in-meeting chat of bookings that ended over an hour ago and no longer hold an occupied room,
/// except lines saved with a server recording (they go with the recording).
/// After EndAt with no next booking the room stays current while it stays occupied (at most 4 h); once it is seen empty
/// (<see cref="RoomOccupancyTracker"/>), RoomAccess has no current booking and nobody can rejoin, even with a valid token.
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
    /// one-minute warning or the takeover itself), or null. A failure in one room (LiveKit, database, anything) is
    /// logged and the other rooms still get their pass.
    public static async Task<DateTime?> TickAsync(IServiceProvider sp, DateTime nowUtc, CancellationToken ct, ILogger? logger = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var liveKit = scope.ServiceProvider.GetRequiredService<ILiveKitRoomAdmin>();
        var tracker = scope.ServiceProvider.GetRequiredService<RoomOccupancyTracker>();
        var media = scope.ServiceProvider.GetService<MeetingRoomMedia>() ?? new MeetingRoomMedia(scope.ServiceProvider.GetService<Microsoft.Extensions.Configuration.IConfiguration>());
        if (!media.BookingWorkerEnabled || !liveKit.IsConfigured) return null;
        var egress = scope.ServiceProvider.GetService<ILiveKitEgress>();
        List<string> mediaRooms;
        try { mediaRooms = (await liveKit.ListRoomsAsync(ct)).Where(x => media.Parse(x) is not null).ToList(); }
        catch
        {
            foreach (var (key, _) in HallBooking.Rooms) tracker.ObserveFailed(key);
            throw;
        }
        DateTime? due = null;
        var held = new HashSet<Guid>();          // bookings still running in an occupied room
        var unknown = new HashSet<string>();     // rooms whose pass failed: their chat is left for a later pass
        // nowUtc is the tick's start; each room is dated by when its own LiveKit listing returned.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (key, _) in HallBooking.Rooms)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RoomAsync(db, liveKit, tracker, media, egress, mediaRooms, key, () => nowUtc + clock.Elapsed, held, ct) is { } d && (due is null || d < due)) due = d;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger?.LogWarning(e, "Meeting room {Room}: booking pass failed", key);
                db.ChangeTracker.Clear();
                unknown.Add(key);
                tracker.ObserveFailed(key);
            }
        }
        try { await CleanupChatAsync(db, nowUtc, held, unknown, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { logger?.LogWarning(e, "Meeting room chat cleanup failed"); }
        return due;
    }

    private static async Task<DateTime?> RoomAsync(IPopDbContext db, ILiveKitRoomAdmin liveKit, RoomOccupancyTracker tracker,
        MeetingRoomMedia media, ILiveKitEgress? egress, IReadOnlyList<string> mediaRooms, string key,
        Func<DateTime> clock, HashSet<Guid> held, CancellationToken ct)
    {
        var session = await BookingMediaSession.ObserveAsync(db, liveKit, tracker, media, key, clock(), ct);
        var nowUtc = clock();
        var occupancy = session.Booking is null ? RoomOccupancy.Empty : tracker.Observe(key, session.People.Count > 0, nowUtc);
        var (current, next) = await RoomAccess.BookingsAsync(db, key, nowUtc, occupancy, ct);
        var boundaryOk = await RecordingBoundary.EndPreviousAsync(db, egress, media, key, current?.MeetingId, nowUtc, ct);
        // Old tokens may reconnect only to their old session. Evict them there, never admit them into the successor.
        foreach (var stale in mediaRooms.Where(r => media.Parse(r) is { } p && p.Key == key && p.MeetingId != current?.MeetingId))
            foreach (var p in await liveKit.ListAsync(stale, ct))
                if (!p.Identity.StartsWith("EG_", StringComparison.Ordinal)) await liveKit.RemoveAsync(stale, p.Identity, ct);
        if (current is null) return NextDue(next, nowUtc);
        var lkRoom = media.Room(key, current.MeetingId);
        var inside = session.People.Select(p => p.Identity).ToList();
        if (inside.Count == 0) return NextDue(next, nowUtc);
        if (current is not null) held.Add(current.MeetingId);

        var stay = new List<string>();
        foreach (var identity in inside)
        {
            var allowed = boundaryOk && current is not null && Guid.TryParse(identity, out var uid)
                && RoomAccess.MayEnter(await RoomAccess.RoleAsync(db, current, uid, ct));
            if (allowed) { stay.Add(identity); continue; }
            try { await liveKit.RemoveAsync(lkRoom, identity, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { stay.Add(identity); /* next pass */ }
        }
        if (stay.Count > 0)
        {
            var json = await RoomMetadata.ComposeAsync(db, key, nowUtc, stay, occupancy, ct);
            if (tracker.MetadataDue(key, json, nowUtc))
            {
                await liveKit.UpdateRoomMetadataAsync(lkRoom, json, ct);
                tracker.MetadataWritten(key, json, nowUtc);
            }
        }
        return NextDue(next, nowUtc);
    }

    /// How long a booking's chat outlives its end (once nobody of that booking is in the room any more).
    public static readonly TimeSpan ChatKeep = TimeSpan.FromHours(1);

    /// Deletes the chat of bookings that ended more than <see cref="ChatKeep"/> ago (or no longer exist) and are not
    /// <paramref name="held"/> (running in an occupied room), skipping rooms whose state is <paramref name="unknown"/>.
    /// Lines with a RecordingId stay: they are deleted with the recording.
    private static async Task CleanupChatAsync(IPopDbContext db, DateTime nowUtc, HashSet<Guid> held, HashSet<string> unknown,
        CancellationToken ct)
    {
        var cutoff = nowUtc - ChatKeep;
        var candidates = await db.MeetingChatMessages.AsNoTracking()
            .Where(m => m.RecordingId == null && !db.Meetings.Any(x => x.Id == m.MeetingId && x.EndAt >= cutoff))
            .Select(m => new { m.MeetingId, m.RoomKey }).Distinct().ToListAsync(ct);
        var ids = candidates.Where(c => !held.Contains(c.MeetingId) && !unknown.Contains(c.RoomKey)).Select(c => c.MeetingId).Distinct().ToList();
        if (ids.Count == 0) return;
        await db.MeetingChatMessages.Where(m => m.RecordingId == null && ids.Contains(m.MeetingId)).ExecuteDeleteAsync(ct);
    }

    private static DateTime? NextDue(Booking? next, DateTime nowUtc)
    {
        if (next is null) return null;
        var takeover = RoomAccess.WindowStart(next);
        var warn = takeover - RoomMetadata.TakeoverWarning;
        return warn > nowUtc ? warn : takeover > nowUtc ? takeover : null;
    }
}
