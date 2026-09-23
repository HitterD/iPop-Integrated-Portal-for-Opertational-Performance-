using IPop.Api.Endpoints;
using IPop.Core.Entities;
using Xunit;

namespace IPop.Tests;

/// ipop-51: the Gateway admin page fetched ONE hard-coded page of 100 requests (and 100 dead
/// letters) and discarded the rest, while the panel head rendered the server's true Total right
/// beside the truncated table. NotificationRequests is append-only — NotificationGatewayService
/// :97/:101 are the only writers, there is no Remove and no retention job in the solution — so a
/// busy gateway passes 100 and stays past it. The fix is client-side (a load-more that appends
/// pages); these pin the server contract that fix depends on: Total is the true total, page two
/// holds the remainder without overlapping page one, and ordering is stable across the boundary.
public class GatewayRequestPagingTests
{
    private static (TestDb t, NotificationGatewayClient c) Seed(int requests, string status = "queued")
    {
        var t = new TestDb();
        var c = t.AddNotificationClient("hr", "h:secret123");
        var baseTime = new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < requests; i++)
        {
            // AcceptedAt ascending with i, so the newest-first ordering is i = requests-1 .. 0.
            t.Db.NotificationRequests.Add(new NotificationRequest
            {
                Id = Guid.NewGuid(),
                ClientId = c.Id,
                IdempotencyKey = $"k{i}",
                Priority = "bulk",
                EventType = "payroll.ready",
                Title = $"req-{i}",
                Body = "body",
                Status = status,
                AcceptedAt = baseTime.AddMinutes(i),
                CreatedAt = baseTime.AddMinutes(i),
                UpdatedAt = baseTime.AddMinutes(i),
            });
        }
        t.Db.SaveChanges();
        return (t, c);
    }

    [Fact]
    public async Task Total_is_the_true_total_not_the_page_size()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 100, default);

        // The exact defect: the head printed this figure while the table held only Items.Count.
        Assert.Equal(130, res.Total);
        Assert.Equal(100, res.Items.Count);
    }

    [Fact]
    public async Task Second_page_returns_the_remainder_without_overlapping_the_first()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var p1 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 100, default);
        var p2 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 2, 100, default);

        Assert.Equal(30, p2.Items.Count);
        Assert.Equal(130, p2.Total);
        Assert.Empty(p1.Items.Select(x => x.Id).Intersect(p2.Items.Select(x => x.Id)));
        // Together the two pages are the whole set — nothing is unreachable.
        Assert.Equal(130, p1.Items.Concat(p2.Items).Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public async Task Pages_stay_in_newest_first_order_across_the_boundary()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var p1 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 100, default);
        var p2 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 2, 100, default);

        var joined = p1.Items.Concat(p2.Items).Select(x => x.AcceptedAt).ToList();
        Assert.Equal(joined.OrderByDescending(x => x).ToList(), joined);
        // req-129 is newest, so it heads page 1; req-0 is oldest, so it ends page 2.
        Assert.Equal("payroll.ready req-129", $"{p1.Items[0].EventType} {p1.Items[0].Title}");
        Assert.Equal("req-0", p2.Items[^1].Title);
    }

    [Fact]
    public async Task Status_filter_narrows_the_total_that_paging_reports()
    {
        var (t, c) = Seed(120);
        using var _t = t;
        t.Db.NotificationRequests.Add(new NotificationRequest
        {
            Id = Guid.NewGuid(), ClientId = c.Id, IdempotencyKey = "fail-1", Priority = "high",
            EventType = "payroll.ready", Title = "boom", Body = "body", Status = "failed",
            AcceptedAt = new DateTime(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        t.Db.SaveChanges();

        var all = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 100, default);
        var failed = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), "failed", null, null, 1, 100, default);

        // The filter is part of the paging identity — switching it must restart at page 1 against
        // the new Total, never append onto rows collected under the old predicate.
        Assert.Equal(121, all.Total);
        Assert.Equal(1, failed.Total);
        Assert.Single(failed.Items);
    }

    [Fact]
    public async Task Page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 9, 100, default);

        Assert.Empty(res.Items);
        Assert.Equal(130, res.Total); // HasMoreRequests must go false here, not loop forever.
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99)]
    public async Task Out_of_range_page_is_clamped_rather_than_a_negative_offset(int page)
    {
        var (t, _) = Seed(130);
        using var _t = t;

        // Same failure ipop-50 hit for real: an unclamped page reaches Skip((0-1)*size) and
        // Postgres rejects the negative OFFSET with a 500. This core clamps internally.
        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, page, 100, default);

        Assert.Equal(1, res.Page);
        Assert.Equal(100, res.Items.Count);
        Assert.Equal(130, res.Total);
    }

    [Fact]
    public async Task Over_large_page_size_is_clamped_to_a_hundred()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var res = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 5000, default);

        // A caller cannot dodge paging by asking for everything at once, which is exactly why
        // the client needs the load-more rather than a bigger pageSize.
        Assert.Equal(100, res.PageSize);
        Assert.Equal(100, res.Items.Count);
        Assert.Equal(130, res.Total);
    }

    [Fact]
    public async Task Append_skips_rows_already_shown()
    {
        var (t, _) = Seed(130);
        using var _t = t;

        var p1 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 1, 100, default);
        var p2 = await NotificationGatewayEndpoints.ListRequestsCore(t.Verify(), null, null, null, 2, 100, default);

        // Mirrors LoadMoreRequests: a request accepted between the two fetches shifts every row
        // down one, so the tail of page 1 can reappear as the head of page 2. Id dedups it.
        var rows = p1.Items.ToList();
        var seen = rows.Select(r => r.Id).ToHashSet();
        var overlapped = new[] { rows[^1] }.Concat(p2.Items);
        foreach (var row in overlapped)
            if (seen.Add(row.Id))
                rows.Add(row);

        Assert.Equal(130, rows.Count);
        Assert.Equal(130, rows.Select(r => r.Id).Distinct().Count());
    }
}
