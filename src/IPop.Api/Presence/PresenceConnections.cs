using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Presence;

/// Counts open /hubs/app connections per user so closing the last iPop tab turns the user
/// offline within ~a minute instead of waiting for the heartbeat to age out. A grace period
/// covers page refreshes and brief network blips, so those never flash "off".
public class PresenceConnections(
    IServiceScopeFactory scopes, PresenceBroadcaster presence, ILogger<PresenceConnections> log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(45);

    private readonly object gate = new();
    private readonly Dictionary<string, int> open = [];

    public void Connected(string nik)
    {
        lock (gate) open[nik] = open.GetValueOrDefault(nik) + 1;
    }

    public void Disconnected(string nik)
    {
        lock (gate)
        {
            var left = open.GetValueOrDefault(nik) - 1;
            if (left > 0) { open[nik] = left; return; }
            open.Remove(nik);
        }
        _ = GoOfflineAfterGraceAsync(nik);
    }

    public bool IsConnected(string nik)
    {
        lock (gate) return open.ContainsKey(nik);
    }

    private async Task GoOfflineAfterGraceAsync(string nik)
    {
        try
        {
            await Task.Delay(Grace);
            if (IsConnected(nik)) return; // came back (refresh, reconnect)

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
            // Age the heartbeat past the offline threshold so the status reads off now; the real
            // last-seen time lives in PresenceLastBeatAt. The next beat restores it.
            var cutoff = DateTime.UtcNow - PresenceStatus.OfflineAfter;
            await db.Users.Where(u => u.Nik == nik && u.PresenceLastSeenAt > cutoff)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PresenceLastSeenAt, cutoff));

            var manual = await db.Users.AsNoTracking().Where(u => u.Nik == nik)
                .Select(u => u.PresenceManualStatus).FirstOrDefaultAsync();
            var status = PresenceStatus.ComputeStatus(cutoff, manual, DateTime.UtcNow);
            if (presence.Track(nik, status)) await presence.BroadcastAsync(nik, status);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Presence disconnect check failed for {Nik}; the sweep will catch it.", nik);
        }
    }
}
