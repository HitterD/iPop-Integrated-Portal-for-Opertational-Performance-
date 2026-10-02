using IPop.Application.News;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.News;

/// <summary>
/// Removes composer media that was uploaded and then never referenced (abandoned editor sessions,
/// discarded drafts, deleted posts), 24 hours after upload. All the safety lives in
/// <see cref="INewsComposerMediaService.CleanupAsync"/>: it re-checks references inside the same
/// statement as the tombstone, never touches a live post's media, and never frees bytes a live link
/// still needs. This worker is only the schedule.
///
/// <para>This class holds no reference graph of its own, so the schedule is safe to disable or add
/// later without affecting correctness — media simply accumulates until a pass runs.</para>
/// </summary>
public sealed class NewsComposerMediaCleanupWorker(IServiceScopeFactory scopes, ILogger<NewsComposerMediaCleanupWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var svc = scope.ServiceProvider.GetRequiredService<INewsComposerMediaService>();
                    var result = await svc.CleanupAsync(NewsComposerMediaCleanupRequest.Default, stoppingToken);
                    if (result.Deleted > 0 || result.Failed > 0)
                        log.LogInformation("News composer media GC: scanned {Scanned}, deleted {Deleted}, retained {Retained}, failed {Failed}",
                            result.Scanned, result.Deleted, result.Retained, result.Failed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "News composer media GC failed; will retry next interval");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }
}