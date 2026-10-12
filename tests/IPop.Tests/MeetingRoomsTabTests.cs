using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Calls & Meetings → Meeting Rooms tab: the five room tiles, the room-full alert and the Recordings list.
[Collection("AvatarPhotos")]
public class MeetingRoomsTabTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static MeetingRoomPerson P(string name, bool sharing = false, bool pc = false) => new(Guid.NewGuid(), name, sharing, pc);
    private static List<MeetingRoomPerson> People(int n) => Enumerable.Range(1, n).Select(i => P($"Person {i}")).ToList();

    private static MeetingRoomDto Room(string key, string name, int people = 0, bool video = true, bool available = true,
        MeetingRoomRecordingState? rec = null, List<MeetingRoomPerson>? list = null, MeetingRoomBookingDto? now = null) =>
        new(key, name, 20, video, available, list ?? People(people), now, null, rec);

    private static List<MeetingRoomDto> FiveRooms() =>
    [
        // Out of order on purpose: the tab shows the hall order, not the API order.
        Room("toraja", "Toraja"),
        Room("arabika", "Arabika", people: 6),
        Room("robusta", "Robusta"),
        Room("kalosi", "Kalosi", people: 2, video: false),
        Room("kapal-api", "Kapal Api", people: 20),
    ];

    [Fact]
    public void Renders_the_five_rooms_in_hall_order_with_empty_counted_and_full_states()
    {
        using var ctx = Context(new Api { Rooms = FiveRooms() });
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        var names = cut.FindAll(".mrt-room h3").Select(h => h.TextContent).ToList();
        Assert.Equal(["Robusta", "Arabika", "Kalosi", "Kapal Api", "Toraja"], names);

        var tiles = cut.FindAll(".mrt-room");
        Assert.Contains("Nobody here yet", tiles[0].TextContent);
        Assert.Contains("6 of 20", tiles[1].TextContent);
        Assert.Contains("In use", tiles[1].QuerySelector(".mrt-cap")!.TextContent);
        Assert.Equal("mic", tiles[2].QuerySelector(".mrt-join i")!.GetAttribute("data-lucide"));     // audio-only room
        Assert.Equal("Full", tiles[3].QuerySelector(".mrt-cap")!.TextContent.Trim());
        var full = tiles[3].QuerySelector(".mrt-join")!;
        Assert.Equal("Room is full", full.TextContent.Trim());
        Assert.True(full.HasAttribute("disabled"));
        Assert.Contains("+17", tiles[3].QuerySelector(".mrt-faces")!.TextContent);
    }

    [Fact]
    public void Recording_room_shows_the_recording_capsule_and_who_is_sharing()
    {
        var rooms = FiveRooms();
        rooms[1] = Room("arabika", "Arabika", rec: new(Guid.NewGuid(), "Budi", Now),
            list: [P("Budi Darmawan", sharing: true), P("Sari Ayu")]);
        rooms[2] = Room("robusta", "Robusta", list: [P("Andi", pc: true)]);
        using var ctx = Context(new Api { Rooms = rooms });
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        var arabika = cut.FindAll(".mrt-room")[1];
        var cap = arabika.QuerySelector(".mrt-cap")!;
        Assert.Equal("Recording", cap.TextContent.Trim());
        Assert.Contains("is-rec", cap.ClassName);
        Assert.Contains("Budi is sharing a screen", arabika.TextContent);
        // Recording to a PC counts as recording too: everyone must see it.
        Assert.Equal("Recording", cut.FindAll(".mrt-room")[0].QuerySelector(".mrt-cap")!.TextContent.Trim());
    }

    [Fact]
    public void Unavailable_room_shows_unavailable_and_a_disabled_join()
    {
        var rooms = FiveRooms();
        rooms[2] = Room("robusta", "Robusta", available: false);
        using var ctx = Context(new Api { Rooms = rooms });
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        var robusta = cut.FindAll(".mrt-room")[0];
        Assert.Equal("Unavailable", robusta.QuerySelector(".mrt-cap")!.TextContent.Trim());
        Assert.True(robusta.QuerySelector(".mrt-join")!.HasAttribute("disabled"));
    }

    [Fact]
    public void Joining_a_room_that_filled_up_shows_the_alert_with_rooms_that_have_space()
    {
        var rooms = FiveRooms();
        rooms[4] = Room("kapal-api", "Kapal Api", people: 19);   // looked free, filled up meanwhile
        var api = new Api { Rooms = rooms, JoinStatus = HttpStatusCode.Conflict,
            JoinBody = """{"code":"room_full","suggestions":["robusta","kalosi"]}""" };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));

        cut.FindAll(".mrt-room")[3].QuerySelector(".mrt-join")!.Click();

        cut.WaitForAssertion(() => Assert.Equal("Kapal Api is full", cut.Find(".mrt-alert h3").TextContent));
        Assert.Contains("All 20 places are taken.", cut.Find(".mrt-alert").TextContent);
        var alts = cut.FindAll(".mrt-alt button");
        Assert.Equal(2, alts.Count);
        Assert.Contains("Robusta", alts[0].TextContent);
        Assert.Contains("Empty", alts[0].TextContent);
        Assert.Contains("Kalosi", alts[1].TextContent);
        Assert.Contains("2 people, audio only", alts[1].TextContent);

        cut.Find(".mrt-alert-actions button").Click();
        Assert.Empty(cut.FindAll(".mrt-alert"));
    }

    [Fact]
    public void Joining_while_in_a_call_shows_a_toast()
    {
        // MeetingRoomState answers "in_call" without touching the API when a call is live; here the API says it.
        var api = new Api { Rooms = FiveRooms(), JoinStatus = HttpStatusCode.Conflict, JoinBody = """{"code":"in_call"}""" };
        using var ctx = Context(api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.FindAll(".mrt-room")[0].QuerySelector(".mrt-join")!.Click();
        cut.WaitForAssertion(() => Assert.Contains(toasts.Active, t => t.Message == "Leave your current call first."));
    }

    [Fact]
    public void Recordings_list_preparing_and_ready_rows_with_download_and_play()
    {
        var ready = new MeetingRecordingDto(Guid.NewGuid(), "robusta", "Robusta", "Weekly ICT sync", "Bagastyo", Now.AddDays(-2),
            3760, 418L * 1024 * 1024, 9, "ready", Now.AddDays(28));
        var processing = new MeetingRecordingDto(Guid.NewGuid(), "arabika", "Arabika", "Production review", "Budi", Now.AddHours(-1),
            null, null, 7, "processing", Now.AddDays(30));
        using var ctx = Context(new Api { Rooms = FiveRooms(), Recordings = [processing, ready] });
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".mrt-rec").Count));

        var rows = cut.FindAll(".mrt-rec");
        Assert.Contains("Preparing video", rows[0].TextContent);
        Assert.Empty(rows[0].QuerySelectorAll("button.mrt-play"));
        Assert.Contains("recorded by Budi", rows[0].TextContent);
        Assert.Contains("Arabika, 7 people", rows[0].TextContent);

        Assert.Contains("Weekly ICT sync", rows[1].TextContent);
        Assert.Equal("1:02:40", rows[1].QuerySelector(".mrt-dur")!.TextContent);
        Assert.Contains("418 MB", rows[1].TextContent);
        Assert.NotNull(rows[1].QuerySelector("button.mrt-download"));
        Assert.NotNull(rows[1].QuerySelector("button.mrt-play"));
        // Retention comes from the recordings themselves (DeleteAfter - StartedAt).
        Assert.Contains("Recordings are deleted automatically after 30 days.", cut.Find(".mrt-foot").TextContent);

        rows[1].QuerySelector("button.mrt-play")!.Click();
        cut.WaitForAssertion(() => cut.Find(".mrt-player video"));
        Assert.Equal($"http://localhost/api/meeting-rooms/recordings/{ready.Id}/file?t=tk-{ready.Id:N}", cut.Find(".mrt-player video").GetAttribute("src"));
    }

    // ---------------------------------------------------------------- final review fixes

    [Fact]
    public void Player_uses_one_recording_ticket_and_keeps_it_across_polls_and_never_puts_the_session_token_in_a_url()
    {
        var ready = new MeetingRecordingDto(Guid.NewGuid(), "robusta", "Robusta", "Weekly ICT sync", "Bagastyo", Now.AddDays(-2),
            3760, 418L * 1024 * 1024, 9, "ready", Now.AddDays(28));
        var api = new Api { Rooms = FiveRooms(), Recordings = [ready] };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromMilliseconds(60)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));

        cut.Find("button.mrt-play").Click();
        cut.WaitForAssertion(() => cut.Find(".mrt-player video"));
        var src = cut.Find(".mrt-player video").GetAttribute("src");
        Assert.Equal($"http://localhost/api/meeting-rooms/recordings/{ready.Id}/file?t=tk-{ready.Id:N}", src);
        Assert.Equal($"{src}&download=1", cut.Find(".mrt-player a.mrt-download").GetAttribute("href"));
        Assert.DoesNotContain("access_token", cut.Markup);

        var gets = api.RoomGets;
        cut.WaitForAssertion(() => Assert.True(api.RoomGets >= gets + 3), TimeSpan.FromSeconds(3));   // several polls re-rendered
        Assert.Equal(src, cut.Find(".mrt-player video").GetAttribute("src"));
        Assert.Equal([ready.Id], api.Tickets);                                                        // fetched once
    }

    [Fact]
    public void Download_fetches_a_ticket_on_click_and_saves_with_download_1()
    {
        var ready = new MeetingRecordingDto(Guid.NewGuid(), "robusta", "Robusta", "Weekly ICT sync", "Bagastyo", Now.AddDays(-2),
            3760, 418L * 1024 * 1024, 9, "ready", Now.AddDays(28));
        var api = new Api { Rooms = FiveRooms(), Recordings = [ready] };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromHours(1)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        Assert.Empty(api.Tickets);   // nothing asked before the click

        cut.Find(".mrt-rec button.mrt-download").Click();

        cut.WaitForAssertion(() => Assert.Single(ctx.JSInterop.Invocations["ipopRooms.download"]));
        Assert.Equal($"http://localhost/api/meeting-rooms/recordings/{ready.Id}/file?t=tk-{ready.Id:N}&download=1",
            ctx.JSInterop.Invocations["ipopRooms.download"].Single().Arguments[0]);
    }

    [Fact]
    public void Ticket_refused_shows_a_toast_and_opens_no_player()
    {
        var ready = new MeetingRecordingDto(Guid.NewGuid(), "robusta", "Robusta", "Weekly ICT sync", "Bagastyo", Now.AddDays(-2),
            3760, 418L * 1024 * 1024, 9, "ready", Now.AddDays(28));
        var api = new Api { Rooms = FiveRooms(), Recordings = [ready], TicketStatus = HttpStatusCode.NotFound };
        using var ctx = Context(api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromHours(1)));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mrt-rec")));
        cut.Find("button.mrt-play").Click();
        cut.WaitForAssertion(() => Assert.Contains(toasts.Active, t => t.Message == "This recording can't be opened right now."));
        Assert.Empty(cut.FindAll(".mrt-player"));
    }

    [Fact]
    public void Join_deep_link_is_consumed_by_replacing_the_url()
    {
        var api = new Api { Rooms = FiveRooms(), JoinStatus = HttpStatusCode.Conflict, JoinBody = """{"code":"room_full","suggestions":[]}""" };
        using var ctx = Context(api);
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/calls?tab=rooms&join=kalosi");
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.JoinKey, "kalosi"));
        cut.WaitForAssertion(() => Assert.Equal(["kalosi"], api.Joins));
        Assert.EndsWith("/calls?tab=rooms", nav.Uri);
        Assert.True(nav.History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void Room_state_changes_reload_at_most_once_per_five_seconds_by_default()
    {
        var api = new Api { Rooms = FiveRooms() };
        using var ctx = Context(api);
        var state = ctx.Services.GetRequiredService<MeetingRoomState>();
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromHours(1)));
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        Assert.Equal(TimeSpan.FromSeconds(5), cut.Instance.ChangedReloadGap);
        for (var i = 0; i < 20; i++) state.OnRoomPeople([]);   // speaking updates
        Thread.Sleep(1500);
        Assert.InRange(api.RoomGets, 1, 2);                       // first load + at most one immediate reload, the rest waits
    }

    [Fact]
    public void Empty_recordings_show_the_hint_and_the_default_retention()
    {
        using var ctx = Context(new Api { Rooms = FiveRooms() });
        var cut = ctx.Render<MeetingRoomsTab>();
        cut.WaitForAssertion(() => Assert.Contains("No recordings yet. Server recordings you attend appear here.", cut.Markup));
        Assert.Contains("after 90 days", cut.Find(".mrt-foot").TextContent);
    }

    [Fact]
    public void Join_deep_link_joins_once_after_the_rooms_load()
    {
        var api = new Api { Rooms = FiveRooms(), JoinStatus = HttpStatusCode.Conflict, JoinBody = """{"code":"room_full","suggestions":[]}""" };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.JoinKey, "kalosi"));
        cut.WaitForAssertion(() => Assert.Equal("Kalosi is full", cut.Find(".mrt-alert h3").TextContent));
        Assert.Equal(["kalosi"], api.Joins);
        Assert.Contains("No other room has space right now.", cut.Find(".mrt-alert").TextContent);
    }

    // ---------------------------------------------------------------- fix round 1

    [Fact]
    public void Room_state_changes_reload_the_rooms_coalesced()
    {
        var api = new Api { Rooms = FiveRooms() };
        using var ctx = Context(api);
        var state = ctx.Services.GetRequiredService<MeetingRoomState>();
        var cut = ctx.Render<MeetingRoomsTab>(p => p
            .Add(c => c.PollInterval, TimeSpan.FromHours(1))
            .Add(c => c.ChangedReloadGap, TimeSpan.FromMilliseconds(300)));
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        Assert.Equal(1, api.RoomGets);

        // Someone walks into Robusta; the browser reports it as a burst of Changed events.
        var rooms = FiveRooms();
        rooms[2] = Room("robusta", "Robusta", people: 3);
        api.Rooms = rooms;
        for (var i = 0; i < 10; i++) state.OnRoomPeople([]);

        cut.WaitForAssertion(() => Assert.Contains("3 of 20", cut.FindAll(".mrt-room")[0].TextContent));
        Thread.Sleep(900);                    // let any trailing reload of the burst finish
        // One reload straight away plus at most one trailing reload for the rest of the burst, never ten.
        Assert.InRange(api.RoomGets, 2, 3);
    }

    [Fact]
    public void Recording_link_to_a_processing_recording_opens_the_player_once_it_is_ready()
    {
        var id = Guid.NewGuid();
        MeetingRecordingDto Rec(string status) => new(id, "arabika", "Arabika", "Production review", "Budi", Now.AddMinutes(-20),
            status == "ready" ? 600 : null, status == "ready" ? 10L * 1024 * 1024 : null, 4, status, Now.AddDays(90));
        var api = new Api { Rooms = FiveRooms(), Recordings = [Rec("processing")] };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p
            .Add(c => c.RecordingId, id)
            .Add(c => c.PollInterval, TimeSpan.FromMilliseconds(80)));
        cut.WaitForAssertion(() => Assert.Contains("mrt-target", cut.Find($"#mrt-rec-{id}").ClassName));
        Assert.Empty(cut.FindAll(".mrt-player"));
        Assert.Single(ctx.JSInterop.Invocations["ipopRooms.reveal"]);

        api.Recordings = [Rec("ready")];
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".mrt-player video")), TimeSpan.FromSeconds(3));
        Assert.Single(ctx.JSInterop.Invocations["ipopRooms.reveal"]);   // scrolled once, not on every reload

        // Closing it is final: later polls do not reopen the player.
        cut.Find(".mrt-player .mrt-close").Click();
        var gets = api.RecordingGets;
        Thread.Sleep(300);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mrt-player")));
        Assert.Equal(gets, api.RecordingGets);                          // nothing left to wait for: recordings stop polling
    }

    [Fact]
    public async Task Leaving_the_tab_while_it_loads_starts_no_polling()
    {
        var api = new Api { Rooms = FiveRooms(), Gate = new TaskCompletionSource() };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromMilliseconds(30)));
        var tab = cut.Instance;
        await ctx.DisposeComponentsAsync();   // user switched tabs while /api/meeting-rooms was still loading
        api.Gate!.SetResult();
        await Task.Delay(300);
        // The timer is private; a leaked one would keep firing (and holding the component) forever.
        var poll = typeof(MeetingRoomsTab).GetField("poll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Null(poll.GetValue(tab));
        Assert.Equal(1, api.RoomGets);
    }

    [Fact]
    public void Rooms_that_cannot_be_reached_show_a_calm_message_with_retry_and_polling_keeps_trying()
    {
        var api = new Api { Rooms = FiveRooms(), RoomsStatus = HttpStatusCode.InternalServerError };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromMilliseconds(80)));
        cut.WaitForAssertion(() => Assert.Contains("Meeting rooms can't be reached right now.", cut.Find(".mrt-rooms-error").TextContent));
        Assert.Empty(cut.FindAll(".mrt-room"));
        Assert.NotNull(cut.Find(".mrt-rooms-error button.mrt-retry"));

        api.RoomsStatus = HttpStatusCode.OK;  // server back: the next poll recovers without a click
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count), TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll(".mrt-rooms-error"));

        // A later failure is reported again (never silently shown as stale or empty rooms).
        api.RoomsStatus = HttpStatusCode.InternalServerError;
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mrt-rooms-error")), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Retry_reloads_rooms_and_recordings()
    {
        var api = new Api { Rooms = FiveRooms(), RoomsStatus = HttpStatusCode.InternalServerError, RecordingsStatus = HttpStatusCode.InternalServerError };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p.Add(c => c.PollInterval, TimeSpan.FromHours(1)));
        cut.WaitForAssertion(() => Assert.Contains("Recordings can't be loaded right now.", cut.Find(".mrt-recs-error").TextContent));
        Assert.DoesNotContain("No recordings yet", cut.Markup);

        api.RoomsStatus = HttpStatusCode.OK;
        api.RecordingsStatus = HttpStatusCode.OK;
        cut.Find(".mrt-recs-error button.mrt-retry").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mrt-room").Count));
        cut.WaitForAssertion(() => Assert.Contains("No recordings yet. Server recordings you attend appear here.", cut.Markup));
        Assert.Empty(cut.FindAll(".mrt-error"));
    }

    [Fact]
    public async Task Leaving_the_tab_during_a_retry_never_joins_the_deep_linked_room()
    {
        // /calls?tab=rooms&join=kalosi while the API is down: nothing to join yet, so the link waits.
        var api = new Api { Rooms = FiveRooms(), RoomsStatus = HttpStatusCode.InternalServerError };
        using var ctx = Context(api);
        var cut = ctx.Render<MeetingRoomsTab>(p => p
            .Add(c => c.JoinKey, "kalosi")
            .Add(c => c.PollInterval, TimeSpan.FromHours(1)));
        cut.WaitForAssertion(() => cut.Find(".mrt-rooms-error"));
        Assert.Empty(api.Joins);

        // Retry; the server comes back, but slowly, and the user leaves the tab before it answers.
        api.RoomsStatus = HttpStatusCode.OK;
        api.RoomsGate = new TaskCompletionSource();
        cut.Find(".mrt-rooms-error button.mrt-retry").Click();
        await ctx.DisposeComponentsAsync();
        api.RoomsGate.SetResult();
        await Task.Delay(300);

        Assert.Empty(api.Joins);                                     // no POST /join
        Assert.Empty(ctx.JSInterop.Invocations["ipopRooms.join"]);   // and no media started
    }

    // ---------------------------------------------------------------- harness

    private static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        var calls = new CallState(auth, http, js);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(new MeetingRoomState(auth, js, calls));
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    private sealed class Api : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
        public volatile List<MeetingRoomDto> Rooms = [];
        public volatile List<MeetingRecordingDto> Recordings = [];
        public HttpStatusCode RoomsStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode RecordingsStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode JoinStatus { get; init; } = HttpStatusCode.NotFound;
        public string JoinBody { get; init; } = "{}";
        public List<string> Joins { get; } = [];
        public List<Guid> Tickets { get; } = [];
        public HttpStatusCode TicketStatus { get; init; } = HttpStatusCode.OK;
        /// When set, GET /api/meeting-rooms waits for it (a slow first load).
        public TaskCompletionSource? Gate { get; init; }
        /// Set mid-test: the next GET /api/meeting-rooms waits for it.
        public volatile TaskCompletionSource? RoomsGate;
        private int roomGets, recordingGets;
        public int RoomGets => Volatile.Read(ref roomGets);
        public int RecordingGets => Volatile.Read(ref recordingGets);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms")
            {
                Interlocked.Increment(ref roomGets);
                if (Gate is not null) await Gate.Task;
                if (RoomsGate is { } later) await later.Task;
                return RoomsStatus == HttpStatusCode.OK ? await Json(Rooms) : new HttpResponseMessage(RoomsStatus);
            }
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms/recordings")
            {
                Interlocked.Increment(ref recordingGets);
                return RecordingsStatus == HttpStatusCode.OK ? await Json(Recordings) : new HttpResponseMessage(RecordingsStatus);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/ticket"))
            {
                var id = Guid.Parse(path.Split('/')[^2]);
                lock (Tickets) Tickets.Add(id);
                return TicketStatus == HttpStatusCode.OK
                    ? await Json(new { url = $"/api/meeting-rooms/recordings/{id}/file?t=tk-{id:N}" })
                    : new HttpResponseMessage(TicketStatus);
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/join"))
            {
                Joins.Add(path.Split('/')[^2]);
                return new HttpResponseMessage(JoinStatus) { Content = new StringContent(JoinBody, Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static Task<HttpResponseMessage> Json<T>(T body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json") });
    }
}
