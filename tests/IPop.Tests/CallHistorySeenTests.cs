using IPop.Core.Entities;
using IPop.Infrastructure.Badges;
using IPop.Infrastructure.Calls;
using Xunit;

namespace IPop.Tests;

/// The Calls badge counted missed calls with no way to clear them. SeenAt fixes that:
/// the badge counts only unseen ones and /calls marks them seen (all, or one row).
public class CallHistorySeenTests
{
    private static (TestDb t, User me, Guid[] missed, Guid outgoing) Seed()
    {
        var t = new TestDb();
        var me = t.AddUser("300");
        var peer = t.AddUser("301");
        var now = DateTime.UtcNow;
        Guid AddCall(string direction, string result, int minutesAgo)
        {
            var id = Guid.NewGuid();
            t.Db.CallRooms.Add(new CallRoom
            {
                Id = id, Kind = "user", Status = "ended", CreatorId = direction == "incoming" ? peer.Id : me.Id,
                StartedAt = now.AddMinutes(-minutesAgo), EndedAt = now.AddMinutes(-minutesAgo).AddSeconds(20),
            });
            t.Db.CallHistories.Add(new CallHistory
            {
                Id = Guid.NewGuid(), CallId = id, PerformerId = me.Id, Direction = direction, Result = result,
                StartedAt = now.AddMinutes(-minutesAgo), EndedAt = now.AddMinutes(-minutesAgo).AddSeconds(20),
            });
            return id;
        }
        var missed = new[] { AddCall("incoming", "missed", 5), AddCall("incoming", "missed", 10) };
        var outgoing = AddCall("outgoing", "no-answer", 15);
        t.Db.SaveChanges();
        return (t, me, missed, outgoing);
    }

    [Fact]
    public async Task History_flags_only_unseen_missed_incoming_calls()
    {
        var (t, me, missed, outgoing) = Seed();
        using var _ = t;
        var page = await new CallService(t.Db).GetHistoryAsync(me.Id, 1, 50, default);
        Assert.All(page.Items.Where(i => missed.Contains(i.CallId)), i => Assert.False(i.Seen));
        Assert.True(page.Items.Single(i => i.CallId == outgoing).Seen);
    }

    [Fact]
    public async Task Marking_one_call_seen_drops_the_badge_by_one()
    {
        var (t, me, missed, _) = Seed();
        using var _d = t;
        var count = await new CallService(t.Db).MarkHistorySeenAsync(me.Id, missed[0], default);
        Assert.Equal(1, count);
        Assert.Equal(1, await new CallsBadgeProvider(t.Db).CountAsync(me.Id, default));
    }

    [Fact]
    public async Task Marking_all_seen_clears_the_badge_and_the_flags()
    {
        var (t, me, _, _) = Seed();
        using var _d = t;
        var svc = new CallService(t.Db);
        Assert.Equal(0, await svc.MarkHistorySeenAsync(me.Id, null, default));
        Assert.Equal(0, await new CallsBadgeProvider(t.Db).CountAsync(me.Id, default));
        var page = await svc.GetHistoryAsync(me.Id, 1, 50, default);
        Assert.All(page.Items, i => Assert.True(i.Seen));
    }

    [Fact]
    public async Task Marking_seen_does_not_touch_another_users_calls()
    {
        var (t, me, missed, _) = Seed();
        using var _d = t;
        var other = t.AddUser("302");
        await new CallService(t.Db).MarkHistorySeenAsync(other.Id, null, default);
        Assert.Equal(2, await new CallsBadgeProvider(t.Db).CountAsync(me.Id, default));
    }
}
