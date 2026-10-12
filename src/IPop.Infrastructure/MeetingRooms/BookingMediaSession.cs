using IPop.Infrastructure.Calls;
using IPop.Infrastructure.Hall;
using IPop.Infrastructure.Persistence;

namespace IPop.Infrastructure.MeetingRooms;

/// Resolve before and after the listing: occupancy belongs to the booking media session, not the physical room.
public static class BookingMediaSession
{
    public static async Task<(Booking? Booking, IReadOnlyList<LiveKitParticipant> People)> ObserveAsync(
        IPopDbContext db, ILiveKitRoomAdmin liveKit, RoomOccupancyTracker tracker, MeetingRoomMedia media,
        string key, DateTime now, CancellationToken ct)
    {
        var current = (await RoomAccess.BookingsAsync(db, key, now, tracker.Snapshot(key, now), ct)).Current;
        if (current is null) return (null, []);
        try
        {
            var people = (await liveKit.ListAsync(media.Room(key, current.MeetingId), ct))
                .Where(p => !p.Identity.StartsWith("EG_", StringComparison.Ordinal)).ToList();
            var seen = DateTime.UtcNow > now && DateTime.UtcNow - now < TimeSpan.FromMinutes(1) ? DateTime.UtcNow : now;
            var occupied = tracker.Observe(key, people.Count > 0, seen);
            var resolved = (await RoomAccess.BookingsAsync(db, key, seen, occupied, ct)).Current;
            return resolved?.MeetingId == current.MeetingId ? (resolved, people) : (resolved, []);
        }
        catch { tracker.ObserveFailed(key); throw; }
    }
}
