using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using IPop.Application.Chat;
using IPop.Web.Services;
using Xunit;

namespace IPop.Tests;

/// Room list / room open / history paging must say when a read failed instead of leaving a
/// skeleton up or showing a false "empty". Failures keep what is already on screen, never move
/// the paging cursor, and 403/404 drop cached data for a room the user can no longer see.
/// Stale answers (a superseded open or retry) never paint over the current room.
public class ChatRoomLoadingTests
{
    // ── Scripted HTTP: first matching rule answers; a rule can hold its response on a gate ──

    private sealed class Script : HttpMessageHandler
    {
        private readonly List<(string Fragment, Func<HttpRequestMessage, Task<HttpResponseMessage>> Answer)> _rules = [];
        public ConcurrentQueue<string> Requests { get; } = new();

        public Script On(string fragment, Func<HttpResponseMessage> answer)
        {
            _rules.Insert(0, (fragment, _ => Task.FromResult(answer())));
            return this;
        }

        public Script OnGated(string fragment, TaskCompletionSource gate, Func<HttpResponseMessage> answer)
        {
            _rules.Insert(0, (fragment, async _ => { await gate.Task; return answer(); }));
            return this;
        }

        public Script Throw(string fragment)
        {
            _rules.Insert(0, (fragment, _ => throw new HttpRequestException("network down")));
            return this;
        }

        public int Count(string fragment) => Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Requests.Enqueue(url);
            // Most recently added matching rule wins. A fragment ending in '$' must match the
            // whole path+query (the room URL is a prefix of its messages URL).
            foreach (var (fragment, answer) in _rules)
            {
                var hit = fragment.EndsWith('$')
                    ? url == fragment[..^1]
                    : url.Contains(fragment, StringComparison.Ordinal);
                if (hit) return answer(request);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static Func<HttpResponseMessage> Status(HttpStatusCode code) => () => new HttpResponseMessage(code);

    private static MessageDto Msg(Guid roomId, string content, DateTime at) => new(Guid.NewGuid(), roomId, Guid.NewGuid(), "Alice", "A1",
        "text", content, at, null, false, null, null, null, null, 0, null, [], [], [], false, Guid.NewGuid());

    private static ChatRoomDto Room(Guid id) => new(id, "dm", "Room", null, 2, 0, [], "member", []);

    private static RoomListDto Row(Guid id, string type = "dm") => new(id, type, "Room", null, null, null, null,
        0, false, false, false, null, false, false, null);

    private static PagedResult<MessageDto> Page(params MessageDto[] items) => new(items, items.Length, 1, 50);

    private sealed class Rig : IDisposable
    {
        public Bunit.BunitContext Ctx { get; } = new();
        public Script Http { get; } = new();
        public AuthState Auth { get; }
        public ChatState Chat { get; }
        public Rig()
        {
            Ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
            var http = new HttpClient(Http) { BaseAddress = new Uri("http://localhost/") };
            Auth = new AuthState(http, Ctx.JSInterop.JSRuntime);
            Chat = new ChatState(Auth, http, new DraftStore(Ctx.JSInterop.JSRuntime));
        }
        public void Dispose() => Ctx.Dispose();
    }

    private static string RoomUrl(Guid id) => $"/api/chat/rooms/{id}$";
    private static string Page1Url(Guid id) => $"/api/chat/rooms/{id}/messages?page=1";

    // ── AuthState.GetResultAsync ──

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, GetStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, GetStatus.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, GetStatus.Failed)]
    [InlineData(HttpStatusCode.BadRequest, GetStatus.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GetStatus.Unreachable)]
    [InlineData(HttpStatusCode.BadGateway, GetStatus.Unreachable)]
    [InlineData(HttpStatusCode.NoContent, GetStatus.Empty)]
    public async Task GetResult_classifies_status_without_stale_fallback(HttpStatusCode code, GetStatus expected)
    {
        using var rig = new Rig();
        var failed = 0;
        rig.Auth.RequestFailed += _ => failed++;
        rig.Http.On("/api/x", () => Json(new[] { 1, 2 }));
        Assert.NotNull(await rig.Auth.GetAsync<int[]>("/api/x")); // primes last-good

        rig.Http.On("/api/x", Status(code));
        var r = await rig.Auth.GetResultAsync<int[]>("/api/x");

        Assert.Equal(expected, r.Status);
        Assert.Null(r.Value);               // never the stale cached answer
        Assert.Equal(code, r.Code);
        Assert.Equal(0, failed);            // the caller owns the error UI, not the shell bar
    }

