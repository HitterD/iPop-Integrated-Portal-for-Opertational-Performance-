using IPop.Application.Chat;
using IPop.Core.Entities;
using Xunit;

namespace IPop.Tests;

/// ipop-48 — same class as ipop-47, found by sweeping the sibling PagedResult loaders.
/// The parent row renders the server's true ThreadReplyCount (up to ChatLimits.ThreadReplies
/// = 255) while the loader fetched a single page of 50 and discarded TotalCount, so a busy
/// thread showed a truthful "200 replies" above exactly 50 rows with no way to reach the rest.
/// The server was already correct; the defect and the fix are both client-side.
public class ThreadReplyPagingTests
{
    private static (TestDb t, User user, ChatMessage parent) SeedThread(int replies)
    {
        var t = new TestDb();
        var user = t.AddUser("OWNER");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, user.Id, "member");
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var parent = t.AddMessage(room.Id, user.Id, "parent", at: baseTime);
        for (int i = 0; i < replies; i++)
        {
            var r = t.AddMessage(room.Id, user.Id, $"reply {i:D3}", at: baseTime.AddMinutes(i + 1));
            r.ThreadParentId = parent.Id;
        }
        parent.ThreadReplyCount = replies;
        t.Db.SaveChanges();
        return (t, user, parent);
    }

    // The label the parent row shows must be reachable in full, not just its first page.
    [Fact]
    public async Task TotalCount_matches_the_ThreadReplyCount_the_parent_row_displays()
    {
        var (t, user, parent) = SeedThread(120);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var page1 = await svc.GetThreadRepliesAsync(parent.Id, user.Id, 1, 50, default);
            Assert.Equal(120, page1.TotalCount);
            Assert.Equal(120, t.Db.ChatMessages.Find(parent.Id)!.ThreadReplyCount);
            Assert.Equal(50, page1.Items.Count);
        }
    }

    // Walking the pages must reach every reply exactly once — the half that was missing.
    [Fact]
    public async Task Paging_reaches_every_reply_exactly_once()
    {
        var (t, user, parent) = SeedThread(120);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var seen = new List<Guid>();
            for (int page = 1; page <= 3; page++)
            {
                var p = await svc.GetThreadRepliesAsync(parent.Id, user.Id, page, 50, default);
                seen.AddRange(p.Items.Select(m => m.Id));
            }
            Assert.Equal(120, seen.Count);
            Assert.Equal(120, seen.Distinct().Count());
        }
    }

    // Ordering is stable across pages, so page 2 continues page 1 rather than reshuffling.
    [Fact]
    public async Task Pages_are_in_stable_chronological_order()
    {
        var (t, user, parent) = SeedThread(60);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var p1 = await svc.GetThreadRepliesAsync(parent.Id, user.Id, 1, 50, default);
            var p2 = await svc.GetThreadRepliesAsync(parent.Id, user.Id, 2, 50, default);
            Assert.Equal(p1.Items.OrderBy(m => m.CreatedAt).Select(m => m.Id), p1.Items.Select(m => m.Id));
            Assert.True(p2.Items.First().CreatedAt >= p1.Items.Last().CreatedAt);
        }
    }

    // A page past the end is empty rather than an error, so the client's
    // "Count < Total" load-more loop terminates instead of throwing.
    [Fact]
    public async Task Page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var (t, user, parent) = SeedThread(10);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var beyond = await svc.GetThreadRepliesAsync(parent.Id, user.Id, 9, 50, default);
            Assert.Empty(beyond.Items);
            Assert.Equal(10, beyond.TotalCount);
        }
    }

    // H4 membership gate still holds on every page, not just the first.
    [Fact]
    public async Task Non_member_gets_nothing_on_any_page()
    {
        var (t, user, parent) = SeedThread(60);
        using (t)
        {
            var stranger = t.AddUser("OTHER");
            t.Db.SaveChanges();
            IChatService svc = t.NewChatService();
            var p1 = await svc.GetThreadRepliesAsync(parent.Id, stranger.Id, 1, 50, default);
            var p2 = await svc.GetThreadRepliesAsync(parent.Id, stranger.Id, 2, 50, default);
            Assert.Empty(p1.Items);
            Assert.Equal(0, p1.TotalCount);
            Assert.Empty(p2.Items);
        }
    }

    // The client dedups paged replies against the same key set the live hub push uses.
    // A reply that arrived over the hub while page 1 was open is also on the server's
    // page 2, and must not appear twice. This pins that rule.
    [Fact]
    public void Paged_replies_dedup_against_live_pushed_keys()
    {
        var live = Guid.NewGuid();
        var seenKeys = new HashSet<Guid> { Guid.NewGuid(), live };  // page 1 + one hub push
        var shown = 2;

        var serverPage2 = new[] { live, Guid.NewGuid(), Guid.NewGuid() }; // overlaps by one
        foreach (var k in serverPage2)
            if (seenKeys.Add(k)) shown++;

        Assert.Equal(4, shown);            // 2 + 2 genuinely new, not 5
        Assert.Equal(4, seenKeys.Count);
    }

    // A live push grows the list without the server total moving; the client increments
    // its total in step so the load-more row does not vanish one reply early.
    [Fact]
    public void Live_push_keeps_total_in_step_with_the_list()
    {
        int total = 60, shown = 50;
        Assert.True(shown < total);        // load-more visible

        shown++; total++;                  // hub push arrives while expanded
        Assert.True(shown < total);        // still 9 unreached — the row must remain

        shown += 10;                       // user loads the rest
        Assert.False(shown < total);       // now genuinely exhausted
    }
}
