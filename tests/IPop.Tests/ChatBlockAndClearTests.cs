using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

public class ChatBlockAndClearTests
{
    [Fact]
    public async Task BlockUser_PreventsDirectMessagesFromSender()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("U1");
        var u2 = t.AddUser("U2");

        var svc1 = new ChatService(t.Verify());
        var svc2 = new ChatService(t.Verify());

        var dm = await svc1.CreateDirectRoomAsync(u1.Id, u2.Id, default);

        // u1 sends message -> success
        var m1 = await svc1.SendMessageAsync(u1.Id, new SendMessageRequest(dm.Id, "Hello", Guid.NewGuid(), null, null), default);
        Assert.True(m1.Success);

        // u2 blocks u1
        var blockOk = await svc2.BlockUserAsync(u2.Id, u1.Id, default);
        Assert.True(blockOk);

        var blockedList = await svc2.GetBlockedUserIdsAsync(u2.Id, default);
        Assert.Contains(u1.Id, blockedList);

        // u1 tries to send message -> fails with "blocked"
        var m2 = await svc1.SendMessageAsync(u1.Id, new SendMessageRequest(dm.Id, "Are you there?", Guid.NewGuid(), null, null), default);
        Assert.False(m2.Success);
        Assert.Equal("blocked", m2.Error);

        // u2 unblocks u1
        await svc2.UnblockUserAsync(u2.Id, u1.Id, default);

        // u1 can send again
        var m3 = await svc1.SendMessageAsync(u1.Id, new SendMessageRequest(dm.Id, "Unblocked now", Guid.NewGuid(), null, null), default);
        Assert.True(m3.Success);
    }

    [Fact]
    public async Task ClearRoomHistory_HidesMessagesPriorToClearedAtForCallerOnly()
    {
        using var t = new TestDb();
        var u1 = t.AddUser("U1");
        var u2 = t.AddUser("U2");

        var svc1 = new ChatService(t.Verify());
        var svc2 = new ChatService(t.Verify());

        var dm = await svc1.CreateDirectRoomAsync(u1.Id, u2.Id, default);

        await svc1.SendMessageAsync(u1.Id, new SendMessageRequest(dm.Id, "Message 1", Guid.NewGuid(), null, null), default);
        await Task.Delay(10);

        // u1 clears history
        var clearRes = await svc1.ClearRoomHistoryAsync(u1.Id, dm.Id, default);
        Assert.True(clearRes.Success);

        await Task.Delay(10);
        await svc2.SendMessageAsync(u2.Id, new SendMessageRequest(dm.Id, "Message 2 after clear", Guid.NewGuid(), null, null), default);

        // u1 only sees messages after ClearedAt
        var msgsForU1 = await svc1.GetMessagesAsync(dm.Id, u1.Id, 1, 20, default);
        Assert.Single(msgsForU1.Items);
        Assert.Equal("Message 2 after clear", msgsForU1.Items[0].Content);

        // u2 still sees all messages
        var msgsForU2 = await svc2.GetMessagesAsync(dm.Id, u2.Id, 1, 20, default);
        Assert.Equal(2, msgsForU2.Items.Count);
    }
}
