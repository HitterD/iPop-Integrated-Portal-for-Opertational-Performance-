using IPop.Api.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPop.Tests;

public class WorkerWatchdogTests
{
    private sealed class Crashing : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
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
        var crashing = new Crashing(); var looping = new Looping();
        await crashing.StartAsync(default); await looping.StartAsync(default);
        var watchdog = new WorkerWatchdog([crashing, looping], new Life(), NullLogger<WorkerWatchdog>.Instance);

        var dead = watchdog.Scan();

        Assert.Single(dead);
        Assert.Contains("Crashing", dead[0]);
        Assert.Contains("InvalidOperationException", dead[0]);
        await looping.StopAsync(default);
    }
}
