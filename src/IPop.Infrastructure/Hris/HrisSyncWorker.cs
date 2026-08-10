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
        await RunOnce(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnce(stoppingToken);
    }

    private async Task RunOnce(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IHrisSyncService>().RunAsync(ct);
            if (result.Inserted > 0 || result.Updated > 0 || result.Deactivated > 0)
                await scope.ServiceProvider.GetRequiredService<IDirectoryChangeNotifier>().NotifyAsync(ct);
            log.LogInformation("HRIS sync: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated",
                result.Inserted, result.Updated, result.Deactivated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "HRIS sync failed; will retry next tick");
        }
    }
}
