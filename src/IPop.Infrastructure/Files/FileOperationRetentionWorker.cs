using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Files;

public class FileOperationRetentionWorker(
    IServiceScopeFactory scopes,
    ILogger<FileOperationRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
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
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in FileOperationRetentionWorker.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }

    public async Task<int> RunPurgeAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var deleted = await db.FileOperations
                .Where(o => o.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                logger.LogInformation("Purged {Count} expired file operation records.", deleted);

            return deleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to purge file operation records.");
            return 0;
        }
    }
}
