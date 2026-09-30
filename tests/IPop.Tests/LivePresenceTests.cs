using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Presence and per-user chat state that has to stay live without a page refresh.
public class LivePresenceTests
{
    [Fact]
    public async Task Room_list_reports_idle_dm_partner_as_away()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var now = DateTime.UtcNow;
        var roomId = Guid.NewGuid();
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "dm", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = a.Id, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = b.Id, Role = "member", JoinedAt = now });
        var partner = t.Db.Users.Single(u => u.Id == b.Id);
        partner.PresenceLastSeenAt = now;
        partner.PresenceIsIdle = true;
        t.Db.SaveChanges();

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(a.Id, RoomTab.Direct, 1, 20, default);

        Assert.Equal("away", rooms.Items.Single().PresenceStatus);
    }

    private static RoomListDto Dm(string nik, string status) => new(Guid.NewGuid(), "dm", null, nik, $"User {nik}",
        null, null, 0, false, false, false, status, false, false, null);

    [Fact]
    public void Presence_push_patches_only_the_matching_dm()
    {
        var rooms = new List<RoomListDto> { Dm("A", "off"), Dm("B", "off"), Dm("C", "on") with { Type = "group", RecipientNik = "A" } };

        var patched = ChatState.PatchRoomPresence(rooms, "a", "on");

        Assert.Equal("on", patched[0].PresenceStatus);
        Assert.Equal("off", patched[1].PresenceStatus);
        Assert.Equal("on", patched[2].PresenceStatus); // group rows untouched
        Assert.Equal("off", rooms[0].PresenceStatus);   // original list not mutated
    }

    [Fact]
    public void Presence_push_for_unknown_nik_returns_same_list()
    {
        var rooms = new List<RoomListDto> { Dm("A", "off") };
        Assert.Same(rooms, ChatState.PatchRoomPresence(rooms, "Z", "on"));
    }

    [Fact]
    public void Presence_push_updates_member_and_recounts_online()
    {
        var room = new ChatRoomDto(Guid.NewGuid(), "group", "G", null, 3, 1,
            [
                new MemberDto(Guid.NewGuid(), "A", "A", "owner", "on"),
                new MemberDto(Guid.NewGuid(), "B", "B", "member", "off"),
                new MemberDto(Guid.NewGuid(), "C", "C", "member", "away"),
            ], "owner", []);

        var up = ChatState.PatchMemberPresence(room, "B", "on");
        Assert.Equal("on", up.Members[1].PresenceStatus);
        Assert.Equal(2, up.OnlineCount);

        // away/busy do not count as online (server rule: status == "on").
        var away = ChatState.PatchMemberPresence(up, "A", "away");
        Assert.Equal(1, away.OnlineCount);

        Assert.Same(away, ChatState.PatchMemberPresence(away, "A", "away"));
    }

    [Fact]
    public void Hub_retry_backoff_widens_then_holds_at_a_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), HubRetry.Delay(0));
        Assert.Equal(TimeSpan.FromSeconds(15), HubRetry.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), HubRetry.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(60), HubRetry.Delay(3));
        Assert.Equal(TimeSpan.FromSeconds(60), HubRetry.Delay(50));
    }
}
