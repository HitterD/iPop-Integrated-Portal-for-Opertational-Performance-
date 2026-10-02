using IPop.Application.Chat;
using IPop.Core.Entities;
using IPop.Infrastructure.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Unpinning leaves a persisted "X unpinned a message" row, the same way pinning does. The row
/// comes back on the result so the endpoint can push it live on pinnedChanged. Repeats, no-ops
/// and late duplicates report Changed = false, so nothing is written or broadcast twice.
/// Permissions are unchanged.
public class ChatPinSystemMessageTests
{
    private static (Guid Owner, Guid Member, Guid Other, Guid Stranger, Guid RoomId, Guid Msg) SeedGroup(TestDb t)
    {
        var owner = t.AddUser("O"); var member = t.AddUser("M"); var other = t.AddUser("N"); var stranger = t.AddUser("X");
        var room = t.AddRoom("group", "G");
        t.AddMember(room.Id, owner.Id, "owner");
        t.AddMember(room.Id, member.Id, "member");
        t.AddMember(room.Id, other.Id, "member");
        var msg = new ChatMessage { Id = Guid.NewGuid(), RoomId = room.Id, SenderId = owner.Id, Type = "text", Content = "hello", IdempotencyKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        t.Db.ChatMessages.Add(msg);
        t.Db.SaveChanges();
        return (owner.Id, member.Id, other.Id, stranger.Id, room.Id, msg.Id);
    }

    private static List<string> SystemRows(TestDb t, Guid roomId) =>
        t.Verify().ChatMessages.Where(m => m.RoomId == roomId && m.Type == "system")
            .OrderBy(m => m.CreatedAt).Select(m => m.Content).ToList();

    [Fact]
    public async Task Unpin_persists_actor_system_row_and_returns_it()
    {
        using var t = new TestDb();
        var (_, member, _, _, roomId, msg) = SeedGroup(t);
        Assert.True((await new ChatService(t.Verify()).PinMessageAsync(member, msg, default)).Changed);

        var result = await new ChatService(t.Verify()).UnpinMessageAsync(member, msg, default);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal(roomId, result.RoomId);
        var sys = Assert.IsType<MessageDto>(result.SystemMessage);
        Assert.Equal("system", sys.Type);
        Assert.Equal("User M unpinned a message", sys.Content);
        Assert.Equal(roomId, sys.RoomId);
        Assert.Equal("User M", sys.SenderName);
        Assert.Equal(["User M pinned a message", "User M unpinned a message"], SystemRows(t, roomId));
        Assert.Empty(t.Verify().ChatPinnedMessages);
    }

    [Fact]
    public async Task Repeated_unpin_is_a_member_noop_without_second_row()
    {
        using var t = new TestDb();
        var (owner, member, other, stranger, roomId, msg) = SeedGroup(t);
        await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);
        await new ChatService(t.Verify()).UnpinMessageAsync(owner, msg, default);

        // A late unpin from another tab or another member succeeds but changes nothing.
        var again = await new ChatService(t.Verify()).UnpinMessageAsync(other, msg, default);
        Assert.True(again.Success);
        Assert.False(again.Changed);
        Assert.Null(again.SystemMessage);
        Assert.Equal(roomId, again.RoomId);

        // Outsiders and unknown messages still fail as before.
        Assert.Equal("forbidden", (await new ChatService(t.Verify()).UnpinMessageAsync(stranger, msg, default)).Error);
        Assert.Equal("message_not_found", (await new ChatService(t.Verify()).UnpinMessageAsync(member, Guid.NewGuid(), default)).Error);

        Assert.Equal(["User M pinned a message", "User O unpinned a message"], SystemRows(t, roomId));
    }

    [Fact]
    public async Task Unpin_of_never_pinned_message_is_a_noop()
    {
        using var t = new TestDb();
        var (_, member, _, _, roomId, msg) = SeedGroup(t);

        var result = await new ChatService(t.Verify()).UnpinMessageAsync(member, msg, default);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.Empty(SystemRows(t, roomId));
    }

    [Fact]
    public async Task Forbidden_unpin_writes_nothing_and_keeps_pin()
    {
        using var t = new TestDb();
        var (_, member, other, _, roomId, msg) = SeedGroup(t);
        await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);

        var byOther = await new ChatService(t.Verify()).UnpinMessageAsync(other, msg, default);

