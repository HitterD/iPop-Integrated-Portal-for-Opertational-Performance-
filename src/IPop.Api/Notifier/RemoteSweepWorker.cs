using IPop.Application.Settings;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Notifier;

/// Remote help housekeeping (BIGIS #8 v3), every minute:
///   • "asking" sessions past their answer time (+5 s) → "expired" (the viewer may have closed before the answer)
///   • "active" sessions without a heartbeat for the alive window → "ended" by timeout
/// and once a day: session history and Admin change entries older than the policy's retention are deleted.
public sealed class RemoteSweepWorker(IServiceScopeFactory scopes, ILogger<RemoteSweepWorker> log) : BackgroundService
{
    private DateTime lastPurge = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                var now = DateTime.UtcNow;
                await SweepAsync(db, now, ct);
                if (now - lastPurge > TimeSpan.FromHours(23))
                {
                    var p = await RemotePolicyStore.GetAsync(scope.ServiceProvider.GetRequiredService<ISettings>(), ct);
                    var n = await PurgeAsync(db, p.RetentionDays, now, ct);
                    if (n > 0) log.LogInformation("Remote help: {Count} history rows older than {Days} days removed.", n, p.RetentionDays);
                    lastPurge = now;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Remote help sweep failed."); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// Expires unanswered prompts and closes silent sessions. Returns how many rows changed.
    public static async Task<int> SweepAsync(IPopDbContext db, DateTime now, CancellationToken ct)
    {
        var asking = await db.RemoteSessions.Where(s => s.Status == "asking").ToListAsync(ct);
        var n = 0;
        foreach (var s in asking.Where(s => now > s.RequestedAt + Endpoints.RemoteEndpoints.AskWindow(s) + TimeSpan.FromSeconds(5)))
        { s.Status = "expired"; s.EndedAt = now; s.EndedBy = "timeout"; n++; }
        var stale = await db.RemoteSessions
            .Where(s => s.Status == "active" && (s.AliveAt ?? s.StartedAt) <= now - Endpoints.RemoteEndpoints.AliveWindow).ToListAsync(ct);
        foreach (var s in stale) { s.Status = "ended"; s.EndedAt = s.AliveAt ?? s.StartedAt ?? now; s.EndedBy = "timeout"; n++; }
        if (n > 0) await db.SaveChangesAsync(ct);
        return n;
    }

    /// Deletes finished sessions and Admin change entries older than <paramref name="days"/>.
    public static async Task<int> PurgeAsync(IPopDbContext db, int days, DateTime now, CancellationToken ct)
    {
        var before = now.AddDays(-days);
        var a = await db.RemoteSessions.Where(s => s.Status != "active" && s.Status != "asking" && s.RequestedAt < before).ExecuteDeleteAsync(ct);
        var b = await db.Set<RemoteAuditEntry>().Where(x => x.At < before).ExecuteDeleteAsync(ct);
        return a + b;
    }
}
