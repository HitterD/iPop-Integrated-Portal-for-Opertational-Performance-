using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Application.Realtime;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Projects;

/// Hentikan timer yang lupa dimatikan: setelah 10 jam entri dipotong di 10 jam, ditandai
/// AutoStopped, dan pemiliknya diberi notifikasi supaya bisa mengoreksi durasinya.
public sealed class TimerAutoStopWorker(IServiceScopeFactory scopeFactory, ILogger<TimerAutoStopWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Beri waktu migrasi & startup selesai sebelum putaran pertama.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Tick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "TimerAutoStopWorker tick failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IProjectTimeService>();
        var stopped = await svc.AutoStopAsync(DateTime.UtcNow, ct);
        if (stopped.Count == 0) return;
        log.LogInformation("Auto-stopped {Count} timers at the 10-hour cap", stopped.Count);

        var gateway = scope.ServiceProvider.GetService<INotificationGatewayService>();
        var realtime = scope.ServiceProvider.GetService<IRealtime>();
        var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
        var userIds = stopped.Select(s => s.UserId).Distinct().ToList();
        var niks = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Nik, ct);

        foreach (var s in stopped)
        {
            try
            {
                if (gateway is not null)
                    await gateway.PublishInternalAsync(
                        clientSlug: "projects",
                        eventType: "project.timer_auto_stopped",
                        title: $"Timer auto-stopped: {s.TaskTitle}",
                        message: $"The timer for \"{s.TaskTitle}\" ran for more than 10 hours, so 10 hours were logged. Correct the duration on the task if needed.",
                        target: new NotificationTarget(UserId: s.UserId),
                        priority: "normal",
                        targetUrl: $"/projects/{s.ProjectId}",
                        ct: ct);
                if (realtime is not null && niks.TryGetValue(s.UserId, out var nik))
                    await realtime.ToUserAsync(nik, RealtimeEvents.TimerChanged, null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The entry is already capped; a lost notification must not undo that.
                log.LogWarning(ex, "Auto-stop notification failed for entry {EntryId}", s.EntryId);
            }
        }
        if (realtime is not null)
            foreach (var projectId in stopped.Select(s => s.ProjectId).Distinct())
            {
                try { await realtime.ToAllAsync(RealtimeEvents.ProjectChanged, projectId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogDebug(ex, "project push failed"); }
            }
    }
}
