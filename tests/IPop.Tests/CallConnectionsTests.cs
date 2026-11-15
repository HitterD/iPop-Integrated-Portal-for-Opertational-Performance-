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
        public Task End(Guid userId) { lock (Ended) Ended.Add(userId); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Reconnecting_inside_the_grace_keeps_the_call()
    {
        var probe = new Probe(); var clock = new ManualClock();
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, clock);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(10));
        c.Connected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(120));
        Assert.Empty(probe.Ended);
    }

    [Fact]
    public async Task Staying_away_past_the_grace_ends_the_call_once()
    {
        var probe = new Probe(); var clock = new ManualClock();
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, clock);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(61));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(600));
        Assert.Equal([u], probe.Ended);
    }

    [Fact]
    public async Task A_quick_drop_and_return_then_a_real_drop_still_ends_after_a_full_grace()
    {
        var probe = new Probe(); var clock = new ManualClock();
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, clock);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(40));
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(40));   // the first timer's 60 s has passed
        Assert.Empty(probe.Ended);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(21));
        Assert.Equal([u], probe.Ended);
    }

    [Fact]
    public async Task Another_open_tab_keeps_the_call()
    {
        var probe = new Probe(); var clock = new ManualClock();
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, clock);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(300));
        Assert.Empty(probe.Ended);
    }

    /// Manual clock: timers fire only when the test advances time, so no test depends on real sleeps or CI load.
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var t = new ManualTimer(this, callback, state, dueTime);
            lock (gate) timers.Add(t);
            return t;
        }
        public async Task AdvanceAsync(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (gate)
            {
                now += by;
                due = timers.Where(t => t.Due is { } d && d <= now).ToList();
                foreach (var t in due) t.Due = null;
            }
            foreach (var t in due) t.Fire();
            for (var i = 0; i < 20; i++) await Task.Yield();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback cb, object? state, TimeSpan due) : ITimer
        {
            public DateTimeOffset? Due { get; set; } = due == Timeout.InfiniteTimeSpan ? null : clock.now + due;
            public void Fire() => cb(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime; return true; }
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Due = null; return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Grace_is_decided_by_the_injected_clock_not_by_real_sleeps()
    {
        var probe = new Probe();
        var clock = new ManualClock();
        var c = new CallConnections(TimeSpan.FromSeconds(60), probe.End, clock);
        var u = Guid.NewGuid();
        c.Connected(u);
        c.Disconnected(u);

        await clock.AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.Empty(probe.Ended);
        c.Connected(u);
        c.Disconnected(u);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(59));   // the first timer's 60 s has passed: it must not end the call
        Assert.Empty(probe.Ended);
        await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([u], probe.Ended);
    }
}
