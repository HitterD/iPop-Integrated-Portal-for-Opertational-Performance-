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
    public void ComputeStatus_BusyWinsOnlyWhileOnline()
    {
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddHours(-1), "busy", Now));
        Assert.Equal("off", PresenceStatus.ComputeStatus(null, "busy", Now));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddMinutes(-2), "busy", Now));
        Assert.Equal("busy", PresenceStatus.ComputeStatus(Now, "busy", Now, idle: true));
        Assert.Equal("off", PresenceStatus.ComputeStatus(Now.AddHours(-1), null, Now));
    }

    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

    [Fact]
    public void BusyUntil_durations_are_computed_on_the_server_clock()
    {
        Assert.True(PresenceStatus.TryBusyUntil("1h", Now, Jakarta, out var oneHour));
        Assert.Equal(Now.AddHours(1), oneHour);
        Assert.True(PresenceStatus.TryBusyUntil("2h", Now, Jakarta, out var twoHours));
        Assert.Equal(Now.AddHours(2), twoHours);
        Assert.True(PresenceStatus.TryBusyUntil(null, Now, Jakarta, out var forever));
        Assert.Null(forever);
        Assert.False(PresenceStatus.TryBusyUntil("forever-ish", Now, Jakarta, out _));
    }

    [Fact]
    public void BusyUntil_today_ends_at_the_users_local_midnight()
    {
        // 12:00 UTC = 19:00 WIB; the day ends at 00:00 WIB = 17:00 UTC.
        Assert.True(PresenceStatus.TryBusyUntil("today", Now, Jakarta, out var end));
        Assert.Equal(new DateTime(2026, 8, 8, 17, 0, 0, DateTimeKind.Utc), end);
        // 18:00 UTC is already the next local day (01:00 WIB), so it runs to the following midnight.
        Assert.True(PresenceStatus.TryBusyUntil("today", Now.AddHours(6), Jakarta, out var next));
        Assert.Equal(new DateTime(2026, 8, 9, 17, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void Timed_busy_lapses_and_busy_only_mutes_while_online()
    {
        Assert.Equal("busy", PresenceStatus.EffectiveManual("busy", Now.AddMinutes(1), Now));
        Assert.Null(PresenceStatus.EffectiveManual("busy", Now, Now));
        Assert.Equal("busy", PresenceStatus.EffectiveManual("busy", null, Now));

        Assert.True(PresenceStatus.IsBusy(Now, "busy", null, Now));
        Assert.False(PresenceStatus.IsBusy(Now, "busy", Now.AddSeconds(-1), Now));
        Assert.False(PresenceStatus.IsBusy(Now.AddHours(-1), "busy", null, Now)); // offline: not muting anything
        Assert.False(PresenceStatus.IsBusy(Now, null, null, Now));
    }
}