        Assert.False(byOther.Success);
        Assert.Equal("forbidden", byOther.Error);
        Assert.False(byOther.Changed);
        Assert.Single(t.Verify().ChatPinnedMessages);
        Assert.Equal(["User M pinned a message"], SystemRows(t, roomId));
    }

    [Fact]
    public async Task Dm_member_unpin_writes_row_with_unpinner_name()
    {
        using var t = new TestDb();
        var a = t.AddUser("A"); var b = t.AddUser("B");
        var room = t.AddRoom("dm");
        t.AddMember(room.Id, a.Id, "member");
        t.AddMember(room.Id, b.Id, "member");
        var msg = new ChatMessage { Id = Guid.NewGuid(), RoomId = room.Id, SenderId = a.Id, Type = "text", Content = "hi", IdempotencyKey = Guid.NewGuid(), CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
        t.Db.ChatMessages.Add(msg);
        t.Db.SaveChanges();
        await new ChatService(t.Verify()).PinMessageAsync(a.Id, msg.Id, default);

        var result = await new ChatService(t.Verify()).UnpinMessageAsync(b.Id, msg.Id, default);

        Assert.True(result.Changed);
        Assert.Equal("User B unpinned a message", result.SystemMessage!.Content);
    }

    [Fact]
    public async Task Two_unpins_of_the_same_pin_write_exactly_one_row()
    {
        using var t = new TestDb();
        var (owner, member, _, _, roomId, msg) = SeedGroup(t);
        await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);

        var r1 = await new ChatService(t.Verify()).UnpinMessageAsync(member, msg, default);
        var r2 = await new ChatService(t.Verify()).UnpinMessageAsync(owner, msg, default);

        Assert.True(r1.Success && r2.Success);
        Assert.Equal(1, new[] { r1, r2 }.Count(r => r.Changed));
        Assert.Single(SystemRows(t, roomId), c => c.EndsWith(" unpinned a message"));
    }

    [Fact]
    public async Task Repeat_pin_reports_no_change()
    {
        using var t = new TestDb();
        var (_, member, _, _, roomId, msg) = SeedGroup(t);
        var first = await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);
        var again = await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);

        Assert.True(first.Changed);
        Assert.Equal("User M pinned a message", first.SystemMessage!.Content);
        Assert.True(again.Success);
        Assert.False(again.Changed);
        Assert.Single(SystemRows(t, roomId));
    }

    [Fact]
    public async Task Unpin_row_is_the_preview_but_not_unread_for_other_members()
    {
        using var t = new TestDb();
        var (owner, member, _, _, roomId, msg) = SeedGroup(t);
        t.Db.ChatRoomUserSettings.Add(new ChatRoomUserSettings { UserId = owner, RoomId = roomId, LastReadMessageAt = DateTime.UtcNow });
        t.Db.SaveChanges();
        await new ChatService(t.Verify()).PinMessageAsync(member, msg, default);
        await new ChatService(t.Verify()).UnpinMessageAsync(member, msg, default);

        var rooms = await new ChatService(t.Verify()).GetRoomsAsync(owner, RoomTab.Groups, 1, 20, default);
        var room = Assert.Single(rooms.Items);
        Assert.Equal(0, room.UnreadCount);
        Assert.Equal("User M unpinned a message", room.LastMessage);
    }

    // ── Client: ChatState merges the row from the pin event ──

    private static MessageDto Sys(Guid roomId, string content, DateTime at) => new(Guid.NewGuid(), roomId, Guid.NewGuid(), "Alice", "A1",
        "system", content, at, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

    [Fact]
    public void MergeSystemMessage_inserts_in_time_order_and_dedupes()
    {
        var room = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var older = Sys(room, "older", now.AddMinutes(-2));
        var newer = Sys(room, "newer", now.AddMinutes(2));
        var list = new List<MessageDto> { older, newer };
        var unpin = Sys(room, "Alice unpinned a message", now);

        Assert.True(ChatState.MergeSystemMessage(list, unpin));
        Assert.False(ChatState.MergeSystemMessage(list, unpin)); // hub echo of this tab's own action
        Assert.Equal(["older", "Alice unpinned a message", "newer"], list.Select(m => m.Content));
    }

    [Fact]
    public void ApplyPinnedChange_raises_pinned_but_never_the_incoming_message_path()
    {
        using var ctx = new Bunit.BunitContext();
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        var chat = new ChatState(new AuthState(http, ctx.JSInterop.JSRuntime), http);
        var pinned = 0; var received = 0; var incoming = 0;
        chat.PinnedChanged += () => pinned++;
        chat.MessageReceived += _ => received++;
        chat.IncomingMessage += _ => incoming++;
        var room = Guid.NewGuid();
        var sys = Sys(room, "Alice unpinned a message", DateTime.UtcNow);

        chat.ApplyPinnedChange(new PinnedChangeDto(room, Guid.NewGuid(), false, "Alice", sys));
        chat.ApplyPinnedChange(new PinnedChangeDto(room, Guid.NewGuid(), false, "Alice")); // payload from an older server

        Assert.Equal(2, pinned);
        Assert.Equal(0, received); // room not open: nothing to append
        Assert.Equal(0, incoming); // never the banner/sound path
        Assert.Equal(0, chat.TotalUnread);
    }

    // ── Client: REST-response fallback used by Chat.Actions, PinnedPanel and the global drawer ──

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request });
    }

    private static async Task<(Bunit.BunitContext Ctx, ChatState Chat)> OpenChatAsync(Guid room)
    {
        var ctx = new Bunit.BunitContext();
        var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        var chat = new ChatState(new AuthState(http, ctx.JSInterop.JSRuntime), http);
        await chat.OpenRoomAsync(room);
        return (ctx, chat);
    }

    private static HttpResponseMessage PinResponse(PinnedResultDto body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = System.Net.Http.Json.JsonContent.Create(body) };

    [Theory]
    [InlineData(false)] // REST response first, hub echo second
    [InlineData(true)]  // hub echo first, REST response second
    public async Task Rest_fallback_and_hub_echo_show_the_row_once(bool hubFirst)
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenChatAsync(room);
        using var _ = ctx;
        var received = 0; var incoming = 0;
        chat.MessageReceived += _ => received++;
        chat.IncomingMessage += _ => incoming++;
        var msgId = Guid.NewGuid();
        var sys = Sys(room, "Alice unpinned a message", DateTime.UtcNow);
        var echo = new PinnedChangeDto(room, msgId, false, "Alice", sys);

        if (hubFirst) chat.ApplyPinnedChange(echo);
        var applied = await chat.ApplyPinResponseAsync(PinResponse(new PinnedResultDto(true, null, room, true, sys)), msgId, isPinned: false);
        if (!hubFirst) chat.ApplyPinnedChange(echo);

        Assert.True(applied);
        Assert.Single(chat.ActiveMessages, m => m.Id == sys.Id);
        Assert.Equal("Alice unpinned a message", chat.ActiveMessages.Single(m => m.Id == sys.Id).Content);
        Assert.Equal(1, received);
        Assert.Equal(0, incoming);
    }

    [Fact]
    public async Task Rest_fallback_ignores_noop_and_failed_responses()
    {
        var room = Guid.NewGuid();
        var (ctx, chat) = await OpenChatAsync(room);
        using var _ = ctx;
        var pinned = 0;
        chat.PinnedChanged += () => pinned++;

        var noop = await chat.ApplyPinResponseAsync(PinResponse(new PinnedResultDto(true, null, room)), Guid.NewGuid(), false);
        var forbidden = await chat.ApplyPinResponseAsync(
            PinResponse(new PinnedResultDto(false, "forbidden"), System.Net.HttpStatusCode.Forbidden), Guid.NewGuid(), false);
        var emptyBody = await chat.ApplyPinResponseAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK), Guid.NewGuid(), false);

        Assert.False(noop);
        Assert.False(forbidden);
        Assert.False(emptyBody);
        Assert.Equal(0, pinned); // callers fall back to their own refresh
        Assert.Empty(chat.ActiveMessages);
    }

    [Fact]
    public async Task Rest_fallback_for_another_room_does_not_touch_the_open_timeline()
    {
        var open = Guid.NewGuid();
        var (ctx, chat) = await OpenChatAsync(open);
        using var _ = ctx;
        var other = Guid.NewGuid();
        var sys = Sys(other, "Alice unpinned a message", DateTime.UtcNow);

        // Unpinned from the global drawer (or the user switched rooms mid-request).
        var applied = await chat.ApplyPinResponseAsync(PinResponse(new PinnedResultDto(true, null, other, true, sys)), Guid.NewGuid(), false);

        Assert.True(applied);
        Assert.Empty(chat.ActiveMessages);
    }
}
