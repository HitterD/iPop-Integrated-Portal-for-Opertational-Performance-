using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Chat;

/// <summary>
/// Cleans orphan ChatAttachment rows whose MessageId is null and never got linked
/// (client uploaded but never sent). Runs hourly, deletes rows older than 24h.
/// ponytail: also delete Synology files when IFileAccessService supports DeleteAsync.
/// </summary>
public sealed class ChatAttachmentGcWorker(IServiceScopeFactory scopes, ILogger<ChatAttachmentGcWorker> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            // First run shortly after startup so dev DBs clean quickly
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                    var cutoff = DateTime.UtcNow - OrphanAge;
                    var orphans = await db.ChatAttachments
                        .Where(a => a.MessageId == null && a.CreatedAt < cutoff)
                        .ToListAsync(stoppingToken);
                    if (orphans.Count > 0)
                    {
                        db.ChatAttachments.RemoveRange(orphans);
                        await db.SaveChangesAsync(stoppingToken);
                        log.LogInformation("ChatAttachment GC: removed {Count} orphans older than {Cutoff}", orphans.Count, cutoff);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "ChatAttachment GC failed; will retry next interval");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
    }
}
