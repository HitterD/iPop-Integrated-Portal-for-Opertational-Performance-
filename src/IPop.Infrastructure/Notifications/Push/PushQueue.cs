using System.Threading.Channels;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

/// Web Push producers write a durable <see cref="PushJob"/> (references only) to the database;
/// <see cref="PushWorker"/> expands and sends it with per-browser retry. This singleton only
/// wakes the worker so a toast leaves within milliseconds; the timer covers retries and restarts.
public sealed class PushQueue
{
    private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => signal.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await signal.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// Stages a job in the caller's unit of work (saved with the caller's next SaveChanges).
    /// Call <see cref="Notify"/> after that save commits.
    public static PushJob Add(IPopDbContext db, string kind, Guid? refId, IEnumerable<Guid>? userIds = null,
        string? eventType = null, string? priority = null, string? title = null)
    {
        var now = DateTime.UtcNow;
        var job = new PushJob
        {
            Id = Guid.NewGuid(), Kind = kind, RefId = refId,
            UserIds = userIds is null ? null : PushDispatcher.JoinIds(userIds),
            EventType = eventType, Priority = priority, Title = title is { Length: > 300 } ? title[..300] : title,
            CreatedAt = now, NextAttemptAt = now,
        };
        db.PushJobs.Add(job);
        return job;
    }

    /// Persists a job on its own (the source change was already committed) and wakes the worker.
    /// Returns once the intent is durable.
    public async Task<Guid> EnqueueAsync(IPopDbContext db, string kind, Guid? refId, CancellationToken ct,
        IEnumerable<Guid>? userIds = null, string? eventType = null, string? priority = null, string? title = null)
    {
        var job = Add(db, kind, refId, userIds, eventType, priority, title);
        try { await db.SaveChangesAsync(ct); }
        catch
        {
            // Leave the caller's unit of work as it was: only the unsaved job is dropped.
            db.Entry(job).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            throw;
        }
        Notify();
        return job.Id;
    }
}

public sealed class PushWorker(PushQueue queue, IServiceScopeFactory scopes, ILogger<PushWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<PushDispatcher>();
                    processed = await dispatcher.ProcessAsync(DateTime.UtcNow, stoppingToken);
                    if (DateTime.UtcNow >= nextPurge)
                    {
                        nextPurge = DateTime.UtcNow + PurgeEvery;
                        await dispatcher.PurgeAsync(DateTime.UtcNow, stoppingToken);
                    }
                } while (processed > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError("Web push outbox pass failed ({Error}); will retry.", ex.GetType().Name);
            }
            try { await queue.WaitAsync(Idle, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
