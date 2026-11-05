using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications;

/// <summary>
/// Background worker to purge expired notifications and deliveries (v6 retention requirement).
/// Purges read/acknowledged and failed deliveries older than 90 days, along with any
/// parent requests that no longer have deliveries.
/// </summary>
public class NotificationRetentionWorker(
    IServiceScopeFactory scopes,
    ILogger<NotificationRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public const int SessionRetentionDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunPurgeAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunPurgeAsync(stoppingToken);   // never throws: a failed run is logged and retried next day
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("NotificationRetentionWorker stopped gracefully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in NotificationRetentionWorker.");
        }
    }

    public async Task<int> RunPurgeAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();

            var cutoff = DateTime.UtcNow.AddDays(-90);

            // Delete deliveries older than the cutoff: read/acknowledged/failed ones, and push deliveries that were delivered but
            // never opened (mostly broadcasts; 107k of 112k rows on prod were these and nothing ever removed them).
            var deletedDeliveries = await db.NotificationDeliveries
                .Where(d => (d.Status == "read" || d.Status == "acknowledged" || d.Status == "failed" || (d.Status == "delivered" && d.Channel == "push"))
                            && (d.DeliveredAt != null ? d.DeliveredAt < cutoff : d.UpdatedAt < cutoff))
                .ExecuteDeleteAsync(ct);

            // Sign-in sessions: every refresh rotates into a new row and nothing removed the old ones (64,749 rows on
            // prod, 63,248 revoked), yet the access-token guard queries this table on every request. Rows revoked or
            // expired more than 30 days ago are no longer needed (Admin "last sign-in" uses the newest row per user).
            var sessionCutoff = DateTime.UtcNow.AddDays(-SessionRetentionDays);
            var deletedSessions = await db.AuthSessions
                .Where(s => (s.RevokedAt != null && s.RevokedAt < sessionCutoff) || s.ExpiresAt < sessionCutoff)
                .Where(s => db.AuthSessions.Any(n => n.UserId == s.UserId && n.CreatedAt > s.CreatedAt))
                .ExecuteDeleteAsync(ct);

            // Delete orphan requests that have no remaining deliveries and are older than cutoff
            var deletedRequests = await db.NotificationRequests
                .Where(r => r.AcceptedAt < cutoff && !db.NotificationDeliveries.Any(d => d.RequestId == r.Id))
                .ExecuteDeleteAsync(ct);

            if (deletedDeliveries > 0 || deletedRequests > 0 || deletedSessions > 0)
            {
                logger.LogInformation("Retention purge completed: {Deliveries} deliveries, {Requests} requests, {Sessions} sessions deleted.",
                    deletedDeliveries, deletedRequests, deletedSessions);
            }

            return deletedDeliveries + deletedRequests + deletedSessions;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification retention purge failed.");
            return 0;
        }
    }
}
