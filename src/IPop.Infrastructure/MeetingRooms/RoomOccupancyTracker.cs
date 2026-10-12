using System.Collections.Concurrent;
using IPop.Infrastructure.Hall;

namespace IPop.Infrastructure.MeetingRooms;

/// In-memory memory of the hall rooms (singleton):
/// <list type="bullet">
/// <item>when each room was last seen empty: an overrunning booking keeps the room only while it has stayed occupied
/// since its EndAt (RoomAccess.Overruns). Unknown rooms count as empty at <see cref="StartedAtUtc"/> (app start), the
/// conservative choice: after a restart a booking that already ended does not come back.</item>
/// <item>the metadata JSON last written to each LiveKit room and when, so writers skip writes that change nothing — but
/// rewrite at least every <see cref="MetadataRefresh"/> (heals a recreated LiveKit room or a lost concurrent write).</item>
/// </list>
/// Per process; lost on restart (by design, see above).
public sealed class RoomOccupancyTracker(DateTime startedAtUtc)
{
    public RoomOccupancyTracker() : this(DateTime.UtcNow) { }

    public static readonly TimeSpan MetadataRefresh = TimeSpan.FromMinutes(5);
    public DateTime StartedAtUtc { get; } = startedAtUtc;
    private readonly ConcurrentDictionary<string, DateTime> _lastEmpty = new();
    private readonly ConcurrentDictionary<string, (string Json, DateTime At)> _lastMetadata = new();

    /// Records what a caller just saw in LiveKit and returns the occupancy to pass to RoomAccess. Pass the time the
    /// LiveKit listing RETURNED (not when the request started): "empty" must never be dated before it was true.
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

    /// True when the room should get <paramref name="json"/>: it differs from what was last written, or that write is
    /// <see cref="MetadataRefresh"/> old.
    public bool MetadataDue(string roomKey, string json, DateTime nowUtc) =>
        !_lastMetadata.TryGetValue(roomKey, out var last) || last.Json != json || nowUtc - last.At >= MetadataRefresh;

    public void MetadataWritten(string roomKey, string json, DateTime nowUtc) => _lastMetadata[roomKey] = (json, nowUtc);

    /// The write failed or its outcome is unknown: the next pass writes again.
    public void ForgetMetadata(string roomKey) => _lastMetadata.TryRemove(roomKey, out _);
}
