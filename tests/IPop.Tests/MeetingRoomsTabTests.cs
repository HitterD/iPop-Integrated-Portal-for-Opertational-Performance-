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
        var dl = rows[1].QuerySelector("a.mrt-download")!;
        Assert.Contains($"/api/meeting-rooms/recordings/{ready.Id}/file?download=1", dl.GetAttribute("href"));
        Assert.NotNull(rows[1].QuerySelector("button.mrt-play"));
        // Retention comes from the recordings themselves (DeleteAfter - StartedAt).
        Assert.Contains("Recordings are deleted automatically after 30 days.", cut.Find(".mrt-foot").TextContent);

        rows[1].QuerySelector("button.mrt-play")!.Click();
        var video = cut.Find(".mrt-player video");
        Assert.Equal($"http://localhost/api/meeting-rooms/recordings/{ready.Id}/file", video.GetAttribute("src"));
    }

    [Fact]
    public void Recording_urls_carry_the_access_token_because_video_tags_cannot_send_headers()
    {
        var id = Guid.Parse("7f1c0a4e-0000-4000-8000-000000000001");
        var http = new HttpClient { BaseAddress = new Uri("https://ipop.example:7103/") };
        Assert.Equal("https://ipop.example:7103/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001/file?access_token=a%2Bb%3D",
            MeetingRoomsTab.FileUrl(http.BaseAddress, id, "a+b=", download: false));
        Assert.Equal("https://ipop.example:7103/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001/file?download=1&access_token=t",
            MeetingRoomsTab.FileUrl(http.BaseAddress, id, "t", download: true));
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

    [Theory]
    [InlineData("/hubs/app", true)]
    [InlineData("/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001/file", true)]
    [InlineData("/api/meeting-rooms/recordings", false)]
    [InlineData("/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001", false)]
    [InlineData("/api/meeting-rooms/recordings/not-a-guid/file", false)]
    [InlineData("/api/meeting-rooms/recordings/7f1c0a4e-0000-4000-8000-000000000001/file/extra", false)]
    [InlineData("/api/files/x/file", false)]
    [InlineData("/api/me", false)]
    public void Query_token_is_accepted_only_for_hubs_and_recording_files(string path, bool allowed) =>
        Assert.Equal(allowed, IPop.Api.QueryTokenRule.Allows(new PathString(path)));

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
        public List<MeetingRoomDto> Rooms { get; init; } = [];
        public List<MeetingRecordingDto> Recordings { get; init; } = [];
        public HttpStatusCode JoinStatus { get; init; } = HttpStatusCode.NotFound;
        public string JoinBody { get; init; } = "{}";
        public List<string> Joins { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms") return Json(Rooms);
            if (request.Method == HttpMethod.Get && path == "/api/meeting-rooms/recordings") return Json(Recordings);
            if (request.Method == HttpMethod.Post && path.EndsWith("/join"))
            {
                Joins.Add(path.Split('/')[^2]);
                return Task.FromResult(new HttpResponseMessage(JoinStatus) { Content = new StringContent(JoinBody, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json") });
    }
}
