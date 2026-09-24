using IPop.Application.Realtime;

namespace IPop.Api.Presence;

/// All presence sends go through here so endpoints/workers never touch hub types.
public class PresenceBroadcaster(IRealtime realtime)
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> known = [];

    /// Tracks the last broadcast state. The sweep passes announceFirst=false so
    /// its initial snapshot is silent; request-driven changes announce immediately.
    public bool Track(string nik, string status, bool announceFirst = true)
    {
        lock (gate)
        {
            if (known.TryGetValue(nik, out var previous) && previous == status) return false;
            var first = !known.ContainsKey(nik);
            known[nik] = status;
            return !first || announceFirst;
        }
    }

    public Task BroadcastAsync(string nik, string status, CancellationToken ct = default) =>
        realtime.ToAllAsync(RealtimeEvents.PresenceChanged, new RealtimeArgs(nik, status), ct);
}
