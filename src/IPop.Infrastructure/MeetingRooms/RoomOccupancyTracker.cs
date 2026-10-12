using System.Collections.Concurrent;
using IPop.Infrastructure.Hall;

namespace IPop.Infrastructure.MeetingRooms;

/// In-memory memory of the hall rooms (singleton):
/// <list type="bullet">
/// <item>when each room was last seen empty: an overrunning booking keeps the room only while it has stayed occupied
/// since its EndAt (RoomAccess.Overruns). Unknown rooms count as empty at <see cref="StartedAtUtc"/> (app start), the
/// conservative choice: after a restart a booking that already ended does not come back.</item>
/// <item>the metadata JSON last written to each LiveKit room, so the booking worker skips writes that change nothing.</item>
/// </list>
/// Per process; lost on restart (by design, see above).
public sealed class RoomOccupancyTracker(DateTime startedAtUtc)
{
    public RoomOccupancyTracker() : this(DateTime.UtcNow) { }

    public DateTime StartedAtUtc { get; } = startedAtUtc;
    private readonly ConcurrentDictionary<string, DateTime> _lastEmpty = new();
    private readonly ConcurrentDictionary<string, string> _lastMetadata = new();

    /// Records what a caller just saw in LiveKit and returns the occupancy to pass to RoomAccess.
    public RoomOccupancy Observe(string roomKey, bool occupied, DateTime nowUtc)
    {
        if (!occupied)
        {
            _lastEmpty.AddOrUpdate(roomKey, nowUtc, (_, old) => old > nowUtc ? old : nowUtc);
            // An empty LiveKit room is deleted after its empty timeout and comes back with blank metadata.
            _lastMetadata.TryRemove(roomKey, out _);
            return RoomOccupancy.Empty;
        }
        return new RoomOccupancy(true, _lastEmpty.GetValueOrDefault(roomKey, StartedAtUtc));
    }

    /// The last time the room was seen empty (app start when never).
    public DateTime LastEmptyAt(string roomKey) => _lastEmpty.GetValueOrDefault(roomKey, StartedAtUtc);

    /// True when <paramref name="json"/> differs from what was last written to the room (then write it).
    public bool MetadataChanged(string roomKey, string json) =>
        !_lastMetadata.TryGetValue(roomKey, out var last) || last != json;

    public void MetadataWritten(string roomKey, string json) => _lastMetadata[roomKey] = json;

    /// The write failed or its outcome is unknown: the next pass writes again.
    public void ForgetMetadata(string roomKey) => _lastMetadata.TryRemove(roomKey, out _);
}
