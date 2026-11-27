using IPop.Application.Chat;
using Xunit;

namespace IPop.Tests;

/// The reply counter is incremented in SQL, so replies that read the same stale parent both count.
public class ChatThreadCounterTests
{
    [Fact]
    public async Task Two_replies_from_separate_contexts_both_count()
    {
        using var t = new TestDb();
        var a = t.AddUser("TC1"); var b = t.AddUser("TC2");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, a.Id); t.AddMember(room.Id, b.Id);
        var parent = t.AddMessage(room.Id, a.Id, "parent");

        // Each service loads the parent (count 0) before either saves.
        var s1 = t.NewChatService(); var s2 = t.NewChatService();
        var r1 = await s1.SendThreadReplyAsync(a.Id, parent.Id, new SendMessageRequest(room.Id, "one", Guid.NewGuid(), null, null), default);
        var r2 = await s2.SendThreadReplyAsync(b.Id, parent.Id, new SendMessageRequest(room.Id, "two", Guid.NewGuid(), null, null), default);

        Assert.True(r1.Success); Assert.True(r2.Success);
        Assert.Equal(2, t.Verify().ChatMessages.Single(m => m.Id == parent.Id).ThreadReplyCount);
    }
}
