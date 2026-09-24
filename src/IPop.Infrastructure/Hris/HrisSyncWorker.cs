using IPop.Application.Hris;
using IPop.Application.Realtime;
using IPop.Application.Settings;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Hris;

/// Runs the HRIS pull on the admin-configured schedule (hris.autoSync.cron, see
/// HrisSchedule). Checks once a minute, so a schedule change applies without a restart.
/// A failed run is retried at the next slot — the cursor only advances on success.
public class HrisSyncWorker(IServiceScopeFactory scopes, ILogger<HrisSyncWorker> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CheckAsync(stoppingToken);
            using var timer = new PeriodicTimer(Tick);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckAsync(stoppingToken);
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

    private async Task CheckAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var cron = await sp.GetRequiredService<ISettings>()
                .GetAsync(SettingKeys.HrisAutoSyncCron, (string)SettingKeys.Defaults[SettingKeys.HrisAutoSyncCron], ct);
            var lastStart = await sp.GetRequiredService<IPopDbContext>().HrisSyncRuns
                .MaxAsync(r => (DateTime?)r.StartedAt, ct);
            if (!HrisSchedule.IsDue(cron, DateTime.Now, lastStart?.ToLocalTime())) return;

            var result = await sp.GetRequiredService<IHrisSyncService>().RunAsync(null, ct);
            if (result.Inserted > 0 || result.Updated > 0 || result.Deactivated > 0)
                await sp.GetRequiredService<IDirectoryChangeNotifier>().NotifyAsync(ct);
            log.LogInformation("HRIS sync: {Inserted} inserted, {Updated} updated, {Deactivated} deactivated",
                result.Inserted, result.Updated, result.Deactivated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is stopping
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "HRIS sync failed; will retry at the next scheduled slot");
        }
    }
}
