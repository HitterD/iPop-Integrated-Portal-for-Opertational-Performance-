using IPop.Application.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.News;

/// <summary>
/// Applies frozen "scheduled" composer drafts whose publish time has arrived.
///
/// <para>The draft autosave never writes the live post, so scheduling is a two-step: the author
/// freezes a complete snapshot with <c>Mode="schedule"</c>, and this worker performs the actual
/// publication when it is due. Permission is re-checked at apply time (the author may have left or
/// lost news.publish), so an unauthorized draft stays frozen for a human instead of publishing.</para>
/// </summary>
public sealed class NewsComposerScheduleWorker(
    IServiceScopeFactory scopes,
    ILogger<NewsComposerScheduleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(stoppingToken);
                using var timer = new PeriodicTimer(Interval);
                while (await timer.WaitForNextTickAsync(stoppingToken))
                    await RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in NewsComposerScheduleWorker.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    /// <summary>Applies every due scheduled draft. Each apply re-checks authorization itself.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return 0;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<INewsComposerService>();
            var now = DateTime.UtcNow;

            // Oldest due first, and skip anything in backoff. A draft that keeps failing is pushed
            // out by NextScheduleAttemptAt so it cannot hold the head of the queue forever and
            // starve younger due drafts.
            var due = await db.NewsComposerDrafts.AsNoTracking()
                .Where(d => d.State == "scheduled" && d.PublishAt != null && d.PublishAt <= now
                            && (d.NextScheduleAttemptAt == null || d.NextScheduleAttemptAt <= now))
                .OrderBy(d => d.PublishAt)
                .Select(d => d.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            var applied = 0;
            foreach (var id in due)
            {
                if (ct.IsCancellationRequested) break;
                // One draft's failure must never stop the rest of the batch.
                try
                {
                    var result = await svc.ApplyScheduledDraftAsync(id, ct);
                    if (result is { Success: true })
                    {
                        applied++;
                    }
                    else
                    {
                        // Not applicable yet (unauthorized, conflict, or no longer due): back it off
                        // so the head of the queue keeps moving. A human can still resume/cancel it.
                        await BackoffAsync(db, id, now, ct);
                    }
                }
                catch (Exception ex)
                {
                    // Never log the draft body — only the id and the failure kind.
                    logger.LogWarning(ex, "Scheduled News draft {DraftId} failed to apply.", id);
                    await BackoffAsync(db, id, now, ct);
                }
            }
            if (applied > 0) logger.LogInformation("Applied {Count} scheduled News drafts.", applied);
            return applied;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply scheduled News drafts.");
            return 0;
        }
    }

    /// <summary>Batch cap per pass, so a worker never holds a long transaction.</summary>
    private const int BatchSize = 100;

    /// <summary>Exponential backoff (capped at 6h) recorded on the draft itself.</summary>
    private static async Task BackoffAsync(IPopDbContext db, Guid draftId, DateTime now, CancellationToken ct)
    {
        var attempts = await db.NewsComposerDrafts.AsNoTracking()
            .Where(d => d.Id == draftId).Select(d => d.ScheduleAttemptCount).FirstOrDefaultAsync(ct);
        var next = attempts + 1;
        var delayMinutes = Math.Min(360, Math.Pow(2, Math.Min(next, 9)));
        await db.NewsComposerDrafts
            .Where(d => d.Id == draftId && d.State == "scheduled")
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScheduleAttemptCount, next)
                .SetProperty(d => d.NextScheduleAttemptAt, (DateTime?)now.AddMinutes(delayMinutes)), ct);
    }
}
