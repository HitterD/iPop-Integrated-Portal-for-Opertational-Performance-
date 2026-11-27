using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Xunit;

namespace IPop.Tests;

/// Edit keeps the reply quote and group read state; edit/delete refuse non-text and system lines.
public class ChatEditDeleteGuardTests
{
    [Fact]
    public async Task Edit_keeps_the_reply_quote_and_group_read_count()
    {
        using var t = new TestDb();
        var a = t.AddUser("EG1"); var b = t.AddUser("EG2"); var c = t.AddUser("EG3");
        var room = t.AddRoom("group", "R");
        t.AddMember(room.Id, a.Id, "owner"); t.AddMember(room.Id, b.Id); t.AddMember(room.Id, c.Id);
        var original = t.AddMessage(room.Id, b.Id, "original text");
        var m = t.AddMessage(room.Id, a.Id, "mine");
        t.Db.ChatMessages.Single(x => x.Id == m.Id).ReplyToId = original.Id;
        t.Db.ChatReadReceipts.Add(new ChatReadReceipt { MessageId = m.Id, UserId = b.Id, DeliveredAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow });
        t.Db.SaveChanges();

        var r = await new ChatService(t.Verify()).EditMessageAsync(a.Id, m.Id, "mine edited", default);

        Assert.True(r.Success);
        Assert.Equal("original text", r.Message!.ReplyPreview?.Content);
        Assert.Equal(1, r.Message.ReadCount);
        Assert.Equal(2, r.Message.ReadTotal);
        Assert.NotEqual("read", r.Message.DeliveryStatus);
    }

    [Theory]
    [InlineData("system", "A removed B")]
    [InlineData("sticker", "wave")]
    [InlineData("text", "[poll]{}")]
    [InlineData("text", "[note]{}")]
    public async Task Edit_refuses_system_sticker_poll_and_note_messages(string type, string content)
    {
        using var t = new TestDb();
        var a = t.AddUser("EG4"); var b = t.AddUser("EG5");
        var room = t.AddRoom("group", "R");
        t.AddMember(room.Id, a.Id, "owner"); t.AddMember(room.Id, b.Id);
        var m = t.AddMessage(room.Id, a.Id, content, type);

        var r = await new ChatService(t.Verify()).EditMessageAsync(a.Id, m.Id, "forged text", default);

        Assert.False(r.Success);
        Assert.Equal("not_editable", r.Error);
        Assert.Equal(content, t.Verify().ChatMessages.Single(x => x.Id == m.Id).Content);
    }

    [Fact]
    public async Task Sender_cannot_delete_their_own_system_line_but_admin_can()
    {
        using var t = new TestDb();
        var a = t.AddUser("EG6"); var b = t.AddUser("EG7");
        var room = t.AddRoom("group", "R");
        t.AddMember(room.Id, a.Id, "owner"); t.AddMember(room.Id, b.Id);
        var m = t.AddMessage(room.Id, a.Id, "A removed B", "system");

        var self = await new ChatService(t.Verify()).DeleteMessageAsync(a.Id, m.Id, isAdmin: false, default);
        var admin = await new ChatService(t.Verify()).DeleteMessageAsync(a.Id, m.Id, isAdmin: true, default);

        Assert.False(self.Success);
        Assert.True(admin.Success);
    }
}
