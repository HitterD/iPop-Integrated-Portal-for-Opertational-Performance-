using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// "Forwarded · Name" leads to the original message only for people who are in the chat it came
/// from; everyone else gets at most a DM with the person who wrote it, never the group's content.
public class ChatForwardOriginTests
{
    private static Guid Room(TestDb t, string type, string? name, params User[] members)
    {
        var now = DateTime.UtcNow; var id = Guid.NewGuid();
        t.Db.ChatRooms.Add(new ChatRoom { Id = id, Type = type, Name = name, IsActive = true, CreatedAt = now, LastMessageAt = now });
        foreach (var m in members) t.Db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = id, UserId = m.Id, Role = "member", JoinedAt = now });
        t.Db.SaveChanges();
        return id;
    }

    private static async Task<(Guid Original, Guid Copy)> ForwardAsync(TestDb t, User author, Guid fromRoom, User forwarder, Guid toRoom)
    {
        var sent = await new ChatService(t.Verify()).SendMessageAsync(author.Id, new SendMessageRequest(fromRoom, "the plan", Guid.NewGuid(), null, null), default);
        var copy = await new ChatService(t.Verify()).ForwardMessageAsync(forwarder.Id, sent.Message!.Id, toRoom, default);
        return (sent.Message.Id, copy.Message!.Id);
    }

    [Fact]
    public async Task A_member_of_the_origin_group_is_taken_to_the_original_message()
    {
        using var t = new TestDb();
        var yudi = t.AddUser("FO_Y"); var me = t.AddUser("FO_M");
        var group = Room(t, "group", "Infra", yudi, me);
        var dm = Room(t, "dm", null, yudi, me);
        var (original, copy) = await ForwardAsync(t, yudi, group, yudi, dm);

        var o = await new ChatService(t.Verify()).GetForwardOriginAsync(me.Id, copy, default);

        Assert.Equal(new ForwardOriginDto("original", group, original, "Infra", yudi.Id), o);
    }

    [Fact]
    public async Task Someone_outside_the_origin_group_only_gets_a_dm_with_the_author()
    {
        using var t = new TestDb();
        var yudi = t.AddUser("FO_Y2"); var fwd = t.AddUser("FO_F2"); var outsider = t.AddUser("FO_O2");
        var group = Room(t, "group", "Secret", yudi, fwd);
        var dm = Room(t, "dm", null, fwd, outsider);
        var (_, copy) = await ForwardAsync(t, yudi, group, fwd, dm);

        var o = await new ChatService(t.Verify()).GetForwardOriginAsync(outsider.Id, copy, default);

        Assert.Equal("dm", o!.Kind);
        Assert.Null(o.RoomId); Assert.Null(o.MessageId); Assert.Null(o.RoomName);
        Assert.Equal(yudi.Id, o.SenderId);
    }

    [Fact]
    public async Task A_deleted_original_opens_the_chat_without_a_message_and_leaving_closes_it()
    {
        using var t = new TestDb();
        var yudi = t.AddUser("FO_Y3"); var me = t.AddUser("FO_M3");
        var group = Room(t, "group", "Ops", yudi, me);
        var dm = Room(t, "dm", null, yudi, me);
        var (original, copy) = await ForwardAsync(t, yudi, group, yudi, dm);
        await t.Db.ChatMessages.Where(m => m.Id == original).ExecuteUpdateAsync(u => u.SetProperty(m => m.IsDeleted, true));

        var deleted = await new ChatService(t.Verify()).GetForwardOriginAsync(me.Id, copy, default);
        Assert.Equal(("deleted", (Guid?)group, (Guid?)null), (deleted!.Kind, deleted.RoomId, deleted.MessageId));

        await t.Db.ChatRoomMembers.Where(m => m.RoomId == group && m.UserId == me.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, DateTime.UtcNow));
        var left = await new ChatService(t.Verify()).GetForwardOriginAsync(me.Id, copy, default);
        Assert.Equal("dm", left!.Kind);
        Assert.Null(left.RoomId);
    }

    [Fact]
    public async Task You_cannot_look_up_a_forward_you_cannot_see()
    {
        using var t = new TestDb();
        var yudi = t.AddUser("FO_Y4"); var a = t.AddUser("FO_A4"); var stranger = t.AddUser("FO_S4");
        var group = Room(t, "group", "G", yudi, a);
        var dm = Room(t, "dm", null, yudi, a);
        var (_, copy) = await ForwardAsync(t, yudi, group, yudi, dm);

        Assert.Null(await new ChatService(t.Verify()).GetForwardOriginAsync(stranger.Id, copy, default));
    }

    [Fact]
    public async Task Forwarding_your_own_message_out_of_a_chat_you_left_does_not_dm_yourself()
    {
        using var t = new TestDb();
        var me = t.AddUser("FO_M5"); var other = t.AddUser("FO_O5");
        var group = Room(t, "group", "Old", me, other);
        var dm = Room(t, "dm", null, me, other);
        var (_, copy) = await ForwardAsync(t, me, group, me, dm);
        await t.Db.ChatRoomMembers.Where(m => m.RoomId == group && m.UserId == me.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.LeftAt, DateTime.UtcNow));

        Assert.Equal("none", (await new ChatService(t.Verify()).GetForwardOriginAsync(me.Id, copy, default))!.Kind);
    }
}
