using IPop.Application.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Projects;

/// Hapus permanen tugas yang sudah di-soft-delete lebih dari 7 hari. Jendela undo di UI
/// hanya 8 detik (server mengizinkan restore 24 jam); sisanya ditahan sebagai jaring pengaman.
public sealed class DeletedTaskPurgeWorker(IServiceScopeFactory scopeFactory, ILogger<DeletedTaskPurgeWorker> log) : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Beri waktu migrasi & startup selesai sebelum putaran pertama.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IProjectService>();
                var purged = await svc.PurgeDeletedTasksAsync(DateTime.UtcNow - Retention, stoppingToken);
                if (purged > 0) log.LogInformation("Purged {Count} soft-deleted project tasks", purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "DeletedTaskPurgeWorker tick failed");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
