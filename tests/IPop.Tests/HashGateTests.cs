using IPop.Infrastructure.Auth;
using Xunit;

namespace IPop.Tests;

public class HashGateTests
{
    [Fact]
    public async Task Never_runs_more_hashes_at_once_than_its_slots()
    {
        var gate = new HashGate(2, TimeSpan.FromSeconds(10));
        var running = 0; var peak = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => gate.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Thread.Sleep(50);
            Interlocked.Decrement(ref running);
            return true;
        })).ToList();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Ran && r.Result));
        Assert.True(peak <= 2, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Answers_busy_instead_of_queueing_forever()
    {
        var gate = new HashGate(1, TimeSpan.FromMilliseconds(100));
        using var release = new ManualResetEventSlim();
        var holder = gate.RunAsync(() => { release.Wait(); return 1; });
        await Task.Delay(50);

        var (ran, _) = await gate.RunAsync(() => 2);

        Assert.False(ran);
        release.Set();
        Assert.True((await holder).Ran);
    }
}
