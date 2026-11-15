using IPop.Api.Calls;
using Xunit;

namespace IPop.Tests;

/// A dropped call connection (refresh, Wi-Fi blip, sleep) keeps the user's seat for the grace
/// period; coming back inside it cancels the pending end, staying away past it ends the call.
public class CallConnectionsTests
{
    private sealed class Probe
    {
        public List<Guid> Ended { get; } = [];
        public TaskCompletionSource<Guid> FirstEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task End(Guid userId)
        {
            lock (Ended) Ended.Add(userId);
            FirstEnded.TrySetResult(userId);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Reconnecting_inside_the_grace_keeps_the_call()
    {
        var probe = new Probe();
        var c = new CallConnections(TimeSpan.FromMilliseconds(200), probe.End);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        await Task.Delay(50);
        c.Connected(u);
        await Task.Delay(400);
        Assert.Empty(probe.Ended);
    }

    [Fact]
    public async Task Staying_away_past_the_grace_ends_the_call_once()
    {
        var probe = new Probe();
        var c = new CallConnections(TimeSpan.FromMilliseconds(100), probe.End);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        Assert.Equal(u, await probe.FirstEnded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        lock (probe.Ended) Assert.Equal([u], probe.Ended);
    }

    [Fact]
    public async Task A_quick_drop_and_return_then_a_real_drop_still_ends_after_a_full_grace()
    {
        var probe = new Probe();
        var timers = new List<(TaskCompletionSource Timer, TaskCompletionSource Observed)>();
        async Task Delay(TimeSpan _, CancellationToken token)
        {
            var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timers.Add((timer, observed));
            // Even an obsolete timer completion must not end the reconnected user's call.
            await timer.Task;
            observed.SetResult();
        }
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, Delay);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        c.Connected(u);
        c.Disconnected(u);
        Assert.Equal(2, timers.Count);
        timers[0].Timer.SetResult();
        await timers[0].Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(probe.Ended);
        timers[1].Timer.SetResult();
        Assert.Equal(u, await probe.FirstEnded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        lock (probe.Ended) Assert.Equal([u], probe.Ended);
    }

    [Fact]
    public async Task Another_open_tab_keeps_the_call()
    {
        var probe = new Probe();
        var c = new CallConnections(TimeSpan.FromMilliseconds(100), probe.End);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Connected(u);
        c.Disconnected(u);
        await Task.Delay(300);
        Assert.Empty(probe.Ended);
    }
}
