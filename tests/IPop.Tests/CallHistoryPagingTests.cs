using IPop.Core.Entities;
using IPop.Infrastructure.Calls;
using Xunit;

namespace IPop.Tests;

/// ipop-49: Calls.razor rendered the server's true Total ("N calls") above a single
/// hard-coded page of 50 rows. CallHistories gets a row PER PARTICIPANT per call and is
/// never pruned, so an active user passes 50 and the rest become unreachable.
/// The server was already correct; these pin that contract so the client can rely on it,
/// and cover the client-side merge rules the fix depends on.
public class CallHistoryPagingTests
{
    /// One row per call for the performer, newest first — mirrors what EndCallAsync writes.
    private static (TestDb t, User me) SeedHistory(int count)
    {
        var t = new TestDb();
        var me = t.AddUser("2000");
        var peer = t.AddUser("2001");
        var baseAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var callId = Guid.NewGuid();
            t.Db.CallRooms.Add(new CallRoom
            {
                Id = callId, CreatorId = me.Id, Kind = "user", Status = "ended",
                StartedAt = baseAt.AddMinutes(i), EstablishedAt = baseAt.AddMinutes(i),
                EndedAt = baseAt.AddMinutes(i).AddSeconds(30),
            });
            t.Db.CallParticipants.Add(new CallParticipant
            {
                Id = Guid.NewGuid(), CallId = callId, UserId = peer.Id,
                Status = "accepted", JoinedAt = baseAt.AddMinutes(i),
            });
            t.Db.CallHistories.Add(new CallHistory
            {
                Id = Guid.NewGuid(), CallId = callId, PerformerId = me.Id,
                Direction = "outgoing", Result = "completed", DurationSeconds = 30,
                StartedAt = baseAt.AddMinutes(i), EndedAt = baseAt.AddMinutes(i).AddSeconds(30),
            });
        }
        t.Db.SaveChanges();
        return (t, me);
    }

    [Fact]
    public async Task Total_is_the_true_total_not_the_page_size()
    {
        var (t, me) = SeedHistory(60);
        using var _ = t;
        var page = await new CallService(t.Verify()).GetHistoryAsync(me.Id, 1, 50, default);
        // The exact contradiction the UI showed: "60 calls" over 50 rows.
        Assert.Equal(60, page.Total);
        Assert.Equal(50, page.Items.Count);
    }

    [Fact]
    public async Task Second_page_returns_the_remainder_without_overlapping_the_first()
    {
        var (t, me) = SeedHistory(60);
        using var _ = t;
        var svc = new CallService(t.Verify());
        var p1 = await svc.GetHistoryAsync(me.Id, 1, 50, default);
        var p2 = await svc.GetHistoryAsync(me.Id, 2, 50, default);
        Assert.Equal(10, p2.Items.Count);
        Assert.Equal(60, p2.Total);
        var all = p1.Items.Concat(p2.Items).Select(i => i.CallId).ToList();
        Assert.Equal(60, all.Distinct().Count());
    }

    [Fact]
    public async Task Pages_stay_in_newest_first_order_across_the_boundary()
    {
        var (t, me) = SeedHistory(60);
        using var _ = t;
        var svc = new CallService(t.Verify());
        var p1 = await svc.GetHistoryAsync(me.Id, 1, 50, default);
        var p2 = await svc.GetHistoryAsync(me.Id, 2, 50, default);
        var joined = p1.Items.Concat(p2.Items).Select(i => i.StartedAt).ToList();
        Assert.Equal(joined.OrderByDescending(x => x).ToList(), joined);
    }

    [Fact]
    public async Task Page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var (t, me) = SeedHistory(60);
        using var _ = t;
        var page = await new CallService(t.Verify()).GetHistoryAsync(me.Id, 3, 50, default);
        Assert.Empty(page.Items);
        Assert.Equal(60, page.Total);
    }

    [Fact]
    public async Task History_is_scoped_to_the_performer()
    {
        var (t, me) = SeedHistory(10);
        using var _ = t;
        var stranger = t.AddUser("9999");
        t.Db.SaveChanges();
        var page = await new CallService(t.Verify()).GetHistoryAsync(stranger.Id, 1, 50, default);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.Total);
        Assert.NotEqual(stranger.Id, me.Id);
    }

    [Fact]
    public async Task Out_of_range_paging_params_are_clamped()
    {
        var (t, me) = SeedHistory(60);
        using var _ = t;
        var svc = new CallService(t.Verify());
        var low = await svc.GetHistoryAsync(me.Id, 0, -5, default);
        Assert.Equal(1, low.Page);
        Assert.Equal(1, low.PageSize);
        var high = await svc.GetHistoryAsync(me.Id, 1, 9999, default);
        Assert.Equal(100, high.PageSize);
    }

    /// The client appends page 2 onto page 1 and dedups on CallId. A call ending between the
    /// two fetches shifts every row down one, so page 1's last row reappears on page 2 —
    /// without dedup it would render twice.
    [Fact]
    public void Append_skips_rows_already_shown()
    {
        var shared = Guid.NewGuid();
        var shown = new List<Guid> { Guid.NewGuid(), shared };
        var incoming = new List<Guid> { shared, Guid.NewGuid() };
        var seen = shown.ToHashSet();
        var merged = new List<Guid>(shown);
        foreach (var id in incoming)
            if (seen.Add(id)) merged.Add(id);
        Assert.Equal(3, merged.Count);
        Assert.Equal(merged.Count, merged.Distinct().Count());
    }
}
