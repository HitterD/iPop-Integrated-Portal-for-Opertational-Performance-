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
    // ── Only an attended Chat page marks read; your own receipt never flips your bubbles ──

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }

    private static readonly Guid MyId = Guid.NewGuid();

    private static async Task<(Bunit.BunitContext Ctx, ChatState Chat)> OpenAsync(Guid room)
    {
        var ctx = new Bunit.BunitContext();
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
            new DirectoryEmployeeDto("A", "Me", null, null, null, null, null, null, "a@x", null, null, "on", UserId: MyId));
        var chat = new ChatState(auth, http);
        await chat.OpenRoomAsync(room);
        return (ctx, chat);
    }

    private static MessageDto In(Guid room, string senderNik) => Msg(senderNik) with { RoomId = room };

    [Theory]
    [InlineData(false, true, false)] // left the Chat page: Mail/Dashboard must not read the last room
    [InlineData(true, false, false)] // background tab / minimized window
    [InlineData(true, true, true)]
    public async Task Open_room_counts_as_seen_only_while_the_page_is_up_and_attended(bool mounted, bool attentive, bool seen)
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        await chat.SetChatPageMountedAsync(mounted);
        await chat.SetAttentiveAsync(attentive);
        IncomingChatMessage? incoming = null;
        chat.IncomingMessage += m => incoming = m;

        chat.OnNewMessage(In(room, "B"));

        Assert.Equal(mounted && attentive, chat.CanMarkRead);
        Assert.NotNull(incoming);
        Assert.Equal(seen, incoming!.IsActiveRoom); // unseen → banner + unread like any other room
    }

    [Fact]
    public async Task Duplicate_live_message_only_increments_unread_and_notifies_once()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        await chat.SetChatPageMountedAsync(false);
        chat.DirectRooms.Add(new RoomListDto(room, "dm", null, "B", "B", null, null,
            0, false, false, false, "on", false, false, null));
        var notifications = 0;
        chat.IncomingMessage += _ => notifications++;
        var message = In(room, "B");

        chat.OnNewMessage(message);
        chat.OnNewMessage(message);

        Assert.Equal(1, chat.DirectRooms.Single().UnreadCount);
        Assert.Equal(1, notifications);
        Assert.Single(chat.ActiveMessages.Where(m => m.Id == message.Id));
    }

    [Fact]
    public async Task Own_seen_update_never_marks_own_messages_read()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        var mine = In(room, "A") with { DeliveryStatus = "sent" };
        chat.ActiveMessages.Add(mine);

        chat.OnSeenUpdate(new SeenUpdateDto(mine.Id, new SeenByDto(MyId, "Me", null, null, DateTime.UtcNow), 1, 2));
        Assert.Equal("sent", chat.ActiveMessages.Single().DeliveryStatus);

        chat.OnSeenUpdate(new SeenUpdateDto(mine.Id, new SeenByDto(Guid.NewGuid(), "B", null, null, DateTime.UtcNow), 1, 2));
        Assert.NotEqual("sent", chat.ActiveMessages.Single().DeliveryStatus);
    }
}
