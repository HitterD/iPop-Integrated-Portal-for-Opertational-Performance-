using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IPop.Api.Diagnostics;

/// Finds background workers that stopped for good.
///
/// The host runs with BackgroundServiceExceptionBehavior.Ignore so one failing worker cannot take the whole API
/// down — but then a worker whose loop throws simply ends and nobody notices (the 2026-11-09 audit found nothing
/// that would report it: mail notifications or reminders would just stop). This checks every hosted
/// BackgroundService once a minute; a worker that ended while the app is running is logged as an error (Loki +
/// alert) and makes /healthz/live fail, so the liveness probe restarts the pod and the worker comes back.
public sealed class WorkerWatchdog(IServiceProvider services, IHostApplicationLifetime life, ILogger<WorkerWatchdog> log)
    : BackgroundService
{
    // Resolved on first scan, not in the constructor: this class is itself an IHostedService, so asking for
    // IEnumerable<IHostedService> while being constructed deadlocked the container (API never started).
    private IHostedService[]? hosted;
    internal WorkerWatchdog(IEnumerable<IHostedService> workers, IHostApplicationLifetime life, ILogger<WorkerWatchdog> log)
        : this(EmptyProvider.Instance, life, log) => hosted = workers.ToArray();

    private sealed class EmptyProvider : IServiceProvider
    {
        public static readonly EmptyProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private volatile string[] dead = [];

    /// Names of workers that ended while the app is still running (empty = all alive).
    public IReadOnlyList<string> Dead => dead;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (life.ApplicationStopping.IsCancellationRequested) return;
            var now = Scan();
            foreach (var name in now.Except(dead)) log.LogError("Background worker {Worker} has stopped; the API will be restarted by its health check.", name);
            dead = now;
        }
    }

    internal string[] Scan() => (hosted ??= services.GetServices<IHostedService>().ToArray())
        .OfType<BackgroundService>()
        .Where(s => !ReferenceEquals(s, this) && s.ExecuteTask is { IsCompleted: true })
        .Select(s => s.GetType().Name + (s.ExecuteTask!.IsFaulted ? $" ({s.ExecuteTask.Exception?.GetBaseException().GetType().Name})" : ""))
        .ToArray();
}
