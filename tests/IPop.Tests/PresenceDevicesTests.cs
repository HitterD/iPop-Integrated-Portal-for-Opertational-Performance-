using IPop.Api.Presence;
using IPop.Core.Presence;
using IPop.Web.Models;
using Xunit;

namespace IPop.Tests;

/// Away needs every open tab/device idle; sign-out drops only that device's lease.
public class PresenceDevicesTests
{
    private static readonly DateTime T0 = new(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Away_only_when_every_device_is_idle()
    {
        var d = new PresenceDevices();
        Assert.True(d.Beat("A", "laptop", idle: true, T0));
        Assert.False(d.Beat("A", "desktop", idle: false, T0));   // an active second computer keeps "on"
        Assert.False(d.Beat("A", "laptop", idle: true, T0.AddSeconds(30)));
        Assert.True(d.Beat("A", "desktop", idle: true, T0.AddSeconds(30)));
    }

    [Fact]
    public void A_device_that_stopped_beating_no_longer_counts()
    {
        var d = new PresenceDevices();
        d.Beat("A", "closed-tab", idle: false, T0);
        Assert.True(d.Beat("A", "laptop", idle: true, T0 + PresenceStatus.OfflineAfter));
    }

    [Fact]
    public void Leave_returns_remaining_state_or_null_when_last_device_signs_out()
    {
        var d = new PresenceDevices();
        d.Beat("A", "laptop", idle: false, T0);
        d.Beat("A", "desktop", idle: true, T0);

        Assert.True(d.Leave("A", "laptop", T0.AddSeconds(5)));   // the idle desktop is all that's left
        Assert.Null(d.Leave("A", "desktop", T0.AddSeconds(6)));  // nobody left → offline now
        Assert.Null(d.Leave("B", "x", T0));
    }

    [Fact]
    public void Clients_without_a_device_id_share_one_lease()
    {
        var d = new PresenceDevices();
        d.Beat("A", null, idle: false, T0);
        Assert.True(d.Beat("A", "", idle: true, T0.AddSeconds(1)));
    }

    [Fact]
    public void Last_seen_prefers_the_real_beat_over_the_aged_offline_marker()
    {
        var beat = T0;
        var aged = T0.AddMinutes(-2);
        Assert.Equal(beat, PresenceStatus.LastSeen(beat, aged));
        Assert.Equal(aged, PresenceStatus.LastSeen(null, aged));
    }

    [Fact]
    public void Last_seen_label_only_for_offline_people()
    {
        var now = T0;
        Assert.Null(LastSeen.Label("on", now.AddMinutes(-5), now));
        Assert.Null(LastSeen.Label("off", null, now));
        Assert.Equal("Last seen just now", LastSeen.Label("off", now.AddSeconds(-20), now));
        Assert.Equal("Last seen 5m ago", LastSeen.Label("off", now.AddMinutes(-5), now));
        Assert.StartsWith("Last seen ", LastSeen.Label("off", now.AddDays(-30), now));
        Assert.Equal("Offline", LastSeen.OfflineLabel(null));
    }
}
