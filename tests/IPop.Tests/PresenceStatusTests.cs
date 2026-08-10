using IPop.Core.Presence;
using Xunit;

namespace IPop.Tests;

public class PresenceStatusTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeStatus_UsesExpectedClockBoundaries()
    {
        Assert.Equal("off", PresenceStatus.ComputeStatus(null, null, Now));
        Assert.Equal("on", PresenceStatus.ComputeStatus(Now.AddMinutes(-4), null, Now));
        Assert.Equal("away", PresenceStatus.ComputeStatus(Now.AddMinutes(-5), null, Now));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddMinutes(-15), null, Now));
    }

    [Fact]
    public void ComputeStatus_BusyWinsUntilCleared()
    {
        Assert.Equal("busy", PresenceStatus.ComputeStatus(Now.AddHours(-1), "busy", Now));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddHours(-1), null, Now));
    }
}
