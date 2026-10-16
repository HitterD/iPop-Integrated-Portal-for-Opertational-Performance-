using System.Net;
using System.Text;
using Bunit;
using IPop.Web.Components.Calls;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace IPop.Tests;

/// Meeting Rooms v2 in-meeting UI (Task 7): chat panel, unread badge, booking time chip / banner / Extend,
/// the 1-minute "next booking" notice, Flip, the share-without-sound toast and the phone layout.
[Collection("AvatarPhotos")]
public class MeetingRoomInMeetingTests
{
    private const string Me = "11111111-1111-4111-8111-111111111111";
    private const string Budi = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTime Now = new(2026, 10, 12, 3, 55, 8, DateTimeKind.Utc);

    private static string Line(string id, string userId, string name, string text, string kind = "text", string sentAt = "2026-10-12T03:14:00Z") =>
        $$"""{"id":"{{id}}","userId":"{{userId}}","name":{{System.Text.Json.JsonSerializer.Serialize(name)}},"text":{{System.Text.Json.JsonSerializer.Serialize(text)}},"sentAt":"{{sentAt}}","kind":"{{kind}}"}""";

    private static string Id(int n) => $"aaaaaaaa-0000-4000-8000-{n:000000000000}";

    // ---------------------------------------------------------------- chat

