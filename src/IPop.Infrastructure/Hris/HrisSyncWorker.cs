using IPop.Application.Hris;
using IPop.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Hris;

/// Spec §4: full pull on startup, then every 15 minutes. Failures are logged and
/// retried on the next tick — the cursor only advances after a successful run.
public class HrisSyncWorker(IServiceScopeFactory scopes, ILogger<HrisSyncWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnce(stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnce(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
            log.LogInformation("HrisSyncWorker stopped gracefully.");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unexpected error in HrisSyncWorker; stopping worker.");
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IHrisSyncService>().RunAsync(ct);
            if (result.Inserted > 0 || result.Updated > 0 || result.Deactivated > 0)
                await scope.ServiceProvider.GetRequiredService<IDirectoryChangeNotifier>().NotifyAsync(ct);
            log.LogInformation("HRIS sync: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated",
                result.Inserted, result.Updated, result.Deactivated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "HRIS sync failed; will retry next tick");
        }
    }
}
