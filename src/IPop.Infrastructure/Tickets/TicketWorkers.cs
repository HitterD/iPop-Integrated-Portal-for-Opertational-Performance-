using IPop.Application.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Tickets;

/// Resends SENDING tickets every minute (each row keeps its own backoff in NextAttemptAt) and turns
/// them FAILED 24 h after creation. The logic lives in TicketService.ProcessOutboxAsync.
public sealed class TicketOutboxWorker(IServiceScopeFactory scopes, ILogger<TicketOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITicketService>().ProcessOutboxAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "TicketOutboxWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

/// Every 15 minutes re-reads open tickets from iDesk so a missed webhook can't leave the mirror
/// stale. Silent: realtime refresh only, no notifications.
public sealed class TicketReconcileWorker(IServiceScopeFactory scopes, ILogger<TicketReconcileWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup settle before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITicketService>().ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "TicketReconcileWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
