using IPop.Notifier.Core;
using Xunit;

namespace IPop.Tests;

/// The tray's Needs action count follows the Inbox (ui/inbox-model.js): unread threads, one per conversation.
public class NotifierInboxThreadsTests
{
    private const string Room = "11111111-1111-1111-1111-111111111111";
    private const string Ticket = "22222222-2222-2222-2222-222222222222";
    private const string Project = "33333333-3333-3333-3333-333333333333";
    private const string Task = "44444444-4444-4444-4444-444444444444";

    [Theory]
    [InlineData("/chat/" + Room, "chat:" + Room)]
    [InlineData("/tickets/" + Ticket, "ticket:" + Ticket)]
    [InlineData("/idesk/tickets/" + Ticket + "#update", "ticket:" + Ticket)]
    [InlineData("/projects/" + Project + "?task=" + Task, "task:" + Task)]
    [InlineData("/projects/" + Project, null)]
    [InlineData("/mail?mailId=1", null)]
    [InlineData(null, null)]
    public void Thread_keys_match_the_inbox(string? url, string? key) => Assert.Equal(key, InboxThreads.ThreadKey(url));

    [Fact]
    public void Three_messages_from_a_leader_in_one_chat_count_once()
    {
        var unread = new (Guid, string?, bool)[]
        {
            (Guid.NewGuid(), "/chat/" + Room, true),
            (Guid.NewGuid(), "/chat/" + Room, true),
            (Guid.NewGuid(), "/chat/" + Room, false),
            (Guid.NewGuid(), "/spp/1", true),
            (Guid.NewGuid(), "/spp/2", true),
            (Guid.NewGuid(), "/news/x", false),
        };
        Assert.Equal(3, InboxThreads.CountNeedsAction(unread));
    }

    [Fact]
    public void Nothing_unread_in_the_act_tier_counts_zero() =>
        Assert.Equal(0, InboxThreads.CountNeedsAction([(Guid.NewGuid(), "/chat/" + Room, false)]));
}
