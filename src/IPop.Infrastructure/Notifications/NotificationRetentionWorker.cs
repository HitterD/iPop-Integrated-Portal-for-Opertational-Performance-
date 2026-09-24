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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunPurgeAsync(stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunPurgeAsync(stoppingToken);
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

            // Delete read or acknowledged deliveries older than cutoff
            var deletedDeliveries = await db.NotificationDeliveries
                .Where(d => (d.Status == "read" || d.Status == "acknowledged" || d.Status == "failed")
                            && (d.DeliveredAt != null ? d.DeliveredAt < cutoff : d.UpdatedAt < cutoff))
                .ExecuteDeleteAsync(ct);

            // Delete orphan requests that have no remaining deliveries and are older than cutoff
            var deletedRequests = await db.NotificationRequests
                .Where(r => r.AcceptedAt < cutoff && !db.NotificationDeliveries.Any(d => d.RequestId == r.Id))
                .ExecuteDeleteAsync(ct);

            if (deletedDeliveries > 0 || deletedRequests > 0)
            {
                logger.LogInformation("Notification retention purge completed: {Deliveries} deliveries, {Requests} requests deleted.",
                    deletedDeliveries, deletedRequests);
            }

            return deletedDeliveries + deletedRequests;
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
