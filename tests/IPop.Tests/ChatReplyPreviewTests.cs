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
    public async Task A_resend_is_marked_as_a_replay_so_nobody_is_notified_twice()
    {
        using var t = new TestDb();
        var (sender, _, room, _) = Seed(t);
        var key = Guid.NewGuid();

        var first = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "hello", key, null, null), default);
        var again = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "hello", key, null, null), default);

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(first.Message!.Id, again.Message!.Id);
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

    [Fact]
    public async Task Reply_quote_shows_mentions_as_names_not_markup()
    {
        using var t = new TestDb();
        var (sender, other, room, original) = Seed(t);
        var msg = t.Db.ChatMessages.Single(m => m.Id == original);
        msg.Content = $"coba lagi @[ALPHIN SANTOSO HERMANTHAN, S.T.](u:{other}) ya";
        t.Db.SaveChanges();

        var res = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "ok", Guid.NewGuid(), original, null), default);

        Assert.Equal("coba lagi @Alphin Santoso Hermanthan, S.T. ya", res.Message!.ReplyPreview!.Content);
    }

    [Fact]
    public async Task Reply_quote_cut_never_leaves_half_a_mention()
    {
        using var t = new TestDb();
        var (sender, other, room, original) = Seed(t);
        var msg = t.Db.ChatMessages.Single(m => m.Id == original);
        msg.Content = new string('x', 140) + $" @[BUDI](u:{other}) " + new string('y', 50);
        t.Db.SaveChanges();

        var res = await new ChatService(t.Verify()).SendMessageAsync(sender, new SendMessageRequest(room, "ok", Guid.NewGuid(), original, null), default);

        Assert.DoesNotContain("(u:", res.Message!.ReplyPreview!.Content);
        Assert.DoesNotContain("@[", res.Message.ReplyPreview.Content);
    }

    [Fact]
    public void RoomPreview_and_ReplyQuote_format_attachment_and_voice_placeholders()
    {
        Assert.Equal("📷 Photo", RoomPreview.Build("dm", "text", "(attachment)", false, "Budi", false, attachmentMime: "image/png", attachmentCount: 1));
        Assert.Equal("You: 📷 3 photos", RoomPreview.Build("group", "text", "(attachment)", false, "Budi", true, attachmentMime: "image/jpeg", attachmentCount: 3));
        Assert.Equal("🎬 Video", RoomPreview.Build("dm", "text", "", false, "Budi", false, attachmentMime: "video/mp4", attachmentCount: 1));
        Assert.Equal("Budi: 📎 Attachment", RoomPreview.Build("group", "text", "(attachment)", false, "Budi", false, attachmentMime: "application/pdf", attachmentCount: 1));
        Assert.Equal("🎤 Voice message", RoomPreview.Build("dm", "text", "[voice]", false, "Budi", false, attachmentMime: "audio/webm", attachmentCount: 1));

        Assert.Equal("📷 Photo", ReplyQuote.Text("text", "(attachment)", false, 140, "image/webp", 1));
        Assert.Equal("🎬 2 videos", ReplyQuote.Text("text", "(attachment)", false, 140, "video/mp4", 2));
        Assert.Equal("📎 Attachment", ReplyQuote.Text("text", "(attachment)", false, 140, "application/pdf", 1));
        Assert.Equal("🎤 Voice message", ReplyQuote.Text("text", "[voice]", false, 140, "audio/ogg", 1));
    }

    [Fact]
    public async Task New_dm_room_has_null_LastMessageAt_until_first_message()
    {
        using var t = new TestDb();
        var a = t.AddUser("A");
        var b = t.AddUser("B");
        var svc = new ChatService(t.Verify());

        var createdRoom = await svc.CreateDirectRoomAsync(a.Id, b.Id, default);
        Assert.NotNull(createdRoom);

        var roomsBefore = await svc.GetRoomsAsync(a.Id, RoomTab.Direct, 1, 20, default);
        var rowBefore = Assert.Single(roomsBefore.Items);
        Assert.Null(rowBefore.LastMessage);
        Assert.Null(rowBefore.LastMessageAt);

        await svc.SendMessageAsync(a.Id, new SendMessageRequest(createdRoom.Id, "halo", Guid.NewGuid(), null, null), default);
        var roomsAfter = await svc.GetRoomsAsync(a.Id, RoomTab.Direct, 1, 20, default);
        var rowAfter = Assert.Single(roomsAfter.Items);
        Assert.Equal("halo", rowAfter.LastMessage);
        Assert.NotNull(rowAfter.LastMessageAt);
    }

    [Fact]
    public async Task Empty_message_without_attachments_is_rejected_while_attachment_only_succeeds()
    {
        using var t = new TestDb();
        var (sender, _, room, original) = Seed(t);
        var svc = new ChatService(t.Verify());

        var emptyText = await svc.SendMessageAsync(sender, new SendMessageRequest(room, "   ", Guid.NewGuid(), null, null), default);
        Assert.False(emptyText.Success);
        Assert.Equal("content_required", emptyText.Error);

        var emptyThread = await svc.SendThreadReplyAsync(sender, original, new SendMessageRequest(room, "", Guid.NewGuid(), null, null), default);
        Assert.False(emptyThread.Success);
        Assert.Equal("content_required", emptyThread.Error);

        var attId = Guid.NewGuid();
        t.Db.ChatAttachments.Add(new ChatAttachment
        {
            Id = attId, MessageId = null, FileName = "shot.png",
            MimeType = "image/png", FileSize = 1024, SynologyServer = "s1", SynologyPath = $"/chat/{room:D}/shot.png", CreatedAt = DateTime.UtcNow
        });
        t.Db.SaveChanges();

        var withAtt = await svc.SendMessageAsync(sender, new SendMessageRequest(room, "", Guid.NewGuid(), null, null, [attId]), default);
        Assert.True(withAtt.Success);

        var rooms = await svc.GetRoomsAsync(sender, RoomTab.Direct, 1, 20, default);
        Assert.Equal("📷 Photo", Assert.Single(rooms.Items).LastMessage);
    }

    [Fact]
    public async Task GetRoomAttachmentsAsync_returns_full_room_attachment_history_respecting_ClearedAt_and_membership()
    {
        using var t = new TestDb();
        var (sender, other, room, _) = Seed(t);
        var outsider = t.AddUser("OUT");
        var now = DateTime.UtcNow;

        var m1 = new ChatMessage { Id = Guid.NewGuid(), RoomId = room, SenderId = sender, Type = "text", Content = "(attachment)", IdempotencyKey = Guid.NewGuid(), CreatedAt = now.AddMinutes(-10) };
        var m2 = new ChatMessage { Id = Guid.NewGuid(), RoomId = room, SenderId = other, Type = "text", Content = "(attachment)", IdempotencyKey = Guid.NewGuid(), CreatedAt = now.AddMinutes(-1) };
        var a1 = new ChatAttachment { Id = Guid.NewGuid(), MessageId = m1.Id, FileName = "old.pdf", MimeType = "application/pdf", FileSize = 100, SynologyServer = "s1", SynologyPath = "/chat/old.pdf", CreatedAt = m1.CreatedAt };
        var a2 = new ChatAttachment { Id = Guid.NewGuid(), MessageId = m2.Id, FileName = "new.png", MimeType = "image/png", FileSize = 200, SynologyServer = "s1", SynologyPath = "/chat/new.png", CreatedAt = m2.CreatedAt };
        t.Db.ChatMessages.AddRange(m1, m2);
        t.Db.ChatAttachments.AddRange(a1, a2);
        t.Db.SaveChanges();

        var svc = new ChatService(t.Verify());
        var all = await svc.GetRoomAttachmentsAsync(room, sender, default);
        Assert.Equal([a2.Id, a1.Id], all.Select(x => x.Id));

        Assert.Empty(await svc.GetRoomAttachmentsAsync(room, outsider.Id, default));

        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = sender, RoomId = room, ClearedAt = now.AddMinutes(-5) });
        t.Db.SaveChanges();

        var afterClear = await svc.GetRoomAttachmentsAsync(room, sender, default);
        Assert.Equal([a2.Id], afterClear.Select(x => x.Id));
    }
}
