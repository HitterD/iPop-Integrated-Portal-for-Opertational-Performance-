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
    public async Task Duplicate_thread_reply_events_increment_the_parent_and_notify_subscribers_only_once()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        var parent = In(room, "A") with { ThreadReplyCount = 2 };
        chat.ActiveMessages.Add(parent);
        var reply = In(room, "B") with { ThreadParentId = parent.Id };
        var events = 0;
        chat.ThreadReplyReceived += _ => events++;
        var handler = typeof(ChatState).GetMethod("OnThreadReply",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        handler.Invoke(chat, [reply]);
        handler.Invoke(chat, [reply]);

        Assert.Equal(3, chat.ActiveMessages.Single(m => m.Id == parent.Id).ThreadReplyCount);
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Editing_or_deleting_the_last_message_updates_the_room_preview()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        await chat.SetChatPageMountedAsync(false);
        var older = In(room, "B") with { Content = "older", CreatedAt = DateTime.UtcNow.AddMinutes(-5) };
        var last = In(room, "B") with { Content = "typo" };
        chat.DirectRooms.Add(new RoomListDto(room, "dm", null, "B", "B", "typo",
            // The list's timestamp comes back from the database rounded to microseconds.
            new DateTime(last.CreatedAt.Ticks / 10 * 10, DateTimeKind.Utc),
            0, false, false, false, "on", false, false, null));
        chat.ActiveMessages.AddRange([older, last]);

        chat.OnMessageEdited(older with { Content = "older edited" });
        Assert.Equal("typo", chat.DirectRooms.Single().LastMessage);

        chat.OnMessageEdited(last with { Content = "fixed" });
        Assert.Equal("fixed", chat.DirectRooms.Single().LastMessage);

        chat.OnMessageDeleted(new DeletedDto(last.Id, "This message was deleted"));
        Assert.NotEqual("fixed", chat.DirectRooms.Single().LastMessage);
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

    [Fact]
    public async Task A_long_open_room_keeps_a_bounded_history_while_the_reader_is_at_the_bottom()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenAsync(room);
        using var _ = ctx;
        await chat.SetChatPageMountedAsync(false);

        for (var i = 0; i < ChatState.TrimAbove + 50; i++) chat.OnNewMessage(In(room, "B") with { Content = $"m{i}" });

        Assert.True(chat.ActiveMessages.Count <= ChatState.TrimAbove);
        Assert.Equal($"m{ChatState.TrimAbove + 49}", chat.ActiveMessages[^1].Content);   // newest always kept
        Assert.True(chat.HasOlderMessages);                                               // trimmed ones load back

        chat.ReaderAtBottom = false;                                                      // scrolled up: nothing moves
        var before = chat.ActiveMessages.Count;
        for (var i = 0; i < 200; i++) chat.OnNewMessage(In(room, "B") with { Content = $"x{i}" });
        Assert.Equal(before + 200, chat.ActiveMessages.Count);
    }
}
