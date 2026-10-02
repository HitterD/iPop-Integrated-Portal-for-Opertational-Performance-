using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Which message the open room's read receipt is sent for. Re-run on hub (re)connect, so a
/// room opened before the hub was up (fresh tab from a push notification) still ends up "read".
public class ChatMarkReadTests
{
    private static MessageDto Msg(string senderNik) => new(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), senderNik, senderNik,
        "text", "hi", DateTime.UtcNow, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

    [Fact]
    public void Picks_newest_message_from_someone_else()
    {
        var older = Msg("B");
        var newest = Msg("B");
        var list = new List<MessageDto> { older, newest, Msg("A") };

        Assert.Same(newest, ChatState.LatestReadable(list, "a", _ => false));
    }

    [Fact]
    public void Falls_back_to_own_newest_when_nobody_else_wrote()
    {
        var mine = Msg("A");
        var list = new List<MessageDto> { Msg("A"), mine };

        Assert.Same(mine, ChatState.LatestReadable(list, "A", _ => false));
    }

    [Fact]
    public void Skips_optimistic_bubbles_and_empty_rooms()
    {
        var confirmed = Msg("A");
        var pending = Msg("A");
        var list = new List<MessageDto> { confirmed, pending };

        Assert.Same(confirmed, ChatState.LatestReadable(list, "A", id => id == pending.Id));
        Assert.Null(ChatState.LatestReadable([], "A", _ => false));
    }
}
