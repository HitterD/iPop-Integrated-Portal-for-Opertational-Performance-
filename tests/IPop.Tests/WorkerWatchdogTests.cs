using IPop.Api.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class WorkerWatchdogTests
{
    private sealed class Crashing : BackgroundService
    {
        // Real workers fail after their first await; StartAsync then sees a running task that faults later.
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
    }
    private sealed class OneOff : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Looping : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }
    private sealed class Life : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    [Fact]
    public async Task Reports_a_worker_whose_loop_ended()
    {
        var crashing = new Crashing(); var looping = new Looping(); var oneOff = new OneOff();
        await crashing.StartAsync(default); await looping.StartAsync(default); await oneOff.StartAsync(default);
        var watchdog = new WorkerWatchdog([crashing, looping, oneOff], new Life(), NullLogger<WorkerWatchdog>.Instance);
        try { await crashing.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }

        var dead = watchdog.Scan();

        Assert.Single(dead);
        Assert.Contains("Crashing", dead[0]);
        Assert.Contains("InvalidOperationException", dead[0]);
        await looping.StopAsync(default);
    }
}
