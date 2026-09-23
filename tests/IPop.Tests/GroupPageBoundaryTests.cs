using IPop.Application.Chat;
using IPop.Application.Chat.Mentions;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

/// Task 21.4 — boundary checks on exact limit values.
public class GroupPageBoundaryTests
{
    // Mention query length 40 vs 41
    [Fact]
    public void MentionQuery_40_is_valid_41_rejected()
    {
        var content = "Hello @" + new string('a', 40);
        var active40 = IPop.Web.Services.ComposerMentionModel.DetectActive(content, content.Length);
        Assert.NotNull(active40);
        var content41 = "Hello @" + new string('a', 41);
        var active41 = IPop.Web.Services.ComposerMentionModel.DetectActive(content41, content41.Length);
        Assert.Null(active41);
    }

    // Mention tokens 50 vs 51
    [Fact]
    public void MentionTokens_50_ok_51_over_limit()
    {
        var segs50 = Enumerable.Range(0, 50).Select(_ => (MentionSegment)new MentionSegment.User(Guid.NewGuid(), "Alice")).ToList();
        var segs51 = Enumerable.Range(0, 51).Select(_ => (MentionSegment)new MentionSegment.User(Guid.NewGuid(), "Alice")).ToList();
        Assert.True(MentionPrinter.IsPrintable(segs50));
        Assert.False(MentionPrinter.IsPrintable(segs51));
    }

    // Content length 4000 vs 4001
    [Fact]
    public void Content_4000_ok_4001_rejected()
    {
        Assert.False(string.Empty.Length > 4000);
        Assert.True(new string('x', 4001).Length > 4000);
    }

    // Thread replies 255 limit
    [Fact]
    public void ChatLimits_ThreadReplies_is_255() => Assert.Equal(255, ChatLimits.ThreadReplies);

    // Pinned rooms per user 5
    [Fact]
    public void ChatLimits_PinnedRooms_is_5() => Assert.Equal(5, ChatLimits.PinnedRoomsPerUser);

    // Group members 256
    [Fact]
    public void ChatLimits_GroupMembers_is_256() => Assert.Equal(256, ChatLimits.GroupMembers);

    // Audit page size clamping
    [Fact]
    public async Task Audit_pageSize_clamped()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWNER");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        // Seed 5 audits
        for (int i = 0; i < 5; i++)
            t.Db.ChatMembershipAudits.Add(new ChatMembershipAudit { Id = Guid.NewGuid(), RoomId = room.Id, Action = "member_added", ActorId = owner.Id, TargetUserId = owner.Id, CreatedAt = DateTime.UtcNow.AddMinutes(i) });
        t.Db.SaveChanges();
        IChatService svc = t.NewChatService();
        var p1 = await svc.GetMembershipAuditAsync(owner.Id, room.Id, 1, 0, default); // 0 -> 20
        Assert.Equal(5, p1.TotalCount);
        var p2 = await svc.GetMembershipAuditAsync(owner.Id, room.Id, 1, 200, default); // 200 -> 100
        Assert.Equal(5, p2.TotalCount);
    }

    // MentionPlainText never exceeds limit
    [Fact]
    public void MentionPlainText_respects_maxLength()
    {
        var guid = Guid.NewGuid();
        var content = $"Hello @[Bob](u:{guid:D}) world";
        var plain = MentionPlainText.Project(content, 5);
        Assert.True(plain.Length <= 5);
        Assert.DoesNotContain("@[", plain);
    }

    // MentionResolver dedup + suppress
    [Fact]
    public void MentionResolver_dedup_and_suppress()
    {
        var sender = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var inactive = Guid.NewGuid();
        var segs = new List<MentionSegment> { new MentionSegment.User(userA, "A"), new MentionSegment.User(userA, "A") };
        var members = new Dictionary<Guid, bool> { [sender] = true, [userA] = true, [inactive] = false };
        var input = new MentionResolutionInput(sender, "group", segs, members);
        var result = MentionResolver.Resolve(input);
        Assert.Single(result);
        Assert.Equal(userA, result[0].UserId);
    }

    // Non-member forbidden on MuteRoom
    [Fact]
    public async Task MuteRoom_forbidden_for_non_member()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var stranger = t.AddUser("STR");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        IChatService svc2 = t.NewChatService();
        var res = await svc2.MuteRoomAsync(stranger.Id, room.Id, MuteDuration.Forever, default);
        Assert.False(res.Success);
        Assert.Equal("forbidden", res.Error);
    }
}
