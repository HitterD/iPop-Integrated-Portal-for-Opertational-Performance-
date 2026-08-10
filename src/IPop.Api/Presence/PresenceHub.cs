using Microsoft.AspNetCore.SignalR;

namespace IPop.Api.Presence;

/// Thin broadcast shell — the transport is a detail (spec: hub content is not asserted).
/// All sends go through PresenceBroadcaster so endpoints/workers never touch Hub types.
public class PresenceHub : Microsoft.AspNetCore.SignalR.Hub;

public class PresenceBroadcaster(Microsoft.AspNetCore.SignalR.IHubContext<PresenceHub> hub)
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
        hub.Clients.All.SendAsync("presenceChanged", nik, status, ct);
}
