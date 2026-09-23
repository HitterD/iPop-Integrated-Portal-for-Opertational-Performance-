using IPop.Application.Chat;
using Xunit;

namespace IPop.Tests;

/// Smoke validation of Requirement 21 matrix — verifies ChatService actually enforces role checks
/// rather than trusting the UI to hide buttons.
public class GroupPageAuthzTests
{
    [Fact]
    public async Task Member_cannot_change_role()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var target = t.AddUser("TGT");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, member.Id, "member");
        t.AddMember(room.Id, target.Id, "member");
        var svc = t.NewChatService();
        var res = await svc.ChangeRoleAsync(member.Id, room.Id, target.Id, ChatMemberRole.Admin, default);
        Assert.False(res.Success);
    }

    [Fact]
    public async Task Admin_cannot_remove_owner()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var admin = t.AddUser("ADM");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, admin.Id, "admin");
        var svc = t.NewChatService();
        var res = await svc.RemoveMemberAsync(admin.Id, room.Id, owner.Id, default);
        Assert.False(res.Success);
    }

    [Fact]
    public async Task Member_cannot_update_identity()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, member.Id, "member");
        IChatService svc = t.NewChatService();
        var res = await svc.UpdateGroupIdentityAsync(member.Id, room.Id, "NewName", null, null, default);
        Assert.False(res.Success);
        Assert.Equal("forbidden", res.Error);
    }

    [Fact]
    public async Task Non_member_cannot_pin_room()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var stranger = t.AddUser("STR");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        IChatService svc = t.NewChatService();
        var res = await svc.PinRoomAsync(stranger.Id, room.Id, default);
        Assert.False(res.Success);
        Assert.Equal("forbidden", res.Error);
    }

    [Fact]
    public async Task Audit_forbidden_for_member()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var member = t.AddUser("MEM");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, member.Id, "member");
        IChatService svc = t.NewChatService();
        var res = await svc.GetMembershipAuditAsync(member.Id, room.Id, 1, 20, default);
        Assert.Equal(0, res.TotalCount);
    }
}