    private static Func<HttpResponseMessage> Html() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("<html><body>Proxy error</body></html>", System.Text.Encoding.UTF8, "text/html"),
    };

    [Fact]
    public async Task GetResult_reports_an_unreadable_200_as_failed_while_GetAsync_still_throws()
    {
        using var rig = new Rig();
        rig.Http.On("/api/x", Html());

        var r = await rig.Auth.GetResultAsync<int[]>("/api/x");
        Assert.Equal(GetStatus.Failed, r.Status);
        Assert.Equal(HttpStatusCode.OK, r.Code);

        // Unchanged legacy contract for every other module.
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Auth.GetAsync<int[]>("/api/x"));
    }

    [Fact]
    public async Task Unreadable_page_one_is_a_retryable_error_not_a_crash()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        rig.Http.On(Page1Url(room), Html());

        await rig.Chat.OpenRoomAsync(room);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
        Assert.False(rig.Chat.IsLoadingMessages);

        var m = Msg(room, "ok", DateTime.UtcNow);
        rig.Http.On(Page1Url(room), () => Json(Page(m)));
        await rig.Chat.RetryActiveRoomAsync();
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
        Assert.Equal(m.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
    }

    [Fact]
    public async Task Background_details_refresh_failure_shows_a_banner_and_keeps_cached_details()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        rig.Http.On(Page1Url(room), () => Json(Page(Msg(room, "a", DateTime.UtcNow))));
        await rig.Chat.OpenRoomAsync(room);

        // roomUpdated / memberChanged refresh fails.
        rig.Http.On(RoomUrl(room), Status(HttpStatusCode.InternalServerError));
        await rig.Chat.LoadActiveRoomAsync();
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
        Assert.Equal(room, rig.Chat.ActiveRoom?.Id);           // cached details stay
        Assert.Single(rig.Chat.ActiveMessages);

        // A page-1 success (reconnect) must not hide the details failure…
        await rig.Chat.LoadMessagesAsync(1);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);

        // …only the details succeeding again clears it.
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        await rig.Chat.LoadActiveRoomAsync();
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
    }

    [Fact]
    public async Task Details_success_does_not_hide_a_parallel_history_failure()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        rig.Http.On(Page1Url(room), Status(HttpStatusCode.InternalServerError));
        rig.Http.OnGated(RoomUrl(room), gate, () => Json(Room(room)));  // details land last

        var open = rig.Chat.OpenRoomAsync(room);
        gate.SetResult();
        await open;

        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
    }

    [Fact]
    public async Task GetResult_reports_network_failure_as_unreachable()
    {
        using var rig = new Rig();
        rig.Http.Throw("/api/x");
        var r = await rig.Auth.GetResultAsync<int[]>("/api/x");
        Assert.Equal(GetStatus.Unreachable, r.Status);
        Assert.Null(r.Code);
    }

    [Fact]
    public async Task GetAsync_keeps_its_old_semantics()
    {
        using var rig = new Rig();
        var failed = new List<string>();
        rig.Auth.RequestFailed += failed.Add;
        rig.Http.On("/api/x", () => Json(new[] { 7 }));
        Assert.Equal(7, Assert.Single((await rig.Auth.GetAsync<int[]>("/api/x"))!));

        rig.Http.On("/api/x", Status(HttpStatusCode.ServiceUnavailable));
        Assert.Equal(7, Assert.Single((await rig.Auth.GetAsync<int[]>("/api/x"))!)); // 503: last good + shell bar
        Assert.Single(failed);

        rig.Http.Throw("/api/x");
        Assert.Equal(7, Assert.Single((await rig.Auth.GetAsync<int[]>("/api/x"))!)); // network: last good + shell bar
        Assert.Equal(2, failed.Count);

        rig.Http.On("/api/x", Status(HttpStatusCode.InternalServerError));
        Assert.Null(await rig.Auth.GetAsync<int[]>("/api/x"));       // 500: null, no stale, no bar
        rig.Http.On("/api/x", Status(HttpStatusCode.NotFound));
        Assert.Null(await rig.Auth.GetAsync<int[]>("/api/x"));
        Assert.Equal(2, failed.Count);
    }

    // ── Room list ──

    [Fact]
    public async Task Failed_first_room_list_shows_error_not_empty_and_retry_recovers()
    {
        using var rig = new Rig();
        rig.Http.On("/api/chat/rooms?tab=", Status(HttpStatusCode.InternalServerError));

        await rig.Chat.LoadRoomsAsync();

        Assert.False(rig.Chat.RoomsLoaded);          // not "No conversations yet"
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomsError);

        var id = Guid.NewGuid();
        rig.Http.On("/api/chat/rooms?tab=direct", () => Json(new PagedResult<RoomListDto>([Row(id)], 1, 1, 50)));
        rig.Http.On("/api/chat/rooms?tab=groups", () => Json(new PagedResult<RoomListDto>([], 0, 1, 50)));
        await rig.Chat.RetryRoomsAsync();

        Assert.True(rig.Chat.RoomsLoaded);
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomsError);
        Assert.Equal(id, Assert.Single(rig.Chat.DirectRooms).Id);
    }

    [Fact]
    public async Task Room_list_failure_after_success_keeps_the_rooms_on_screen()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        rig.Http.On("/api/chat/rooms?tab=direct", () => Json(new PagedResult<RoomListDto>([Row(id)], 120, 1, 50)));
        rig.Http.On("/api/chat/rooms?tab=groups", () => Json(new PagedResult<RoomListDto>([], 0, 1, 50)));
        await rig.Chat.LoadRoomsAsync();

        rig.Http.On("/api/chat/rooms?tab=direct", Status(HttpStatusCode.ServiceUnavailable));
        await rig.Chat.LoadRoomsAsync();

        Assert.Equal(id, Assert.Single(rig.Chat.DirectRooms).Id);
        Assert.True(rig.Chat.RoomsLoaded);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomsError);

        // A failed "more" page does not advance the page counter: the next try asks for page 2 again.
        await rig.Chat.LoadMoreDirectRoomsAsync();
        await rig.Chat.LoadMoreDirectRoomsAsync();
        Assert.Equal(2, rig.Http.Count("tab=direct&page=2"));
        Assert.Equal(0, rig.Http.Count("tab=direct&page=3"));
    }

    [Fact]
    public async Task Failed_room_search_keeps_previous_results()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        rig.Http.On("/api/chat/rooms/search", () => Json(new PagedResult<RoomListDto>([Row(id)], 1, 1, 50)));
        await rig.Chat.SearchRoomsAsync("ali");
        rig.Http.On("/api/chat/rooms/search", Status(HttpStatusCode.InternalServerError));
        await rig.Chat.SearchRoomsAsync("alic");

        Assert.Equal(id, Assert.Single(rig.Chat.DirectRooms).Id);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomsError);
    }

    // ── Opening a room ──

    [Fact]
    public async Task Failed_uncached_open_ends_the_skeleton_with_an_error_and_retry_reloads_the_same_room()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        rig.Http.On(Page1Url(room), Status(HttpStatusCode.InternalServerError));

        await rig.Chat.OpenRoomAsync(room);

        Assert.False(rig.Chat.IsLoadingMessages);    // no endless skeleton
        Assert.Empty(rig.Chat.ActiveMessages);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);

        // OpenRoomAsync ignores the room that is already open, so it cannot be the retry.
        await rig.Chat.OpenRoomAsync(room);
        Assert.Equal(1, rig.Http.Count(Page1Url(room)));

        var m = Msg(room, "hi", DateTime.UtcNow);
        rig.Http.On(Page1Url(room), () => Json(Page(m)));
        await rig.Chat.RetryActiveRoomAsync();

        Assert.Equal(2, rig.Http.Count(Page1Url(room)));
        Assert.Equal(room, rig.Chat.ActiveRoomId);
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
        Assert.Equal(m.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
        Assert.False(rig.Chat.IsLoadingMessages);
    }

    [Fact]
    public async Task Failed_room_details_on_uncached_open_is_an_error_too()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), Status(HttpStatusCode.BadGateway));
        rig.Http.On(Page1Url(room), () => Json(Page()));       // more specific: wins for page 1

        await rig.Chat.OpenRoomAsync(room);

        Assert.Null(rig.Chat.ActiveRoom);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
        Assert.False(rig.Chat.IsLoadingMessages);
    }

    [Fact]
    public async Task Transient_failure_reopening_a_cached_room_keeps_its_history()
    {
        using var rig = new Rig();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var m = Msg(a, "kept", DateTime.UtcNow);
        rig.Http.On(RoomUrl(a), () => Json(Room(a)));
        rig.Http.On(Page1Url(a), () => Json(Page(m)));
        rig.Http.On(RoomUrl(b), () => Json(Room(b)));
        rig.Http.On(Page1Url(b), () => Json(Page()));
        await rig.Chat.OpenRoomAsync(a);
        await rig.Chat.OpenRoomAsync(b);

        rig.Http.On(Page1Url(a), Status(HttpStatusCode.ServiceUnavailable));
        await rig.Chat.OpenRoomAsync(a);

        Assert.Equal(m.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
        Assert.NotNull(rig.Chat.ActiveRoom);
        // A transient failure is not a membership verdict: the draft stays.
        Assert.DoesNotContain(rig.Ctx.JSInterop.Invocations, i => i.Identifier == "ipopDrafts.remove");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Denied_room_drops_cached_history_and_its_list_row(HttpStatusCode code)
    {
        using var rig = new Rig();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        rig.Http.On("/api/chat/rooms?tab=direct", () => Json(new PagedResult<RoomListDto>([Row(a), Row(b)], 2, 1, 50)));
        rig.Http.On("/api/chat/rooms?tab=groups", () => Json(new PagedResult<RoomListDto>([], 0, 1, 50)));
        await rig.Chat.LoadRoomsAsync();
        rig.Http.On(RoomUrl(a), () => Json(Room(a)));
        rig.Http.On(Page1Url(a), () => Json(Page(Msg(a, "secret", DateTime.UtcNow))));
        rig.Http.On(RoomUrl(b), () => Json(Room(b)));
        rig.Http.On(Page1Url(b), () => Json(Page()));
        await rig.Chat.OpenRoomAsync(a);
        await rig.Chat.OpenRoomAsync(b);

        // Removed from A while away: the room endpoint now refuses.
        var gone = new List<Guid>();
        rig.Chat.RoomGone += gone.Add;
        var gate = new TaskCompletionSource();
        rig.Http.OnGated(RoomUrl(a), gate, Status(code));
        rig.Http.OnGated(Page1Url(a), gate, () => Json(Page()));
        var open = rig.Chat.OpenRoomAsync(a);
        Assert.Single(rig.Chat.ActiveMessages);      // cache paints while the check is in flight…
        gate.SetResult();
        await open;

        Assert.Equal(ChatLoadError.Gone, rig.Chat.RoomError);   // …then it is gone
        Assert.Empty(rig.Chat.ActiveMessages);
        Assert.Null(rig.Chat.ActiveRoom);
        Assert.False(rig.Chat.IsLoadingMessages);
        Assert.DoesNotContain(rig.Chat.DirectRooms, r => r.Id == a);
        Assert.Equal([a], gone);                                 // owners of other per-room state told once
        Assert.Contains(rig.Ctx.JSInterop.Invocations, i =>      // stored draft for the room removed
            i.Identifier == "ipopDrafts.remove" && (string?)i.Arguments[1] == a.ToString("D"));

        // And the cache entry is gone: a later visit starts from the skeleton, not the old history.
        await rig.Chat.OpenRoomAsync(b);
        var gate2 = new TaskCompletionSource();
        rig.Http.OnGated(RoomUrl(a), gate2, Status(code));
        var again = rig.Chat.OpenRoomAsync(a);
        Assert.Empty(rig.Chat.ActiveMessages);                  // nothing painted from memory
        gate2.SetResult();
        await again;
        Assert.Equal(ChatLoadError.Gone, rig.Chat.RoomError);
    }

    [Fact]
    public async Task Late_answer_for_a_previous_room_does_not_paint_over_the_new_one()
    {
        using var rig = new Rig();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        rig.Http.OnGated(RoomUrl(a), gate, Status(HttpStatusCode.NotFound));
        rig.Http.OnGated(Page1Url(a), gate, () => Json(Page(Msg(a, "from A", DateTime.UtcNow))));
        var mb = Msg(b, "from B", DateTime.UtcNow);
        rig.Http.On(RoomUrl(b), () => Json(Room(b)));
        rig.Http.On(Page1Url(b), () => Json(Page(mb)));

        var openA = rig.Chat.OpenRoomAsync(a);
        await rig.Chat.OpenRoomAsync(b);
        gate.SetResult();
        await openA;

        Assert.Equal(b, rig.Chat.ActiveRoomId);
        Assert.Equal(b, rig.Chat.ActiveRoom?.Id);
        Assert.Equal(mb.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
    }

    [Fact]
    public async Task Superseded_retry_answer_is_ignored()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        rig.Http.On(Page1Url(room), Status(HttpStatusCode.InternalServerError));
        await rig.Chat.OpenRoomAsync(room);

        // First retry hangs; the user closes and reopens the room, which succeeds.
        var gate = new TaskCompletionSource();
        rig.Http.OnGated(Page1Url(room), gate, Status(HttpStatusCode.InternalServerError));
        var slowRetry = rig.Chat.RetryActiveRoomAsync();
        rig.Chat.CloseActiveRoom();
        var m = Msg(room, "fresh", DateTime.UtcNow);
        rig.Http.On(Page1Url(room), () => Json(Page(m)));
        await rig.Chat.OpenRoomAsync(room);
        gate.SetResult();
        await slowRetry;

        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
        Assert.Equal(m.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
    }

    [Fact]
    public async Task Closing_the_room_clears_its_error()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        rig.Http.On(RoomUrl(room), Status(HttpStatusCode.InternalServerError));
        rig.Http.On(Page1Url(room), () => Json(Page()));
        await rig.Chat.OpenRoomAsync(room);
        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.RoomError);
        rig.Chat.CloseActiveRoom();
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);
    }

    // ── Older history ──

    [Fact]
    public async Task Failed_older_page_keeps_history_and_cursor_and_retry_asks_for_the_same_page()
    {
        using var rig = new Rig();
        var room = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var newest = Msg(room, "newest", now);
        rig.Http.On(RoomUrl(room), () => Json(Room(room)));
        rig.Http.On(Page1Url(room), () => Json(new PagedResult<MessageDto>([newest], 80, 1, 50)));
        await rig.Chat.OpenRoomAsync(room);
        Assert.True(rig.Chat.HasOlderMessages);

        rig.Http.On("messages?before=", Status(HttpStatusCode.ServiceUnavailable));
        await rig.Chat.LoadOlderMessagesAsync();

        Assert.Equal(ChatLoadError.Unavailable, rig.Chat.OlderError);
        Assert.Equal(newest.Id, Assert.Single(rig.Chat.ActiveMessages).Id);
        Assert.True(rig.Chat.HasOlderMessages);
        Assert.Equal(1, rig.Chat.MessagePage);
        Assert.Equal(ChatLoadError.None, rig.Chat.RoomError);   // the room itself is fine

        // Scrolling to the top again does not hammer the server while the error is up.
        await rig.Chat.LoadOlderMessagesAsync();
        Assert.Equal(1, rig.Http.Count("messages?before="));

        var older = Msg(room, "older", now.AddMinutes(-5));
        rig.Http.On("messages?before=", () => Json(new PagedResult<MessageDto>([older], 1, 1, 50)));
        await rig.Chat.RetryOlderMessagesAsync();

        var befores = rig.Http.Requests.Where(r => r.Contains("messages?before=")).Distinct().ToList();
        Assert.Single(befores);                                  // same cursor both times
        Assert.Equal(ChatLoadError.None, rig.Chat.OlderError);
        Assert.Equal([older.Id, newest.Id], rig.Chat.ActiveMessages.Select(x => x.Id));
        Assert.Equal(2, rig.Chat.MessagePage);
        Assert.False(rig.Chat.HasOlderMessages);
    }

    [Fact]
    public async Task Older_page_answer_for_a_room_left_meanwhile_is_dropped()
    {
        using var rig = new Rig();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        rig.Http.On(RoomUrl(a), () => Json(Room(a)));
        rig.Http.On(Page1Url(a), () => Json(new PagedResult<MessageDto>([Msg(a, "a", DateTime.UtcNow)], 80, 1, 50)));
        rig.Http.On(RoomUrl(b), () => Json(Room(b)));
        rig.Http.On(Page1Url(b), () => Json(Page()));
        await rig.Chat.OpenRoomAsync(a);

        var gate = new TaskCompletionSource();
        rig.Http.OnGated("messages?before=", gate, Status(HttpStatusCode.InternalServerError));
        var older = rig.Chat.LoadOlderMessagesAsync();
        await rig.Chat.OpenRoomAsync(b);
        gate.SetResult();
        await older;

        Assert.Equal(ChatLoadError.None, rig.Chat.OlderError);  // A's failure is not B's banner
        Assert.Equal(b, rig.Chat.ActiveRoomId);
    }
}
