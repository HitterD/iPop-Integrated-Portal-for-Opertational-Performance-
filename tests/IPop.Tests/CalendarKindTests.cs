using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Meetings;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §6 (mockup screens 4 + 6): kind tiles in the New event form, room tiles with free/busy and the
/// day timeline, kind colours on calendar items, the Meetings legend and the tidy busy popover.
[Collection("AvatarPhotos")]
public class CalendarKindTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static DateTime At(int h, int m = 0) => new DateTime(2026, 10, 14, h, m, 0, DateTimeKind.Utc).AddHours(-7);
    private static readonly DateTime NowUtc = new DateTime(2026, 10, 13, 9, 50, 0, DateTimeKind.Utc).AddHours(-7);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static IRenderedComponent<MeetingKindPicker> Picker(BunitContext ctx, string kind, string? room = null, List<string>? kinds = null,
        List<string?>? rooms = null, List<string?>? places = null, List<(DateTime, DateTime)>? times = null) =>
        ctx.Render<MeetingKindPicker>(p => p.Add(c => c.Kind, kind).Add(c => c.RoomKey, room).Add(c => c.Location, "ICT office, 2nd floor")
            .Add(c => c.Start, At(13)).Add(c => c.End, At(14)).Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib)
            .Add(c => c.KindChanged, k => kinds?.Add(k)).Add(c => c.RoomKeyChanged, r => rooms?.Add(r))
            .Add(c => c.LocationChanged, l => places?.Add(l)).Add(c => c.OnTimesChanged, t => times?.Add(t)));

    // ---------------------------------------------------------------- kind tiles

    [Fact]
    public void Kind_tiles_follow_the_mockup_and_switch_the_section()
    {
        using var ctx = Context(new Api());
        var kinds = new List<string>();
        var cut = Picker(ctx, "", kinds: kinds);
        var tiles = cut.FindAll(".mkp-ktile");
        Assert.Equal(["Event", "Voice call", "Meeting room", "In person"], tiles.Select(t => t.QuerySelector("b")!.TextContent.Trim()).ToArray());
        Assert.Equal(["Just a time on the calendar", "Audio only, join from anywhere", "One of 5 rooms · video, share, record", "A place, no call link"],
            tiles.Select(t => t.QuerySelector(".mkp-ks")!.TextContent.Trim()).ToArray());
        Assert.Equal(["k-event", "k-voice", "r-kalosi", "k-person"], tiles.Select(t => t.QuerySelector(".mk-kt")!.ClassList.Last()).ToArray());
        Assert.Equal("true", tiles[0].GetAttribute("aria-checked"));
        Assert.Equal("radiogroup", cut.Find(".mkp-ktiles").GetAttribute("role"));
        foreach (var i in new[] { 1, 2, 3, 0 }) cut.FindAll(".mkp-ktile")[i].Click();
        Assert.Equal(["voice", "room", "inperson", ""], kinds);
        Assert.Empty(cut.FindAll(".mkp-rtiles"));
    }

    [Fact]
    public void Voice_shows_the_join_note_and_in_person_the_place()
    {
        using var ctx = Context(new Api());
        var voice = Picker(ctx, "voice");
        Assert.Contains("Everyone gets a Join call button in the invite and the reminder. Audio only, from PC or phone.", voice.Find(".mkp-kinfo").TextContent);
        Assert.Empty(voice.FindAll(".mkp-where"));

        var places = new List<string?>();
        var person = Picker(ctx, "inperson", places: places);
        var input = person.Find(".mkp-where input");
        Assert.Equal("ICT office, 2nd floor", input.GetAttribute("value"));
        input.Change("Lobby");
        Assert.Equal(["Lobby"], places);
    }

    [Fact]
    public void Room_tiles_show_free_or_busy_for_the_chosen_time_and_the_room_timeline()
    {
        var api = new Api();
        api.Busy.UnionWith(["arabika", "kapal-api"]);
        using var ctx = Context(api);
        var rooms = new List<string?>();
        var cut = Picker(ctx, "room", "kalosi", rooms: rooms);
        cut.WaitForAssertion(() => Assert.Equal("Free", cut.Find(".mkp-rtile.r-robusta .mkp-fs").TextContent.Trim()));
        Assert.Equal("Room · for Wed 14 Oct, 13:00–14:00", cut.Find(".mkp-gh").TextContent.Trim());
        var tiles = cut.FindAll(".mkp-rtile");
        Assert.Equal(["Robusta", "Arabika", "Kalosi", "Kapal Api", "Toraja"], tiles.Select(t => t.QuerySelector("b")!.TextContent.Trim()).ToArray());
        Assert.Equal(["Free", "Busy", "Free", "Busy", "Free"], tiles.Select(t => t.QuerySelector(".mkp-fs")!.TextContent.Trim()).ToArray());
        Assert.Contains("busy", tiles[1].ClassList);
        Assert.Contains("on", tiles[2].ClassList);
        Assert.Equal("true", tiles[2].GetAttribute("aria-checked"));
        Assert.All(tiles, t => Assert.Contains("up to 20", t.TextContent));
        var free = api.Gets.Single(g => g.StartsWith("/api/meeting-rooms/free"));
        Assert.Contains("from=", free);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".tl-track")));
        Assert.Contains("/api/meeting-rooms/kalosi/availability?date=2026-10-14", api.Gets);

        tiles[4].Click();
        Assert.Equal(["toraja"], rooms);
    }

    [Fact]
    public void Room_kind_without_a_room_asks_for_one()
    {
        using var ctx = Context(new Api());
        var cut = Picker(ctx, "room", null);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".mkp-rtile").Count));
        Assert.Contains("Choose a room", cut.Find(".mkp-pick").TextContent);
        Assert.Empty(cut.FindAll(".tl-track"));
    }

    // ---------------------------------------------------------------- save rule

    [Fact]
    public void Save_place_and_kind_per_tile()
    {
        Assert.Equal(("Online · iPop Meeting", (string?)null, "voice"), CalendarKinds.SavePlace("voice", null, "ignored", "https://x"));
        Assert.Equal(("SJA Hall · Kalosi", (string?)null, "room"), CalendarKinds.SavePlace("room", "kalosi", "ignored", "https://x"));
        Assert.Equal(("SJA Hall · Kapal Api", (string?)null, "room"), CalendarKinds.SavePlace("room", "kapal-api", null, null));
        Assert.Equal(("ICT office", "https://x", "inperson"), CalendarKinds.SavePlace("inperson", null, " ICT office ", "https://x"));
        Assert.Equal(("Desk", "https://x", (string?)null), CalendarKinds.SavePlace("", null, "Desk", "https://x"));
        Assert.Equal(((string?)null, (string?)null, (string?)null), CalendarKinds.SavePlace("", null, "  ", " "));
        // Room without a chosen room → the form refuses before saving.
        Assert.Equal("Choose a meeting room.", CalendarKinds.Validate("room", null, null));
        Assert.Equal("Where is it? Add a place.", CalendarKinds.Validate("inperson", null, " "));
        Assert.Null(CalendarKinds.Validate("voice", null, null));
    }

    [Fact]
    public void Editing_an_old_meeting_without_a_kind_infers_it()
    {
        Assert.Equal(("room", "toraja"), CalendarKinds.FormKind(null, null, "SJA Hall · Toraja", true));
        Assert.Equal(("voice", (string?)null), CalendarKinds.FormKind(null, null, "Online · iPop Meeting", true));
        Assert.Equal(("inperson", (string?)null), CalendarKinds.FormKind(null, null, "ICT office", true));
        Assert.Equal(("", (string?)null), CalendarKinds.FormKind(null, null, null, false));
        Assert.Equal(("room", "kalosi"), CalendarKinds.FormKind("room", "kalosi", "SJA Hall · Kalosi", true));
        // A plain personal event with a place stays an Event (no meeting semantics).
        Assert.Equal(("", (string?)null), CalendarKinds.FormKind(null, null, "Lantai 3", false, isMeeting: false));
    }

    [Fact]
    public void Query_kind_and_room_from_the_booking_sheet()
    {
        Assert.Equal(("room", "kalosi"), CalendarKinds.FromQuery("room", "kalosi"));
        Assert.Equal(("voice", (string?)null), CalendarKinds.FromQuery("voice", null));
        Assert.Equal(((string?)null, (string?)null), CalendarKinds.FromQuery("room", "gayo"));
        Assert.Equal(((string?)null, (string?)null), CalendarKinds.FromQuery("weird", null));
        Assert.Equal(((string?)null, (string?)null), CalendarKinds.FromQuery(null, Guid.NewGuid().ToString()));
    }

    // ---------------------------------------------------------------- calendar items

    private static Calendar.FeedItem Item(string source, string kind, string? meetingKind = null, string? roomKey = null, string? owner = null) =>
        new("x", source, kind, "Weekly ICT", At(10), At(11), false, null, null, Guid.NewGuid(), Guid.NewGuid(), null, true, null, null,
            OwnerNik: owner, MeetingKind: meetingKind, RoomKey: roomKey);

    [Fact]
    public void Meeting_items_are_coloured_by_kind_with_their_icon_and_other_items_are_unchanged()
    {
        Assert.Equal("mk-ev r-kalosi", CalendarKinds.ItemClass(Item("meetings", "meeting", "room", "kalosi")));
        Assert.Equal("video", CalendarKinds.ItemIcon(Item("meetings", "meeting", "room", "kalosi")));
        Assert.Equal("mk-ev k-voice", CalendarKinds.ItemClass(Item("meetings", "meeting", "voice")));
        Assert.Equal("headphones", CalendarKinds.ItemIcon(Item("meetings", "meeting", "voice")));
        Assert.Equal("mk-ev k-person", CalendarKinds.ItemClass(Item("chat", "meeting", "inperson")));
        Assert.Equal("map-pin", CalendarKinds.ItemIcon(Item("chat", "meeting", "inperson")));
        Assert.Equal("", CalendarKinds.ItemClass(Item("personal", "event")));
        Assert.Null(CalendarKinds.ItemIcon(Item("personal", "event")));
        // Someone else's busy block keeps the person's colour.
        Assert.Equal("", CalendarKinds.ItemClass(Item("other", "busy", "room", "kalosi", owner: "NIK1")));
        Assert.Equal("Kalosi", CalendarKinds.RoomLine(Item("meetings", "meeting", "room", "kalosi")));
        Assert.Null(CalendarKinds.RoomLine(Item("meetings", "meeting", "voice")));
    }

    [Fact]
    public void Legend_lists_the_three_meeting_kinds_with_room_swatches()
    {
        using var ctx = Context(new Api());
        var cut = ctx.Render<CalendarMeetingLegend>();
        Assert.Equal("Meetings", cut.Find(".cal-lg-t").TextContent.Trim());
        var rows = cut.FindAll(".cal-lg-row");
        Assert.Equal(3, rows.Count);
        Assert.Contains("Meeting room", rows[0].TextContent);
        Assert.Contains("Colour of the room", rows[0].TextContent);
        Assert.Equal(["r-robusta", "r-arabika", "r-kalosi", "r-kapal-api", "r-toraja"], rows[0].QuerySelectorAll(".cal-lg-sws i").Select(i => i.ClassName).ToArray());
        Assert.Contains("Voice call", rows[1].TextContent); Assert.Contains("Audio only", rows[1].TextContent);
        Assert.Contains("k-voice", rows[1].ClassList);
        Assert.Contains("In person", rows[2].TextContent); Assert.Contains("Place, no call link", rows[2].TextContent);
    }

    // ---------------------------------------------------------------- busy popover

    private static Calendar.FeedItem Busy(string visibility = "busy", string title = "Busy", string kind = "busy") =>
        new("other:NIK1:1", "other", kind, title, At(11), At(12), false, null, "Jason Kristanto", null, null, null, false, null, null,
            Visibility: visibility, OwnerNik: "NIK700001");

    private static IRenderedComponent<CalendarBusyPopover> Pop(BunitContext ctx, Calendar.FeedItem item, List<string> log, bool asked = false) =>
        ctx.Render<CalendarBusyPopover>(p => p.Add(c => c.Item, item).Add(c => c.Name, "Jason Kristanto").Add(c => c.Department, "ICT")
            .Add(c => c.Asked, asked).Add(c => c.X, 100).Add(c => c.Y, 100)
            .Add(c => c.OnAsk, () => log.Add("ask")).Add(c => c.OnFindTime, () => log.Add("find")).Add(c => c.OnClose, () => log.Add("close")));

    [Fact]
    public void Busy_popover_has_the_three_actions_and_no_title_for_busy_only()
    {
        using var ctx = Context(new Api());
        var log = new List<string>();
        var cut = Pop(ctx, Busy(), log);
        Assert.Equal("Jason Kristanto", cut.Find(".cal-bp-h b").TextContent.Trim());
        Assert.Equal("ICT · shares free/busy only", cut.Find(".cal-bp-h b + span").TextContent.Trim());
        Assert.Equal("Busy", cut.Find(".cal-bp-bz b").TextContent.Trim());
        Assert.Equal("Wednesday 14 Oct · 11:00–12:00", cut.Find(".cal-bp-bz b + span").TextContent.Trim());
        Assert.Equal("Ask to see details", cut.Find(".cal-bp-ask").TextContent.Trim());
        Assert.Equal("Find another time", cut.Find(".cal-bp-find").TextContent.Trim());
        var chat = cut.Find("a.cal-bp-chat");
        Assert.Equal("Chat", chat.TextContent.Trim());
        Assert.DoesNotContain("NIK700001", chat.GetAttribute("href"));
        Assert.DoesNotContain("NIK700001", cut.Markup);
        cut.Find(".cal-bp-ask").Click(); cut.Find(".cal-bp-find").Click(); cut.Find(".cal-bp-x").Click();
        Assert.Equal(["ask", "find", "close"], log);

        var asked = Pop(ctx, Busy(), log, asked: true);
        Assert.Equal("Asked", asked.Find(".cal-bp-ask").TextContent.Trim());
        Assert.True(asked.Find(".cal-bp-ask").HasAttribute("disabled"));
    }

    [Fact]
    public void Private_busy_has_no_ask_and_a_shared_title_shows_in_place_of_Busy()
    {
        using var ctx = Context(new Api());
        var log = new List<string>();
        var priv = Pop(ctx, Busy(visibility: "private"), log);
        Assert.Empty(priv.FindAll(".cal-bp-ask"));
        Assert.Contains("Private", priv.Find(".cal-bp-h b + span").TextContent);

        var titled = Pop(ctx, Busy(visibility: "public", title: "Budget review"), log);
        Assert.Equal("Budget review", titled.Find(".cal-bp-bz b").TextContent.Trim());
        Assert.Empty(titled.FindAll(".cal-bp-ask"));

        var tentative = Pop(ctx, Busy(kind: "busy-tentative", title: "Tentative"), log);
        Assert.Contains("Hasn't replied to this invitation yet", tentative.Find(".cal-bp-bz").TextContent);
    }

    // ---------------------------------------------------------------- fixture

    private static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddScoped<OverlayStack>();
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private sealed class Api : HttpMessageHandler
    {
        public HashSet<string> Busy { get; } = [];
        public List<string> Gets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get) lock (Gets) Gets.Add(request.RequestUri.PathAndQuery);
            if (path == "/api/meeting-rooms/free")
            {
                var rooms = new[] { ("robusta", "Robusta"), ("arabika", "Arabika"), ("kalosi", "Kalosi"), ("kapal-api", "Kapal Api"), ("toraja", "Toraja") };
                return Json(rooms.Select(r => new RoomFreeDto(r.Item1, r.Item2, 20, !Busy.Contains(r.Item1), Busy.Contains(r.Item1) ? "Booked" : null)).ToList());
            }
            if (path.EndsWith("/availability"))
            {
                var key = path.Split('/')[^2];
                return Json(new RoomDayDto(key, key, 20, new DateOnly(2026, 10, 14), At(7), At(19), []));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json") });
    }
}
