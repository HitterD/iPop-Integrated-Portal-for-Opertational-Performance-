using IPop.Core.Presence;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Presence;

/// Away/offline are derived from heartbeat age, so a user who stops beating changes
/// status without any request firing. This sweep recomputes effective statuses and
/// broadcasts only actual changes (spec: same worker pattern as HrisSyncWorker).
public class PresenceSweepWorker(
    IServiceScopeFactory scopes, PresenceBroadcaster presence, ILogger<PresenceSweepWorker> log)
    : BackgroundService
{
    /// Well under OfflineAfter so a silent user (computer asleep, network gone) turns "off"
    /// within ~2.5 minutes; closed tabs are faster via PresenceConnections.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                var now = DateTime.UtcNow;

                var users = await db.Users.AsNoTracking()
                    .Where(u => u.IsActive)
                    .Select(u => new { u.Nik, u.PresenceLastSeenAt, u.PresenceManualStatus, u.PresenceIsIdle })
                    .ToListAsync(stoppingToken);

                foreach (var u in users)
                {
                    var status = PresenceStatus.ComputeStatus(u.PresenceLastSeenAt, u.PresenceManualStatus, now, u.PresenceIsIdle);
                    if (presence.Track(u.Nik, status, announceFirst: false))
                        await presence.BroadcastAsync(u.Nik, status, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Presence sweep failed; will retry next interval.");
            }
        }
    }
}
