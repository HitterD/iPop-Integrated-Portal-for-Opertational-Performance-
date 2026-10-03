using IPop.Core.Presence;

namespace IPop.Api.Presence;

/// One lease per open iPop tab, renewed by its heartbeat. A user is idle (away) only when
/// every tab still beating reports idle — an active second computer keeps them "on".
/// In memory: after an API restart the leases come back within one heartbeat interval.
public class PresenceDevices
{
    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, (bool Idle, DateTime At)>> leases =
        new(StringComparer.OrdinalIgnoreCase);

    /// Records this device's beat and returns whether the user counts as idle overall.
    public bool Beat(string nik, string? device, bool idle, DateTime now)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(nik, out var mine)) leases[nik] = mine = [];
            // Older clients send no device id: they share one lease, as before.
            mine[string.IsNullOrWhiteSpace(device) ? "-" : device] = (idle, now);
            return AllIdle(mine, now);
        }
    }

    /// Drops a device (sign-out). Returns the remaining idle state, or null when no device
    /// is still beating — the user is offline.
    public bool? Leave(string nik, string? device, DateTime now)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(nik, out var mine)) return null;
            mine.Remove(string.IsNullOrWhiteSpace(device) ? "-" : device);
            Prune(mine, now);
            if (mine.Count == 0) { leases.Remove(nik); return null; }
            return AllIdle(mine, now);
        }
    }

    private static bool AllIdle(Dictionary<string, (bool Idle, DateTime At)> mine, DateTime now)
    {
        Prune(mine, now);
        return mine.Count > 0 && mine.Values.All(l => l.Idle);
    }

    private static void Prune(Dictionary<string, (bool Idle, DateTime At)> mine, DateTime now)
    {
        foreach (var k in mine.Where(kv => now - kv.Value.At >= PresenceStatus.OfflineAfter).Select(kv => kv.Key).ToList())
            mine.Remove(k);
    }
}
