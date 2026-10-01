using System.Text.Json;
using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

// Group chat polish: poll voting by any member, room-list preview prefixes, seen-by pending list.
public class ChatGroupFeatureTests
{
    private const string Poll = """[poll]{"Question":"Lunch?","Options":["Rice","Noodles"],"AllowMultiple":false,"Votes":{}}""";

    private static (User Owner, User Member, User Outsider, Guid RoomId) SeedGroup(TestDb t)
    {
        var owner = t.AddUser("A");
        var member = t.AddUser("B");
        var outsider = t.AddUser("C");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "group", Name = "Team", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = owner.Id, Role = "owner", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = member.Id, Role = "member", JoinedAt = now });
        t.Db.SaveChanges();
        return (owner, member, outsider, roomId);
    }

    private static ChatMessage AddMessage(TestDb t, Guid roomId, Guid senderId, string content, string type = "text")
    {
        var m = new ChatMessage
        {
            Id = Guid.NewGuid(), RoomId = roomId, SenderId = senderId, Type = type, Content = content,
            IdempotencyKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        };
        t.Db.ChatMessages.Add(m);
        t.Db.SaveChanges();
        return m;
    }

    private static Dictionary<string, List<string>> Votes(MessageDto dto)
    {
        using var doc = JsonDocument.Parse(dto.Content["[poll]".Length..]);
        return doc.RootElement.GetProperty("Votes").Deserialize<Dictionary<string, List<string>>>()!;
    }

    [Fact]
    public async Task VotePoll_non_sender_member_can_vote_and_single_choice_switches()
    {
        using var t = new TestDb();
        var (owner, member, _, roomId) = SeedGroup(t);
        var msg = AddMessage(t, roomId, owner.Id, Poll);
        var svc = new ChatService(t.Verify());

        var first = await svc.VotePollAsync(member.Id, msg.Id, 0, default);
        Assert.True(first.Success, first.Error);
        Assert.Equal(["B"], Votes(first.Message!)["0"]);

        var switched = await svc.VotePollAsync(member.Id, msg.Id, 1, default);
        var votes = Votes(switched.Message!);
        Assert.False(votes.ContainsKey("0"));
        Assert.Equal(["B"], votes["1"]);

        var ownerVote = await svc.VotePollAsync(owner.Id, msg.Id, 1, default);
        Assert.Equal(["B", "A"], Votes(ownerVote.Message!)["1"]);
    }

    [Fact]
    public async Task VotePoll_same_option_twice_removes_vote()
    {
        using var t = new TestDb();
        var (owner, member, _, roomId) = SeedGroup(t);
        var msg = AddMessage(t, roomId, owner.Id, Poll);
        var svc = new ChatService(t.Verify());

        await svc.VotePollAsync(member.Id, msg.Id, 0, default);
        var undone = await svc.VotePollAsync(member.Id, msg.Id, 0, default);

        Assert.True(undone.Success);
        Assert.Empty(Votes(undone.Message!));
    }

    [Fact]
    public async Task VotePoll_multiple_choice_keeps_other_options()
    {
        using var t = new TestDb();
        var (owner, member, _, roomId) = SeedGroup(t);
        var msg = AddMessage(t, roomId, owner.Id, Poll.Replace("\"AllowMultiple\":false", "\"AllowMultiple\":true"));
        var svc = new ChatService(t.Verify());

        await svc.VotePollAsync(member.Id, msg.Id, 0, default);
        var both = await svc.VotePollAsync(member.Id, msg.Id, 1, default);

        var votes = Votes(both.Message!);
        Assert.Equal(["B"], votes["0"]);
        Assert.Equal(["B"], votes["1"]);
    }

    [Fact]
    public async Task VotePoll_rejects_outsider_bad_option_and_non_poll()
    {
        using var t = new TestDb();
        var (owner, member, outsider, roomId) = SeedGroup(t);
        var poll = AddMessage(t, roomId, owner.Id, Poll);
        var text = AddMessage(t, roomId, owner.Id, "hello");
        var svc = new ChatService(t.Verify());

        Assert.Equal("not a member", (await svc.VotePollAsync(outsider.Id, poll.Id, 0, default)).Error);
        Assert.Equal("invalid_option", (await svc.VotePollAsync(member.Id, poll.Id, 2, default)).Error);
        Assert.Equal("invalid_option", (await svc.VotePollAsync(member.Id, poll.Id, -1, default)).Error);
        Assert.Equal("not_a_poll", (await svc.VotePollAsync(member.Id, text.Id, 0, default)).Error);
        Assert.Equal("message_not_found", (await svc.VotePollAsync(member.Id, Guid.NewGuid(), 0, default)).Error);
    }

    [Theory]
    [InlineData("group", "text", "hi there", false, "Beny Setiawan", false, "Beny Setiawan: hi there")]
    [InlineData("group", "text", "hi there", false, "Beny Setiawan", true, "You: hi there")]
    [InlineData("group", "system", "A added B", false, "Beny", false, "A added B")]
    [InlineData("dm", "text", "hi there", false, "Beny", false, "hi there")]
    [InlineData("group", "text", "x", true, "Beny", false, "This message was deleted")]
    [InlineData("group", "text", "[poll]{}", false, "Beny", false, "Beny: 📊 Poll")]
    [InlineData("group", "text", "hi", false, null, false, "hi")]
    public void RoomPreview_prefixes_group_sender(string roomType, string type, string content, bool deleted,
        string? sender, bool own, string expected)
    {
        Assert.Equal(expected, RoomPreview.Build(roomType, type, content, deleted, sender, own));
    }

    [Fact]
    public async Task SeenDetail_lists_members_who_have_not_read()
    {
        using var t = new TestDb();
        var (owner, member, outsider, roomId) = SeedGroup(t);
        t.Db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = outsider.Id, Role = "member", JoinedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        var msg = AddMessage(t, roomId, owner.Id, "hello team");
        var svc = new ChatService(t.Verify());

        await svc.RecordReadAsync(member.Id, msg.Id, default);
        var seen = await ((IChatService)svc).GetSeenDetailAsync(owner.Id, msg.Id, default);

        Assert.Equal([member.Id], seen.Readers.Select(r => r.UserId));
        Assert.NotNull(seen.Pending);
        Assert.Equal([outsider.Id], seen.Pending!.Select(p => p.UserId));
    }
}
