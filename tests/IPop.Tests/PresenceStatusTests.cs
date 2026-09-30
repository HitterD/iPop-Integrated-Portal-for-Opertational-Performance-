using IPop.Core.Presence;
using Xunit;

namespace IPop.Tests;

public class PresenceStatusTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeStatus_OnWhileAnyTabBeats_OffOnceBeatsStop()
    {
        Assert.Equal("off", PresenceStatus.ComputeStatus(null, null, Now));
        Assert.Equal("on", PresenceStatus.ComputeStatus(Now.AddSeconds(-30), null, Now));
        // A hidden tab is throttled to ~1 beat/min; one late beat must not flip to off.
        Assert.Equal("on", PresenceStatus.ComputeStatus(Now.AddSeconds(-110), null, Now));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddMinutes(-2), null, Now));
    }

    [Fact]
    public void ComputeStatus_IdleComputerIsAway_ButOnlyWhileStillBeating()
    {
        Assert.Equal("away", PresenceStatus.ComputeStatus(Now.AddSeconds(-30), null, Now, idle: true));
        // Asleep/closed wins over the last reported idle flag.
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddMinutes(-5), null, Now, idle: true));
    }

    [Fact]
    public void ComputeStatus_BusyWinsUntilCleared()
    {
        Assert.Equal("busy", PresenceStatus.ComputeStatus(Now.AddHours(-1), "busy", Now));
        Assert.Equal("busy", PresenceStatus.ComputeStatus(Now, "busy", Now, idle: true));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddHours(-1), null, Now));
    }
}