    [Fact]
    public async Task Chat_history_renders_as_plain_text_with_only_http_links()
    {
        var api = new Api();
        api.Get["/api/meeting-rooms/arabika/chat"] = (HttpStatusCode.OK, "[" + string.Join(",",
            Line(Id(1), "00000000-0000-0000-0000-000000000000", "", "Budi started recording", "system"),
            Line(Id(2), Budi, "<i>Budi</i>", "<b>bold</b> see https://ipop.santos.co.id/files/a?x=1. and javascript:alert(1)"),
            Line(Id(3), Me, "Bagastyo", "Thanks 👍")) + "]");
        using var ctx = Context(out var state, api);
        Enter(state);
        await state.LoadChatAsync();
        var cut = ctx.Render<MeetingRoomOverlay>();

        cut.Find("button.mr-chat-btn").Click();
        var panel = cut.Find(".mr-chat");
        Assert.Contains("Budi started recording", panel.QuerySelector(".mr-sysm")!.TextContent);
        var budi = panel.QuerySelectorAll(".mr-m").First(m => m.TextContent.Contains("bold"));
        Assert.Contains("<i>Budi</i>", budi.TextContent);                 // the name is text, not markup
        Assert.Empty(budi.QuerySelectorAll("i, b"));
        Assert.Contains("<b>bold</b>", budi.QuerySelector("p")!.TextContent);
        var link = Assert.Single(budi.QuerySelectorAll("a"));
        Assert.Equal("https://ipop.santos.co.id/files/a?x=1", link.GetAttribute("href")); // trailing "." is not part of it
        Assert.Equal("_blank", link.GetAttribute("target"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.EndsWith(". and javascript:alert(1)", budi.QuerySelector("p")!.TextContent);
        var mine = panel.QuerySelectorAll(".mr-m").Single(m => m.ClassList.Contains("mine"));
        Assert.Contains("You", mine.TextContent);
    }

    [Theory]
    [InlineData("see http://a.b/c, ok", new[] { "see ", "http://a.b/c", ", ok" })]
    [InlineData("(https://x.y/z)", new[] { "(", "https://x.y/z", ")" })]
    [InlineData("ftp://x.y/z http://", new[] { "ftp://x.y/z http://" })]
    [InlineData("HTTPS://X.Y", new[] { "HTTPS://X.Y" })]
    public void Link_segments_only_take_absolute_http_urls(string text, string[] parts)
    {
        var segs = MeetingRoomOverlay.Segments(text);
        Assert.Equal(parts, segs.Select(s => s.Text).ToArray());
        Assert.All(segs.Where(s => s.Href is not null), s => Assert.StartsWith("http", s.Href!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unread_badge_counts_other_peoples_messages_while_the_panel_is_closed()
    {
        using var ctx = Context(out var state);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Empty(cut.FindAll(".mr-chat-btn .mr-badge"));

        state.OnRoomChat(Line(Id(1), Budi, "Budi", "one"));
        state.OnRoomChat(Line(Id(1), Budi, "Budi", "one"));               // relayed twice: one message
        state.OnRoomChat(Line(Id(2), Me, "Bagastyo", "mine"));            // own message: not unread
        state.OnRoomChat(Line(Id(3), "00000000-0000-0000-0000-000000000000", "", "Recording stopped", "system"));
        state.OnRoomChat(Line(Id(4), Budi, "Budi", "two"));
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find(".mr-chat-btn .mr-badge").TextContent.Trim()));
        Assert.Equal(4, state.Chat.Count);

        cut.Find("button.mr-chat-btn").Click();
        Assert.Empty(cut.FindAll(".mr-chat-btn .mr-badge"));
        state.OnRoomChat(Line(Id(5), Budi, "Budi", "three"));             // read while open
        cut.WaitForAssertion(() => Assert.Contains("three", cut.Find(".mr-chat").TextContent));
        Assert.Equal(0, state.Unread);

        cut.Find("button.mr-chat-btn").Click();                           // closed again
        state.OnRoomChat(Line(Id(6), Budi, "Budi", "four"));
        cut.WaitForAssertion(() => Assert.Equal("1", cut.Find(".mr-chat-btn .mr-badge").TextContent.Trim()));
    }

    [Fact]
    public void Chat_and_people_panels_replace_each_other()
    {
        using var ctx = Context(out var state);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-chat-btn").Click();
        Assert.NotEmpty(cut.FindAll(".mr-chat"));
        cut.Find("button.mr-people-btn").Click();
        Assert.Empty(cut.FindAll(".mr-chat"));
        Assert.NotEmpty(cut.FindAll(".mr-people"));
        cut.Find("button.mr-chat-btn").Click();
        Assert.Empty(cut.FindAll(".mr-people"));
    }

    [Fact]
    public void Sending_posts_once_and_the_relay_of_the_same_message_is_not_doubled()
    {
        var api = new Api();
        api.Post["/api/meeting-rooms/arabika/chat"] = (HttpStatusCode.OK, Line(Id(9), Me, "Bagastyo", "hello room"));
        using var ctx = Context(out var state, api);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-chat-btn").Click();

        cut.Find(".mr-composer input.mr-in").Input("  hello room ");
        cut.Find(".mr-composer button.mr-send").Click();
        cut.WaitForAssertion(() => Assert.Single(api.Posts));
        Assert.Equal("""{"text":"hello room"}""", api.Bodies.Single());
        state.OnRoomChat(Line(Id(9), Me, "Bagastyo", "hello room"));      // relay arrives after the POST answer
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mr-chat .mr-m")));
        Assert.Equal("", cut.Find(".mr-composer input.mr-in").GetAttribute("value") ?? "");
    }

    [Fact]
    public void Rate_limited_send_says_slow_down_and_keeps_the_draft()
    {
        var api = new Api();
        api.Post["/api/meeting-rooms/arabika/chat"] = (HttpStatusCode.TooManyRequests, """{"code":"rate_limited"}""");
        using var ctx = Context(out var state, api);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-chat-btn").Click();
        cut.Find(".mr-composer input.mr-in").Input("again");
        cut.Find(".mr-composer button.mr-send").Click();
        cut.WaitForAssertion(() => Assert.Contains("Slow down — you're sending messages too fast.", cut.Find(".mr-toast").TextContent));
        Assert.Equal("again", cut.Find(".mr-composer input.mr-in").GetAttribute("value"));
    }

    [Fact]
    public void Composer_caps_at_2000_and_shows_a_counter_near_the_limit()
    {
        using var ctx = Context(out var state);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find("button.mr-chat-btn").Click();
        Assert.Equal("2000", cut.Find(".mr-composer input.mr-in").GetAttribute("maxlength"));
        cut.Find(".mr-composer input.mr-in").Input(new string('a', 100));
        Assert.Empty(cut.FindAll(".mr-count"));
        cut.Find(".mr-composer input.mr-in").Input(new string('a', 1900));
        Assert.Contains("1900 / 2000", cut.Find(".mr-count").TextContent);
    }

    // ---------------------------------------------------------------- booking

    private static string Booking(DateTime endAt, string organizer = Me, DateTime? next = null, string[]? leaving = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            booking = new
            {
                meetingId = "33333333-3333-4333-8333-333333333333",
                organizerId = organizer,
                endAt,
                nextStartAt = next,
                takeoverAt = next?.AddMinutes(-10),
                leaving,
            },
        });

    [Fact]
    public void Time_chip_appears_only_in_the_last_15_minutes()
    {
        using var ctx = Context(out var state);
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(20)));
        var cut = ctx.Render<MeetingRoomOverlay>();
        Assert.Empty(cut.FindAll(".mr-timechip"));

        state.OnRoomMetadata(Booking(Now.AddMinutes(4).AddSeconds(52)));
        cut.WaitForAssertion(() => Assert.Equal("Ends in 4:52", cut.Find(".mr-timechip").TextContent.Trim()));
    }

