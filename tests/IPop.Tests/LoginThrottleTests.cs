using IPop.Infrastructure.Auth;
using Xunit;

namespace IPop.Tests;

/// Per-account lockout alone lets one address spray a single password across many accounts without ever tripping it.
/// The throttle counts failures per client address, independent of which account was tried.
public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void One_address_failing_across_many_accounts_is_blocked_after_the_limit()
    {
        var clock = new Clock();
        var t = new LoginThrottle(failuresPerWindow: 10, window: TimeSpan.FromMinutes(15), clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.False(t.IsBlocked("10.0.0.5"));
            t.RecordFailure("10.0.0.5");        // a different NIK each time: the per-account lockout never trips
        }

        Assert.True(t.IsBlocked("10.0.0.5"));
        Assert.False(t.IsBlocked("10.0.0.6"));  // another office PC is unaffected
    }

    [Fact]
    public void The_block_ends_after_the_window_and_a_success_clears_the_count()
    {
        var clock = new Clock();
        var t = new LoginThrottle(3, TimeSpan.FromMinutes(15), clock);
        for (var i = 0; i < 3; i++) t.RecordFailure("a");
        Assert.True(t.IsBlocked("a"));

        clock.Now += TimeSpan.FromMinutes(16);
        Assert.False(t.IsBlocked("a"));

        t.RecordFailure("b"); t.RecordFailure("b");
        t.RecordSuccess("b");
        t.RecordFailure("b");
        Assert.False(t.IsBlocked("b"));
    }

    [Fact]
    public void Memory_stays_bounded_when_many_addresses_fail_once()
    {
        var clock = new Clock();
        var t = new LoginThrottle(5, TimeSpan.FromMinutes(15), clock, maxTracked: 100);
        for (var i = 0; i < 1000; i++) t.RecordFailure($"ip-{i}");
        Assert.True(t.Tracked <= 100);
    }
}
