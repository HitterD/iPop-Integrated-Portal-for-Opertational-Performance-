using IPop.Api.Chat;
using Xunit;

namespace IPop.Tests;

/// One limiter backs hub, REST send, thread reply and forward: the 31st send in a minute is refused
/// no matter which door it came through, and the window reopens afterwards.
public class ChatSendLimiterTests
{
    [Fact]
    public void Thirty_pass_the_thirty_first_is_refused_and_the_window_reopens()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var key = "LIM-" + Guid.NewGuid();
        for (var i = 0; i < ChatSendLimiter.Limit; i++) Assert.True(ChatSendLimiter.TryAcquire(key, t0.AddSeconds(i)));

        Assert.False(ChatSendLimiter.TryAcquire(key, t0.AddSeconds(40)));
        Assert.True(ChatSendLimiter.TryAcquire(key, t0.AddSeconds(61)));
    }

    [Fact]
    public void Users_are_limited_independently()
    {
        var t0 = DateTime.UtcNow;
        var a = "LIM-A-" + Guid.NewGuid(); var b = "LIM-B-" + Guid.NewGuid();
        for (var i = 0; i < ChatSendLimiter.Limit; i++) ChatSendLimiter.TryAcquire(a, t0);

        Assert.False(ChatSendLimiter.TryAcquire(a, t0));
        Assert.True(ChatSendLimiter.TryAcquire(b, t0));
    }
}
