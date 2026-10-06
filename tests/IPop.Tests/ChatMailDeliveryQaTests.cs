using IPop.Application.Chat;
using IPop.Application.Mail;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Infrastructure.Mail;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Chat / mail QA 2026-10-06: nobody is notified twice, blocks hold on every path, thread
/// mentions count, a late-added member starts with nothing unread, and a mail that reached the
/// inbox is never reported as failed.
public class ChatMailDeliveryQaTests
{
    private static Guid Room(TestDb t, string type, params User[] members)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        t.Db.ChatRooms.Add(new ChatRoom { Id = id, Type = type, Name = type == "group" ? "Ops" : null, IsActive = true, CreatedAt = now, LastMessageAt = now });
        for (var i = 0; i < members.Length; i++)
            t.Db.ChatRoomMembers.Add(new ChatRoomMember { Id = Guid.NewGuid(), RoomId = id, UserId = members[i].Id, Role = i == 0 ? "owner" : "member", JoinedAt = now });
        t.Db.SaveChanges();
        return id;
    }

    private static SendMessageRequest Msg(Guid room, string text, Guid? key = null) => new(room, text, key ?? Guid.NewGuid(), null, null);

    [Fact]
    public async Task Blocked_user_cannot_forward_or_thread_reply_into_the_dm()
    {
        using var t = new TestDb();
        var a = t.AddUser("BLK_A"); var b = t.AddUser("BLK_B");
        var dm = Room(t, "dm", a, b);
        var group = Room(t, "group", a, b);
        var inGroup = await new ChatService(t.Verify()).SendMessageAsync(b.Id, Msg(group, "from group"), default);
        var inDm = await new ChatService(t.Verify()).SendMessageAsync(b.Id, Msg(dm, "before block"), default);
        t.Db.UserBlocks.Add(new UserBlock { UserId = a.Id, BlockedUserId = b.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var fwd = await new ChatService(t.Verify()).ForwardMessageAsync(b.Id, inGroup.Message!.Id, dm, default);
        var thread = await new ChatService(t.Verify()).SendThreadReplyAsync(b.Id, inDm.Message!.Id, Msg(dm, "sneaky"), default);

        Assert.Equal("blocked", fwd.Error);
        Assert.Equal("blocked", thread.Error);
        await using var v = t.Verify();
        Assert.False(await v.ChatMessages.AnyAsync(m => m.RoomId == dm && m.Content == "sneaky"));
    }

    [Fact]
    public async Task A_mention_in_a_thread_is_recorded_for_the_mentioned_member()
    {
        using var t = new TestDb();
        var a = t.AddUser("TM_A"); var b = t.AddUser("TM_B"); var c = t.AddUser("TM_C");
        var group = Room(t, "group", a, b, c);
        var parent = await new ChatService(t.Verify()).SendMessageAsync(a.Id, Msg(group, "plan"), default);

        var reply = await new ChatService(t.Verify()).SendThreadReplyAsync(b.Id, parent.Message!.Id,
            Msg(group, $"@[TM C](u:{c.Id}) can you check?"), default);

        Assert.True(reply.Success);
        await using var v = t.Verify();
        var mention = Assert.Single(await v.ChatMentions.Where(m => m.MessageId == reply.Message!.Id).ToListAsync());
        Assert.Equal(c.Id, mention.UserId);
    }

    [Fact]
    public async Task A_thread_reply_resend_is_a_replay()
    {
        using var t = new TestDb();
        var a = t.AddUser("TR_A"); var b = t.AddUser("TR_B");
        var group = Room(t, "group", a, b);
        var parent = await new ChatService(t.Verify()).SendMessageAsync(a.Id, Msg(group, "plan"), default);
        var key = Guid.NewGuid();

        var first = await new ChatService(t.Verify()).SendThreadReplyAsync(b.Id, parent.Message!.Id, Msg(group, "ok", key), default);
        var again = await new ChatService(t.Verify()).SendThreadReplyAsync(b.Id, parent.Message!.Id, Msg(group, "ok", key), default);

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
    }

    [Fact]
    public async Task Someone_added_to_an_old_group_starts_with_nothing_unread()
    {
        using var t = new TestDb();
        var owner = t.AddUser("UN_O"); var old = t.AddUser("UN_M"); var late = t.AddUser("UN_L");
        var group = Room(t, "group", owner, old);
        for (var i = 0; i < 5; i++) await new ChatService(t.Verify()).SendMessageAsync(old.Id, Msg(group, "history " + i), default);

        var added = await new ChatService(t.Verify()).AddMemberAsync(owner.Id, group, late.Id, default);
        Assert.True(added.Success);
        await Task.Delay(20);
        await new ChatService(t.Verify()).SendMessageAsync(old.Id, Msg(group, "welcome"), default);

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(late.Id, RoomTab.Groups, 1, 50, default);
        Assert.Equal(1, rooms.Items.Single(r => r.Id == group).UnreadCount);
    }

    [Fact]
    public async Task Two_rules_with_the_same_label_do_not_fail_a_delivered_mail()
    {
        using var t = new TestDb();
        var sender = t.AddUser("RL_S"); var recipient = t.AddUser("RL_R");
        var label = new MailLabel { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "Boss", Color = "#000", CreatedAt = DateTime.UtcNow };
        t.Db.MailLabels.Add(label);
        t.Db.MailRules.AddRange(
            new MailRule { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "by name", SenderContains = "RL_S", LabelId = label.Id, IsEnabled = true, CreatedAt = DateTime.UtcNow },
            new MailRule { Id = Guid.NewGuid(), UserId = recipient.Id, Name = "by nik", SenderContains = sender.Nik, LabelId = label.Id, IsEnabled = true, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var res = await new MailService(t.Verify()).SendAsync(sender.Id, new ComposeMailRequest("Budget", "<p>x</p>", [recipient.Nik], [], []), default);

        Assert.True(res.Success);
        await using var v = t.Verify();
        Assert.Equal(1, await v.MailLabelAssignments.CountAsync(a => a.LabelId == label.Id));
        Assert.Equal(1, await v.MailRecipients.CountAsync(r => r.UserId == recipient.Id && r.Folder == "inbox"));
    }

    [Fact]
    public async Task An_out_of_office_reply_is_delivered_and_notified_like_any_mail()
    {
        using var t = new TestDb();
        var sender = t.AddUser("OOO_S"); var away = t.AddUser("OOO_R");
        t.Db.MailAutoReplySettings.Add(new MailAutoReplySetting { Id = Guid.NewGuid(), UserId = away.Id, IsEnabled = true, Message = "Away", UpdatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        await new MailService(t.Verify()).SendAsync(sender.Id, new ComposeMailRequest("Question", "<p>x</p>", [away.Nik], [], []), default);

        await using var v = t.Verify();
        var reply = await v.Mails.SingleAsync(m => m.Signature == "auto-reply");
        var row = await v.MailRecipients.SingleAsync(r => r.MailId == reply.Id && r.UserId == sender.Id);
        Assert.NotNull(row.DeliveredAt);
        Assert.True(await v.MailNotifyJobs.AnyAsync(j => j.MailId == reply.Id));
    }
}
