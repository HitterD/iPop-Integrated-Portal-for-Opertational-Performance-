using IPop.Application.Chat;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// @all: 3 per 10 minutes per person per group (past that it is plain text), per-group switch for
/// ordinary members, owners/admins always allowed (but still capped), DMs never.
public class ChatMentionAllRulesTests
{
    [Theory]
    [InlineData("member", true, 0, true)]
    [InlineData("member", true, 2, true)]
    [InlineData("member", true, 3, false)]   // cap reached
    [InlineData("member", false, 0, false)]  // switched off for members
    [InlineData("admin", false, 0, true)]    // admins always may
    [InlineData("owner", false, 2, true)]
    [InlineData("owner", false, 3, false)]   // ...but the cap still applies
    [InlineData(null, true, 0, false)]       // not an active member
    public void Decision_table(string? role, bool allowMembers, int used, bool expected)
        => Assert.Equal(expected, ChatService.MayMentionAll(role, allowMembers, used));

    private static async Task<IPop.Application.Chat.MessageResultDto> SendAll(TestDb t, Guid sender, Guid room)
        => await t.NewChatService().SendMessageAsync(sender,
            new SendMessageRequest(room, "@[all](all) ping", Guid.NewGuid(), null, null), default);

    [Fact]
    public async Task Fourth_all_within_ten_minutes_is_plain_text_without_mass_mentions()
    {
        using var t = new TestDb();
        var a = t.AddUser("MA1"); var b = t.AddUser("MA2"); var c = t.AddUser("MA3");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "member"); t.AddMember(room.Id, b.Id); t.AddMember(room.Id, c.Id);

        for (var i = 0; i < ChatService.MentionAllLimit; i++)
            Assert.True((await SendAll(t, a.Id, room.Id)).Message!.MentionsAll);

        var fourth = await SendAll(t, a.Id, room.Id);
        Assert.True(fourth.Success);                       // still delivered...
        Assert.False(fourth.Message!.MentionsAll);         // ...as plain text
        Assert.Equal(0, await t.Verify().ChatMentions.CountAsync(m => m.MessageId == fourth.Message.Id));
        // Another person in the same group is not affected by a's cap.
        Assert.True((await SendAll(t, b.Id, room.Id)).Message!.MentionsAll);
    }

    [Fact]
    public async Task Old_all_messages_outside_the_window_do_not_count()
    {
        using var t = new TestDb();
        var a = t.AddUser("MA4"); var b = t.AddUser("MA5");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "member"); t.AddMember(room.Id, b.Id);
        for (var i = 0; i < 3; i++)
        {
            var m = t.AddMessage(room.Id, a.Id, "@[all](all) old", at: DateTime.UtcNow.AddMinutes(-11));
            t.Db.ChatMentions.Add(new IPop.Core.Entities.ChatMention
            { Id = Guid.NewGuid(), MessageId = m.Id, UserId = b.Id, IsAllMention = true, CreatedAt = DateTime.UtcNow.AddMinutes(-11) });
        }
        t.Db.SaveChanges();

        Assert.True((await SendAll(t, a.Id, room.Id)).Message!.MentionsAll);
    }

    [Fact]
    public async Task Editing_an_all_message_does_not_count_against_its_own_cap()
    {
        using var t = new TestDb();
        var a = t.AddUser("MA6"); var b = t.AddUser("MA7");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "member"); t.AddMember(room.Id, b.Id);
        Guid last = default;
        for (var i = 0; i < 3; i++) last = (await SendAll(t, a.Id, room.Id)).Message!.Id;

        var edit = await t.NewChatService().EditMessageAsync(a.Id, last, "@[all](all) edited", default);

        Assert.True(edit.Success);
        Assert.True(edit.Message!.MentionsAll);
    }

    [Fact]
    public async Task Only_admins_can_change_the_setting_and_dms_never_get_all()
    {
        using var t = new TestDb();
        var owner = t.AddUser("MA8"); var member = t.AddUser("MA9");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner"); t.AddMember(room.Id, member.Id);
        var dm = t.AddRoom("dm");
        t.AddMember(dm.Id, owner.Id); t.AddMember(dm.Id, member.Id);

        Assert.Equal("forbidden", (await t.NewChatService().SetMemberMentionAllAsync(member.Id, room.Id, false, default)).Error);
        Assert.True((await t.NewChatService().SetMemberMentionAllAsync(owner.Id, room.Id, false, default)).Success);
        Assert.False(t.Verify().ChatRooms.Single(r => r.Id == room.Id).AllowMemberMentionAll);
        Assert.Equal("not_a_group", (await t.NewChatService().SetMemberMentionAllAsync(owner.Id, dm.Id, false, default)).Error);
        Assert.False((await SendAll(t, owner.Id, dm.Id)).Message!.MentionsAll);
    }

    [Fact]
    public async Task Quota_counts_own_all_messages_and_says_when_the_cap_resets()
    {
        using var t = new TestDb();
        var a = t.AddUser("MB1"); var b = t.AddUser("MB2"); var outsider = t.AddUser("MB3");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id, "member"); t.AddMember(room.Id, b.Id, "owner");

        var q0 = await t.NewChatService().GetMentionAllQuotaAsync(a.Id, room.Id, default);
        Assert.Equal((true, 0, 3, 10), (q0!.Allowed, q0.Used, q0.Limit, q0.WindowMinutes));
        Assert.Null(q0.ResetsAt);

        for (var i = 0; i < 3; i++) await SendAll(t, a.Id, room.Id);
        var q3 = await t.NewChatService().GetMentionAllQuotaAsync(a.Id, room.Id, default);
        Assert.Equal(3, q3!.Used);
        Assert.NotNull(q3.ResetsAt);
        Assert.InRange(q3.ResetsAt!.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(10.1));
        // b's budget is untouched by a.
        Assert.Equal(0, (await t.NewChatService().GetMentionAllQuotaAsync(b.Id, room.Id, default))!.Used);

        await t.NewChatService().SetMemberMentionAllAsync(b.Id, room.Id, false, default);
        Assert.False((await t.NewChatService().GetMentionAllQuotaAsync(a.Id, room.Id, default))!.Allowed);
        Assert.True((await t.NewChatService().GetMentionAllQuotaAsync(b.Id, room.Id, default))!.Allowed);
        Assert.Null(await t.NewChatService().GetMentionAllQuotaAsync(outsider.Id, room.Id, default));
    }
}
