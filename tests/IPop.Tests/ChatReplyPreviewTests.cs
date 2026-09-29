using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

/// A reply must carry its quote in the send result itself. The sender's bubble is built from
/// that result (ack / broadcast echo); when the quote was missing it only appeared after a
/// reload, because the history query included the replied-to message and the send path did not.
public class ChatReplyPreviewTests
{
    private static (Guid Sender, Guid Other, Guid Room, Guid Original) Seed(TestDb t)
    {
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var roomId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        t.Db.ChatRooms.Add(new ChatRoom { Id = roomId, Type = "dm", IsActive = true, CreatedAt = now, LastMessageAt = now });
        t.Db.ChatRoomMembers.AddRange(
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = a.Id, Role = "member", JoinedAt = now },
            new ChatRoomMember { Id = Guid.NewGuid(), RoomId = roomId, UserId = b.Id, Role = "member", JoinedAt = now });
        var original = new ChatMessage
        {
            Id = Guid.NewGuid(), RoomId = roomId, SenderId = b.Id, Type = "text", Content = "ooo gitu siap",
            IdempotencyKey = Guid.NewGuid(), CreatedAt = now.AddSeconds(-30),
        };
        t.Db.ChatMessages.Add(original);
        t.Db.SaveChanges();
        return (a.Id, b.Id, roomId, original.Id);
    }

    [Fact]
    public async Task Send_reply_result_includes_the_quoted_message()
    {
        using var t = new TestDb();
        var (sender, other, room, original) = Seed(t);
        var svc = new ChatService(t.Verify());

        var res = await svc.SendMessageAsync(sender, new SendMessageRequest(room, "ok", Guid.NewGuid(), original, null), default);

        Assert.True(res.Success);
        Assert.Equal(original, res.Message!.ReplyToId);
        Assert.NotNull(res.Message.ReplyPreview);
        Assert.Equal("ooo gitu siap", res.Message.ReplyPreview!.Content);
        Assert.Equal(t.Db.Users.First(u => u.Id == other).Name, res.Message.ReplyPreview.SenderName);
    }

    [Fact]
    public async Task Resending_the_same_reply_still_returns_the_quote()
    {
        using var t = new TestDb();
        var (sender, _, room, original) = Seed(t);
        var key = Guid.NewGuid();

        var first = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "ok", key, original, null), default);
        // Client retry after a dropped ack: the idempotent path must return the same quote.
        var again = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "ok", key, original, null), default);

        Assert.Equal(first.Message!.Id, again.Message!.Id);
        Assert.Equal("ooo gitu siap", again.Message.ReplyPreview?.Content);
    }

    [Fact]
    public async Task Plain_message_has_no_quote()
    {
        using var t = new TestDb();
        var (sender, _, room, _) = Seed(t);

        var res = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "hi", Guid.NewGuid(), null, null), default);

        Assert.True(res.Success);
        Assert.Null(res.Message!.ReplyPreview);
    }

    [Fact]
    public async Task Thread_reply_result_includes_the_quoted_message()
    {
        using var t = new TestDb();
        var (sender, _, room, original) = Seed(t);
        var svc = new ChatService(t.Verify());

        var res = await svc.SendThreadReplyAsync(sender, original, new SendMessageRequest(room, "in thread", Guid.NewGuid(), original, null), default);

        Assert.True(res.Success);
        Assert.Equal("ooo gitu siap", res.Message!.ReplyPreview?.Content);
    }
}
