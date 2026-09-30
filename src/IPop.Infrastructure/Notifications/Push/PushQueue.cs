using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Notifications.Push;

/// Web push leaves the request path: producers enqueue a job, <see cref="PushWorker"/> runs it
/// in its own scope. Jobs must not capture a scoped service (DbContext) — only plain data.
public sealed class PushQueue
{
    private readonly Channel<Func<PushDispatcher, CancellationToken, Task>> channel =
        Channel.CreateBounded<Func<PushDispatcher, CancellationToken, Task>>(
            new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(Func<PushDispatcher, CancellationToken, Task> job) => channel.Writer.TryWrite(job);

    public ChannelReader<Func<PushDispatcher, CancellationToken, Task>> Reader => channel.Reader;
}

public sealed class PushWorker(PushQueue queue, IServiceScopeFactory scopes, ILogger<PushWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await job(scope.ServiceProvider.GetRequiredService<PushDispatcher>(), stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Web push job failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