    [Fact]
    public void Banner_at_five_minutes_offers_extend_only_to_the_organizer_when_the_slot_after_is_free()
    {
        var end = Now.AddMinutes(4).AddSeconds(52);
        var hhmm = end.ToLocalTime().ToString("HH:mm");

        using (var ctx = Context(out var state))
        {
            Enter(state);
            state.OnRoomMetadata(Booking(end));
            var cut = ctx.Render<MeetingRoomOverlay>();
            Assert.Contains($"This booking ends at {hhmm}.", cut.Find(".mr-banner").TextContent);
            Assert.NotEmpty(cut.FindAll(".mr-banner button.mr-extend"));
            cut.Find(".mr-banner button.mr-ok").Click();
            Assert.Empty(cut.FindAll(".mr-banner"));
            state.OnRoomMetadata(Booking(end));                              // metadata flicker does not bring it back
            cut.Render();
            Assert.Empty(cut.FindAll(".mr-banner"));
        }
        using (var ctx = Context(out var state))
        {
            Enter(state);
            state.OnRoomMetadata(Booking(end, organizer: Budi));
            var cut = ctx.Render<MeetingRoomOverlay>();
            Assert.NotEmpty(cut.FindAll(".mr-banner button.mr-ok"));
            Assert.Empty(cut.FindAll(".mr-banner button.mr-extend"));
        }
        using (var ctx = Context(out var state))
        {
            Enter(state);
            state.OnRoomMetadata(Booking(end, next: end.AddMinutes(20)));    // next window opens 10 min after the end
            var cut = ctx.Render<MeetingRoomOverlay>();
            Assert.NotEmpty(cut.FindAll(".mr-banner"));
            Assert.Empty(cut.FindAll(".mr-banner button.mr-extend"));
        }
        using (var ctx = Context(out var state))
        {
            Enter(state);
            state.OnRoomMetadata(Booking(Now.AddMinutes(12)));               // chip yes, banner not yet
            var cut = ctx.Render<MeetingRoomOverlay>();
            Assert.NotEmpty(cut.FindAll(".mr-timechip"));
            Assert.Empty(cut.FindAll(".mr-banner"));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"code":"room_booked"}""", "The room is booked right after this.")]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"forbidden"}""", "Only the organizer can extend this booking.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"media_unavailable"}""", "The meeting server is not reachable right now. Try again.")]
    public void Extend_posts_and_explains_a_refusal(HttpStatusCode code, string body, string message)
    {
        var api = new Api();
        api.Post["/api/meeting-rooms/arabika/extend"] = (code, body);
        using var ctx = Context(out var state, api);
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(4)));
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find(".mr-banner button.mr-extend").Click();
        cut.WaitForAssertion(() => Assert.Contains(message, cut.Find(".mr-toast").TextContent));
        Assert.Equal(["/api/meeting-rooms/arabika/extend"], api.Posts);
    }

    [Fact]
    public void Extend_success_hides_the_banner()
    {
        var api = new Api();
        api.Post["/api/meeting-rooms/arabika/extend"] = (HttpStatusCode.OK, """{"endAt":"2026-10-12T04:15:00Z"}""");
        using var ctx = Context(out var state, api);
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(4)));
        var cut = ctx.Render<MeetingRoomOverlay>();
        cut.Find(".mr-banner button.mr-extend").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".mr-banner")));
    }

    [Fact]
    public void One_minute_notice_only_for_people_listed_as_leaving_and_only_once()
    {
        var next = Now.AddMinutes(11);
        var hhmm = next.ToLocalTime().ToString("HH:mm");
        using (var ctx = Context(out var state))
        {
            Enter(state);
            var cut = ctx.Render<MeetingRoomOverlay>();
            state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: next, leaving: [Budi]));
            cut.Render();
            Assert.DoesNotContain(cut.FindAll(".mr-island"), i => i.TextContent.Contains("moved out"));
        }
        using (var ctx = Context(out var state))
        {
            Enter(state);
            var toasts = ctx.Services.GetRequiredService<ToastService>();
            var cut = ctx.Render<MeetingRoomOverlay>();
            state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: next, leaving: [Budi, Me]));
            cut.WaitForAssertion(() => Assert.Contains($"The next booking starts at {hhmm} — you'll be moved out in 1 minute.", cut.Find(".mr-island").TextContent));
            Assert.Equal(1, state.LeavingNotices);
            state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: next));               // flicker: leaving gone…
            state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: next, leaving: [Me])); // …and back
            Assert.Equal(1, state.LeavingNotices);
            Assert.Empty(toasts.Active);                                                  // no toasts for metadata
        }
    }

    [Fact]
    public void Removed_for_the_next_booking_says_so_instead_of_disconnected()
    {
        using var ctx = Context(out var state);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: Now.AddMinutes(10), leaving: [Me]));
        state.OnRoomState("disconnected");
        Assert.Null(state.Room);
        Assert.Contains(toasts.Active, t => t.Message == "The next booking starts now. You were moved out of Arabika.");
        Assert.DoesNotContain(toasts.Active, t => t.Message.Contains("disconnected"));
    }

    [Fact]
    public async Task Removed_at_takeover_while_invited_to_the_next_booking_rejoins_its_new_session()
    {
        // QA 9b: booking-specific media rooms end everyone's session at the takeover, including people invited to both
        // bookings ("People invited to the next booking can stay"). They must land in the successor's room, not be
        // dropped with "You were disconnected".
        var api = new Api();
        api.Post["/api/meeting-rooms/arabika/join"] = (HttpStatusCode.OK,
            """{"key":"arabika","name":"Arabika","liveKitRoom":"qa-hall-arabika-b","url":"wss://x","token":"token-b","videoAllowed":true,"identity":"me"}""");
        using var ctx = Context(out var state, api);
        ctx.JSInterop.Setup<bool>("ipopRooms.join", _ => true).SetResult(true);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(-5), next: Now.AddMinutes(10), leaving: [Budi]));   // takeoverAt = Now

        state.OnRoomState("disconnected");

        await WaitUntil(() => state.Room is not null);
        Assert.Equal("qa-hall-arabika-b", state.Room!.LiveKitRoom);
        Assert.Equal("token-b", Assert.Single(ctx.JSInterop.Invocations["ipopRooms.join"]).Arguments[1]);
        Assert.Contains(toasts.Active, t => t.Message == "The next booking started in Arabika. You're in it now.");
        Assert.DoesNotContain(toasts.Active, t => t.Message.Contains("disconnected"));
    }

    [Fact]
    public async Task Disconnected_outside_a_takeover_does_not_rejoin()
    {
        var api = new Api();
        using var ctx = Context(out var state, api);
        var toasts = ctx.Services.GetRequiredService<ToastService>();
        Enter(state);
        state.OnRoomMetadata(Booking(Now.AddMinutes(20), next: Now.AddMinutes(40), leaving: [Budi]));   // takeover in 30 min

        state.OnRoomState("disconnected");
        await Task.Delay(100);

        Assert.Null(state.Room);
        Assert.DoesNotContain(api.Posts, p => p.EndsWith("/join"));
        Assert.Contains(toasts.Active, t => t.Message == "You were disconnected from Arabika.");
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(20);
    }

    [Fact]
    public void Booking_metadata_survives_garbage_and_keeps_the_recording()
    {
        using var ctx = Context(out var state);
        Enter(state);
        state.OnRoomMetadata("""{"recording":{"id":"7f1c0a4e-0000-4000-8000-000000000001","by":"Budi","startedAt":"2026-10-11T03:00:00Z"},"booking":{"organizerId":"nope","endAt":"x"}}""");
        Assert.NotNull(state.RecordingServer);
        Assert.Null(state.Booking);
        state.OnRoomMetadata("not json");
        Assert.Null(state.Booking);
    }

    // ---------------------------------------------------------------- flip, share audio

    [Fact]
    public async Task Flip_shows_only_with_more_than_one_camera_and_rechecks_after_camera_on_and_device_change()
    {
        using var ctx = Context(out var state);
        Enter(state);
        var count = 1;
        ctx.JSInterop.Setup<int>("ipopRooms.listCameras").SetResult(1);
        var cut = ctx.Render<MeetingRoomOverlay>();
        await state.RefreshCamerasAsync();
        cut.Render();
        Assert.Empty(cut.FindAll("button.mr-flip"));

        // The browser shows the second camera only after permission: turning the camera on re-counts.
        ctx.JSInterop.Setup<bool>("ipopRooms.setCamera", _ => true).SetResult(true);
        ctx.JSInterop.Setup<int>("ipopRooms.listCameras").SetResult(count = 2);
        await cut.InvokeAsync(() => cut.Find("button.mr-cam").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.mr-flip")));

        cut.Find("button.mr-flip").Click();
        Assert.Single(ctx.JSInterop.Invocations["ipopRooms.flipCamera"]);

        // A camera unplugged: devicechange re-counts.
        ctx.JSInterop.Setup<int>("ipopRooms.listCameras").SetResult(1);
        await cut.InvokeAsync(() => state.OnDevicesChanged());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button.mr-flip")));
        Assert.Equal(2, count);
    }

    [Fact]
    public void Share_without_sound_shows_the_toast()
    {
        using var ctx = Context(out var state);
        Enter(state);
        var cut = ctx.Render<MeetingRoomOverlay>();
        state.OnRoomNotice("share_no_audio", null);
        cut.WaitForAssertion(() => Assert.Contains("This window shares no sound. To share audio, choose a Tab or Entire screen.", cut.Find(".mr-toast").TextContent));
    }

    [Fact]
    public void Phone_layout_opens_chat_full_screen_and_keeps_the_compact_dock()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/IPop.Web/wwwroot/css/modules/meeting-rooms.css"));
        var phone = System.Text.RegularExpressions.Regex.Matches(css, @"@media \(max-width: 767px\)[^{]*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}")
            .Select(m => m.Groups["body"].Value).Aggregate("", (a, b) => a + b);
        Assert.Matches(@"\.mr-chat\s*\{[^}]*inset:\s*0", phone);                      // chat full screen
        Assert.Matches(@"\.mr-share-btn,[^{]*\.mr-rec[^{]*\{[^}]*display:\s*none", phone); // Mute, Camera, Flip, Chat, Leave
        Assert.Matches(@"\.mr-tile\.local[^{]*\{[^}]*position:\s*absolute", phone);    // own picture-in-picture
    }

    // ---------------------------------------------------------------- plumbing

    private static void Enter(MeetingRoomState state)
    {
        state.SetForTest(new("arabika", "Arabika", "hall-arabika", "wss://x", "t", true, Me));
        state.Clock = () => Now;
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IPop.slnx"))) dir = dir.Parent;
            if (dir is not null) return dir.FullName;
        }
        throw new DirectoryNotFoundException("IPop.slnx not found");
    }

    private static BunitContext Context(out MeetingRoomState state, Api? api = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api ?? new Api(), disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        var calls = new CallState(auth, http, js);
        var toasts = new ToastService();
        state = new MeetingRoomState(auth, js, calls, toasts);
        ctx.Services.AddSingleton(new PhoneState(js));
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(calls);
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    /// Answers configured GET/POST paths; everything else 404s. Records POST paths and bodies.
    private sealed class Api : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Code, string Body)> Get { get; } = [];
        public Dictionary<string, (HttpStatusCode Code, string Body)> Post { get; } = [];
        public List<string> Posts { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var map = request.Method == HttpMethod.Post ? Post : Get;
            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                lock (Posts) { Posts.Add(path); Bodies.Add(body); }
            }
            return map.TryGetValue(path, out var r)
                ? new HttpResponseMessage(r.Code) { Content = new StringContent(r.Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
