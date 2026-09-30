using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// "Hapus chat" from the room-list menu: delete for me (hide + clear) and group delete for everyone.
public class ChatDeleteRoomTests
{
    private static SendMessageRequest Msg(Guid roomId, string text) => new(roomId, text, Guid.NewGuid(), null, null);

    [Fact]
    public async Task DeleteForMe_HidesDm_ClearsHistory_AndItReturnsOnNewMessage()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("U1");
        var u2 = t.AddUser("U2");
        var svc = new ChatService(t.Verify());
        var dm = await svc.CreateDirectRoomAsync(u1.Id, u2.Id, default);
        await svc.SendMessageAsync(u2.Id, Msg(dm.Id, "lama"), default);
        await Task.Delay(10);

        var res = await new ChatService(t.Verify()).DeleteRoomForMeAsync(u1.Id, dm.Id, default);
        Assert.True(res.Success);

        Assert.DoesNotContain((await new ChatService(t.Verify()).GetRoomsAsync(u1.Id, RoomTab.Direct, 1, 20, default)).Items, r => r.Id == dm.Id);
        // The other person is untouched.
        Assert.Contains((await new ChatService(t.Verify()).GetRoomsAsync(u2.Id, RoomTab.Direct, 1, 20, default)).Items, r => r.Id == dm.Id);
        Assert.Single((await new ChatService(t.Verify()).GetMessagesAsync(dm.Id, u2.Id, 1, 20, default)).Items);

        await Task.Delay(10);
        await new ChatService(t.Verify()).SendMessageAsync(u2.Id, Msg(dm.Id, "baru"), default);

        var rooms = (await new ChatService(t.Verify()).GetRoomsAsync(u1.Id, RoomTab.Direct, 1, 20, default)).Items;
        Assert.Contains(rooms, r => r.Id == dm.Id);
        var mine = await new ChatService(t.Verify()).GetMessagesAsync(dm.Id, u1.Id, 1, 20, default);
        Assert.Equal("baru", Assert.Single(mine.Items).Content);
    }

    [Fact]
    public async Task DeleteForMe_ReopeningTheDmBringsItBack()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("U1");
        var u2 = t.AddUser("U2");
        var dm = await new ChatService(t.Verify()).CreateDirectRoomAsync(u1.Id, u2.Id, default);
        await new ChatService(t.Verify()).SendMessageAsync(u2.Id, Msg(dm.Id, "hi"), default);
        await Task.Delay(10);
        await new ChatService(t.Verify()).DeleteRoomForMeAsync(u1.Id, dm.Id, default);

        var again = await new ChatService(t.Verify()).CreateDirectRoomAsync(u1.Id, u2.Id, default);

        Assert.Equal(dm.Id, again.Id);
        Assert.Contains((await new ChatService(t.Verify()).GetRoomsAsync(u1.Id, RoomTab.Direct, 1, 20, default)).Items, r => r.Id == dm.Id);
        // History stays cleared.
        Assert.Empty((await new ChatService(t.Verify()).GetMessagesAsync(dm.Id, u1.Id, 1, 20, default)).Items);
    }

    [Fact]
    public async Task DeleteForMe_RequiresMembership()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("U1");
        var u2 = t.AddUser("U2");
        var outsider = t.AddUser("U3");
        var dm = await new ChatService(t.Verify()).CreateDirectRoomAsync(u1.Id, u2.Id, default);

        var res = await new ChatService(t.Verify()).DeleteRoomForMeAsync(outsider.Id, dm.Id, default);

        Assert.False(res.Success);
        Assert.Equal("forbidden", res.Error);
    }

    [Fact]
    public async Task DeleteForEveryone_RejectsMembersDmsProjectRoomsAndWrongName()
    {
        using var t = new TestDb();
        var owner = t.AddUser("U1");
        var member = t.AddUser("U2");
        var third = t.AddUser("U4");
        var group = await new ChatService(t.Verify()).CreateGroupRoomAsync(owner.Id, new CreateGroupRequest("Tim QA", [member.Id, third.Id]), default);
        var dm = await new ChatService(t.Verify()).CreateDirectRoomAsync(owner.Id, member.Id, default);

        Assert.Equal("forbidden", (await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(member.Id, group.Id, "Tim QA", default)).Error);
        Assert.Equal("not_a_group", (await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(owner.Id, dm.Id, null, default)).Error);
        Assert.Equal("name_mismatch", (await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(owner.Id, group.Id, "Tim", default)).Error);
        Assert.Equal("room_not_found", (await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(owner.Id, Guid.NewGuid(), "x", default)).Error);

        var project = t.AddProject("Proyek", owner.Id);
        t.Db.ProjectRooms.Add(new ProjectRoom { ProjectId = project.Id, RoomId = group.Id, CreatedAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        Assert.Equal("project_room", (await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(owner.Id, group.Id, "Tim QA", default)).Error);

        Assert.True(await t.Verify().ChatRooms.AnyAsync(r => r.Id == group.Id));
    }

    [Fact]
    public async Task DeleteForEveryone_RemovesGroupThreadsAndReportsOnlyUnsharedFiles()
    {
        using var t = new TestDb();
        var owner = t.AddUser("U1");
        var member = t.AddUser("U2");
        var third = t.AddUser("U4");
        var group = await new ChatService(t.Verify()).CreateGroupRoomAsync(owner.Id, new CreateGroupRequest("Tim QA", [member.Id, third.Id]), default);
        var other = await new ChatService(t.Verify()).CreateDirectRoomAsync(owner.Id, member.Id, default);

        var parent = await new ChatService(t.Verify()).SendMessageAsync(owner.Id, Msg(group.Id, "induk"), default);
        var reply = await new ChatService(t.Verify()).SendThreadReplyAsync(member.Id, parent.Message!.Id, Msg(group.Id, "balas"), default);
        Assert.True(reply.Success);

        // One file only in this group, one forwarded (same stored file) into another room.
        var onlyHere = t.AddMessage(group.Id, owner.Id, "file", "file");
        var shared = t.AddMessage(group.Id, owner.Id, "file", "file");
        var forwarded = t.AddMessage(other.Id, owner.Id, "file", "file");
        ChatAttachment Att(Guid msgId, string path, string? thumb = null) => new()
        {
            Id = Guid.NewGuid(), MessageId = msgId, FileName = "f", MimeType = "image/png",
            SynologyServer = "local", SynologyPath = path, ThumbnailPath = thumb, CreatedAt = DateTime.UtcNow,
        };
        t.Db.ChatAttachments.AddRange(Att(onlyHere.Id, "a.png", "a_thumb.png"), Att(shared.Id, "s.png"), Att(forwarded.Id, "s.png"));
        t.Db.SaveChanges();

        var res = await new ChatService(t.Verify()).DeleteGroupForEveryoneAsync(owner.Id, group.Id, "  tim qa ", default);

        Assert.True(res.Success, res.Error);
        Assert.Equal(new[] { owner.Id, member.Id, third.Id }.OrderBy(x => x), res.MemberIds.OrderBy(x => x));
        Assert.Equal(new HashSet<string> { "a.png", "a_thumb.png" }, res.OrphanFiles.Select(f => f.Path).ToHashSet());

        var db = t.Verify();
        Assert.False(await db.ChatRooms.AnyAsync(r => r.Id == group.Id));
        Assert.False(await db.ChatMessages.AnyAsync(m => m.RoomId == group.Id));
        Assert.True(await db.ChatAttachments.AnyAsync(a => a.MessageId == forwarded.Id));
    }
}
