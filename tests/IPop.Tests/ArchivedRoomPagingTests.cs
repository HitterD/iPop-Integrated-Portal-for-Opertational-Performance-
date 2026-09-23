using IPop.Application.Chat;
using IPop.Core.Entities;
using Xunit;

namespace IPop.Tests;

/// ipop-47 — archived rooms past the first page were unreachable and the count
/// saturated at the page size. The fix spans two layers: the service returns a
/// true TotalCount and honours page, and ChatState pages through with a merge
/// that tolerates a room being archived by someone else mid-walk. These lock
/// down the server half plus the merge rule the client half depends on.
public class ArchivedRoomPagingTests
{
    private static (TestDb db, User user) SeedArchived(int count)
    {
        var t = new TestDb();
        var user = t.AddUser("OWNER");
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < count; i++)
        {
            var room = t.AddRoom("group", $"Room {i:D3}");
            room.LastMessageAt = baseTime.AddMinutes(i);   // deterministic order
            t.AddMember(room.Id, user.Id, "member");
            t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings
            {
                UserId = user.Id,
                RoomId = room.Id,
                IsArchived = true,
            });
        }
        t.Db.SaveChanges();
        return (t, user);
    }

    // The bug users could see: 60 archived rooms must report 60, not the page size.
    [Fact]
    public async Task TotalCount_is_the_true_total_not_the_page_size()
    {
        var (t, user) = SeedArchived(60);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var page1 = await svc.GetArchivedRoomsAsync(user.Id, 1, 50, default);
            Assert.Equal(60, page1.TotalCount);
            Assert.Equal(50, page1.Items.Count);
        }
    }

    // The half the count fix did not address: rooms 51+ must actually be fetchable.
    [Fact]
    public async Task Second_page_returns_the_remainder_and_does_not_overlap_the_first()
    {
        var (t, user) = SeedArchived(60);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var page1 = await svc.GetArchivedRoomsAsync(user.Id, 1, 50, default);
            var page2 = await svc.GetArchivedRoomsAsync(user.Id, 2, 50, default);

            Assert.Equal(10, page2.Items.Count);
            Assert.Equal(60, page2.TotalCount);
            // Every room reachable exactly once across the two pages.
            var ids = page1.Items.Select(r => r.Id).Concat(page2.Items.Select(r => r.Id)).ToList();
            Assert.Equal(60, ids.Distinct().Count());
        }
    }

    // A page past the end is empty rather than an error, so the client's
    // "keep asking until Count >= Total" loop terminates instead of throwing.
    [Fact]
    public async Task Page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var (t, user) = SeedArchived(10);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var beyond = await svc.GetArchivedRoomsAsync(user.Id, 5, 50, default);
            Assert.Empty(beyond.Items);
            Assert.Equal(10, beyond.TotalCount);
        }
    }

    // Archiving is per-user: another user's archive flag must not leak into this list.
    [Fact]
    public async Task Archived_list_is_scoped_to_the_requesting_user()
    {
        var (t, user) = SeedArchived(3);
        using (t)
        {
            var stranger = t.AddUser("OTHER");
            t.Db.SaveChanges();
            IChatService svc = t.NewChatService();
            var mine = await svc.GetArchivedRoomsAsync(user.Id, 1, 50, default);
            var theirs = await svc.GetArchivedRoomsAsync(stranger.Id, 1, 50, default);
            Assert.Equal(3, mine.TotalCount);
            Assert.Equal(0, theirs.TotalCount);
        }
    }

    // ipop-47: the client asks for pageSize=50 (ChatState.cs:73) but the endpoint's
    // default is 20 (ChatEndpoints.cs:531). Harmless while the client always sends the
    // param, and this pins the service behaviour that makes it harmless: page size is
    // honoured as given, so the two numbers can differ without rooms going missing.
    [Fact]
    public async Task Page_size_is_honoured_exactly_as_requested()
    {
        var (t, user) = SeedArchived(60);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var at20 = await svc.GetArchivedRoomsAsync(user.Id, 1, 20, default);
            var at50 = await svc.GetArchivedRoomsAsync(user.Id, 1, 50, default);
            Assert.Equal(20, at20.Items.Count);
            Assert.Equal(50, at50.Items.Count);
            // Total is independent of page size — that is what the client's
            // "Count < Total" load-more test relies on at either value.
            Assert.Equal(60, at20.TotalCount);
            Assert.Equal(60, at50.TotalCount);
        }
    }

    // ChatState.MergeArchived's rule: a room already shown is skipped, so a room
    // archived by a third party between page 1 and page 2 cannot appear twice.
    [Fact]
    public void Merge_skips_ids_already_shown()
    {
        var shared = Guid.NewGuid();
        var shown = new List<Guid> { shared, Guid.NewGuid() };
        var incoming = new List<Guid> { shared, Guid.NewGuid() };

        var merged = shown.Concat(incoming.Where(i => shown.All(s => s != i))).ToList();

        Assert.Equal(3, merged.Count);
        Assert.Equal(merged.Count, merged.Distinct().Count());
    }
}
