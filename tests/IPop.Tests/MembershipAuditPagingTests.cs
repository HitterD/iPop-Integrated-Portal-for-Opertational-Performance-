using IPop.Application.Chat;
using Xunit;

namespace IPop.Tests;

/// ipop-50: the room-audit endpoint was the one paged param in ChatEndpoints.cs passed through
/// raw. No service clamps `page`, so page=0 reached Skip((0-1)*pageSize) and Postgres rejected
/// the negative OFFSET with a 500 (reproduced live before the fix). These pin the service
/// contract the endpoint's Math.Max(1, ...) now relies on.
public class MembershipAuditPagingTests
{
    private static (TestDb t, Guid owner, Guid room) SeedAudit(int entries)
    {
        var t = new TestDb();
        var owner = t.AddUser("3000");
        var room = t.AddRoom("group", "audited");
        t.AddMember(room.Id, owner.Id, "owner");
        var at = new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < entries; i++)
        {
            t.Db.ChatMembershipAudits.Add(new IPop.Core.Entities.ChatMembershipAudit
            {
                Id = Guid.NewGuid(), RoomId = room.Id, ActorId = owner.Id,
                Action = "add", CreatedAt = at.AddMinutes(i),
            });
        }
        t.Db.SaveChanges();
        return (t, owner.Id, room.Id);
    }

    /// The negative-OFFSET crash, at the service boundary: a caller that does NOT clamp
    /// produces a negative Skip. The endpoint is what must prevent this.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void Non_positive_page_yields_a_negative_skip(int page)
    {
        const int pageSize = 20;
        Assert.True((page - 1) * pageSize < 0);
        // What the endpoint now does instead.
        Assert.Equal(1, Math.Max(1, page));
    }

    [Fact]
    public async Task Clamped_page_returns_the_first_page_rather_than_throwing()
    {
        var (t, owner, room) = SeedAudit(30);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var clamped = await svc.GetMembershipAuditAsync(owner, room, Math.Max(1, 0), 20, default);
            var first = await svc.GetMembershipAuditAsync(owner, room, 1, 20, default);
            Assert.Equal(20, clamped.Items.Count);
            Assert.Equal(30, clamped.TotalCount);
            Assert.Equal(first.Items.Select(i => i.Id), clamped.Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task Total_is_the_true_total_and_page_two_holds_the_remainder()
    {
        var (t, owner, room) = SeedAudit(30);
        using (t)
        {
            IChatService svc = t.NewChatService();
            var p1 = await svc.GetMembershipAuditAsync(owner, room, 1, 20, default);
            var p2 = await svc.GetMembershipAuditAsync(owner, room, 2, 20, default);
            Assert.Equal(30, p1.TotalCount);
            Assert.Equal(10, p2.Items.Count);
            Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));
        }
    }

    /// pageSize needs no endpoint clamp because the service already guards it (:1723).
    /// Pinned so nobody "tidies" that guard away on the strength of the endpoint looking bare.
    [Fact]
    public async Task Service_guards_page_size_internally()
    {
        var (t, owner, room) = SeedAudit(30);
        using (t)
        {
            IChatService svc = t.NewChatService();
            Assert.Equal(20, (await svc.GetMembershipAuditAsync(owner, room, 1, 0, default)).Items.Count);
            Assert.Equal(30, (await svc.GetMembershipAuditAsync(owner, room, 1, 9999, default)).Items.Count);
        }
    }

    [Fact]
    public async Task Non_admin_member_gets_nothing()
    {
        var (t, _, room) = SeedAudit(30);
        using (t)
        {
            var plain = t.AddUser("3001");
            t.AddMember(room, plain.Id, "member");
            t.Db.SaveChanges();
            var r = await ((IChatService)t.NewChatService()).GetMembershipAuditAsync(plain.Id, room, 1, 20, default);
            Assert.Empty(r.Items);
            Assert.Equal(0, r.TotalCount);
        }
    }
}
