using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Meetings;
using IPop.Web.Models;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §5 (mockup screen 1): Calls › Scheduled grouped by day with kind badges, countdowns, avatar
/// stacks and "You organise"; the collapsed Past section with attendance and recordings; the Schedule menu.
[Collection("AvatarPhotos")]
public class CallsScheduledTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Alphin = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string MeNik = "NIK990001", AlphinNik = "NIK990002";
    /// Tuesday 13 October 2026, 09:50 WIB (the mockup's "now").
    private static DateTime At(int day, int h, int m = 0) => new DateTime(2026, 10, day, h, m, 0, DateTimeKind.Utc).AddHours(-7);
    private static readonly DateTime NowUtc = At(13, 9, 50);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static MeetingInfo M(string title, DateTime start, int minutes, string? kind, string? roomKey = null, Guid? organizer = null,
        int extraPeople = 0, string status = "aktif")
    {
        var org = organizer ?? Alphin;
        var location = kind switch { "room" => $"SJA Hall · {MeetingKinds.RoomName(roomKey)}", "voice" => "Online · iPop Meeting", _ => "ICT office, 2nd floor" };
        var people = new List<MeetingAttendeeInfo>
        {
            new(Alphin, "Alphin Santoso", AlphinNik, true, "accept", null),
            new(Me, "Bagas Tyo", MeNik, true, org == Me ? "accept" : "pending", null),
        };
        for (var i = 0; i < extraPeople; i++) people.Add(new(Guid.NewGuid(), $"Person {i}", $"NIK99{i:0000}", true, "pending", null));
        return new MeetingInfo(Guid.NewGuid(), title, start, start.AddMinutes(minutes), null, location, null, org,
            org == Me ? "Bagas Tyo" : "Alphin Santoso", org == Me ? MeNik : AlphinNik, status, people, 1, 0, 0, people.Count - 1,
            org == Me ? "accept" : "pending", null, "meeting-x", null, 10, null, kind, roomKey);
    }

    private static List<MeetingInfo> MockupWeek() =>
    [
        M("Vendor sync · Cisco Meraki", At(13, 9), 60, "voice"),
        M("Weekly ICT", At(13, 10), 60, "room", "kalosi", extraPeople: 5),
        M("SAP access review", At(13, 13, 30), 60, "inperson", organizer: Me),
        M("Budget 2027 draft", At(14, 11), 60, "voice"),
        M("Production review", At(15, 8, 30), 60, "room", "arabika"),
        M("Q4 network rollout review", At(19, 13), 150, "room", "kalosi", organizer: Me),
    ];

    private static IRenderedComponent<ScheduledList> List(BunitContext ctx, IReadOnlyList<MeetingInfo> meetings, Guid? selected = null,
        List<MeetingInfo>? picked = null, List<PastMeetingInfo>? pickedPast = null) =>
        ctx.Render<ScheduledList>(p => p.Add(c => c.Meetings, meetings).Add(c => c.Me, Me).Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib)
            .Add(c => c.SelectedId, selected)
            .Add(c => c.OnSelect, m => picked?.Add(m)).Add(c => c.OnSelectPast, m => pickedPast?.Add(m)));

    // ---------------------------------------------------------------- grouping rule

    [Theory]
    [InlineData(13, "today", "Today")]
    [InlineData(12, "today", "Today")]           // started yesterday and still running
    [InlineData(14, "tomorrow", "Tomorrow")]
    [InlineData(15, "week", "This week")]
    [InlineData(18, "week", "This week")]        // Sunday closes the Monday-first week
    [InlineData(19, "later", "Later")]
    public void Group_by_day_relative_to_today(int day, string key, string label)
    {
        var g = CallsSchedule.Group(new DateTime(2026, 10, day, 9, 0, 0), new DateTime(2026, 10, 13));
        Assert.Equal((key, label), (g.Key, g.Label));
    }

    [Fact]
    public void Group_on_a_Sunday_puts_Monday_under_tomorrow_and_the_rest_later()
    {
        var sunday = new DateTime(2026, 10, 18);
        Assert.Equal("tomorrow", CallsSchedule.Group(sunday.AddDays(1), sunday).Key);
        Assert.Equal("later", CallsSchedule.Group(sunday.AddDays(2), sunday).Key);
    }

    [Fact]
    public void Default_room_is_the_first_free_room_else_the_first_room()
    {
        Assert.Equal("arabika", CallsSchedule.DefaultRoom([new("robusta", "Robusta", 20, false, "Booked"), new("arabika", "Arabika", 20, true, null)]));
        Assert.Equal("robusta", CallsSchedule.DefaultRoom([new("robusta", "Robusta", 20, false, "Booked")]));
        Assert.Equal("robusta", CallsSchedule.DefaultRoom(null));
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public void List_is_grouped_Today_Tomorrow_This_week_Later_with_day_subtitles()
    {
        using var ctx = Context(new Api());
        var cut = List(ctx, MockupWeek());
        Assert.Equal(["Today", "Tomorrow", "This week", "Later"], cut.FindAll(".sch-day-h").Select(h => h.ChildNodes[0].TextContent.Trim()).ToList());
        Assert.Equal("Tuesday 13 Oct", cut.FindAll(".sch-day-h")[0].QuerySelector("span")!.TextContent);
        Assert.Equal(3, cut.FindAll(".sch-group")[0].QuerySelectorAll(".sch-ev").Length);
    }

    [Fact]
    public void Cards_show_time_range_title_kind_badge_countdown_stack_and_organizer()
    {
        using var ctx = Context(new Api());
        var cut = List(ctx, MockupWeek());
        var cards = cut.FindAll(".sch-ev");
        var vendor = cards[0];
        Assert.Equal("09:00–10:00", vendor.QuerySelector(".sch-time")!.TextContent.Trim());
        Assert.Equal("Vendor sync · Cisco Meraki", vendor.QuerySelector(".sch-title")!.TextContent.Trim());
        Assert.Equal("kb k-voice", vendor.QuerySelector(".kb")!.GetAttribute("class"));
        Assert.Contains("k-voice", vendor.QuerySelector(".mk-kt")!.ClassList);
        Assert.Equal("Live now", vendor.QuerySelector(".cd.live")!.TextContent.Trim());
        Assert.Contains("Alphin", vendor.QuerySelector(".mk-org")!.TextContent);

        var ict = cards[1];
        Assert.Equal("kb r-kalosi", ict.QuerySelector(".kb")!.GetAttribute("class"));
        Assert.Equal("Kalosi", ict.QuerySelector(".kb")!.TextContent.Trim());
        Assert.Equal("Starts in 10 min", ict.QuerySelector(".cd.soon")!.TextContent.Trim());
        Assert.Equal("+4", ict.QuerySelector(".mk-stack .more")!.TextContent.Trim());   // 7 people, 3 faces

        var sap = cards[2];
        Assert.Equal("kb k-person", sap.QuerySelector(".kb")!.GetAttribute("class"));
        Assert.Equal("You organise", sap.QuerySelector(".mk-org.mine")!.TextContent.Trim());
        Assert.Equal("Starts in 3 h 40 min", sap.QuerySelector(".cd")!.TextContent.Trim());

        Assert.Equal("Thu", cards[4].QuerySelector(".cd")!.TextContent.Trim());
        Assert.Equal("Mon 19 Oct", cards[5].QuerySelector(".cd")!.TextContent.Trim());
        Assert.Equal("13:00–15:30", cards[5].QuerySelector(".sch-time")!.TextContent.Trim());
        foreach (var nik in new[] { MeNik, AlphinNik }) Assert.DoesNotContain(nik, cut.Markup);
    }

    [Fact]
    public void Clicking_a_card_selects_it_and_the_selected_card_is_marked()
    {
        using var ctx = Context(new Api());
        var week = MockupWeek();
        var picked = new List<MeetingInfo>();
        var cut = List(ctx, week, selected: week[1].Id, picked: picked);
        Assert.Contains("sel", cut.FindAll(".sch-ev")[1].ClassList);
        Assert.Equal("true", cut.FindAll(".sch-ev")[1].GetAttribute("aria-current"));
        cut.FindAll(".sch-ev")[3].Click();
        Assert.Equal("Budget 2027 draft", Assert.Single(picked).Title);
        cut.FindAll(".sch-ev")[0].KeyDown("Enter");
        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void Search_filters_by_title_and_empty_says_so()
    {
        using var ctx = Context(new Api());
        var cut = ctx.Render<ScheduledList>(p => p.Add(c => c.Meetings, MockupWeek()).Add(c => c.Me, Me).Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib)
            .Add(c => c.Search, "budget"));
        Assert.Equal(["Budget 2027 draft"], cut.FindAll(".sch-title").Select(t => t.TextContent.Trim()).ToArray());

        var none = ctx.Render<ScheduledList>(p => p.Add(c => c.Meetings, Array.Empty<MeetingInfo>()).Add(c => c.Me, Me).Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib));
        Assert.Contains("No scheduled meetings yet.", none.Find(".sch-empty").TextContent);
    }

    // ---------------------------------------------------------------- past

    [Fact]
    public void Past_is_collapsed_then_expands_with_attendance_and_recordings_and_Show_more_pages()
    {
        var api = new Api();
        var p1 = M("Weekly ICT", At(6, 10), 60, "room", "kalosi");
        var p3 = M("Production review", At(8, 8, 30), 75, "room", "arabika");
        var p2 = M("Licence renewal call", At(9, 14), 40, "voice");
        var recId = Guid.NewGuid();
        api.PastPages.Enqueue(new PastMeetingsPageInfo(
            [new(p2, 3, []), new(p3, 9, [new(Guid.NewGuid(), At(8, 8, 31), 4320)]), new(p1, 5, [new(recId, At(6, 10, 1), 3120)])], 27, true));
        var older = M("Kick-off", At(1, 9), 60, "inperson");
        api.PastPages.Enqueue(new PastMeetingsPageInfo([new(older, 4, [])], 27, false));
        using var ctx = Context(api);
        var pickedPast = new List<PastMeetingInfo>();
        var cut = List(ctx, MockupWeek(), pickedPast: pickedPast);

        var head = cut.Find(".sch-past-h");
        Assert.Contains("closed", head.ClassList);
        Assert.Equal("false", head.GetAttribute("aria-expanded"));
        Assert.Contains("Past", head.TextContent);
        Assert.Contains("· 30 days", head.TextContent);
        Assert.Empty(api.Gets.Where(g => g.StartsWith("/api/meetings/past")));

        head.Click();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".sch-past .sch-ev").Count));
        Assert.Equal("/api/meetings/past?days=30", api.Gets.Single(g => g.StartsWith("/api/meetings/past")));
        var rows = cut.FindAll(".sch-past .sch-ev");
        Assert.All(rows, r => Assert.Contains("past", r.ClassList));
        Assert.Contains("· Fri 9 Oct", rows[0].QuerySelector(".sch-l1")!.TextContent);
        Assert.Equal("3 attended", rows[0].QuerySelector(".sch-att")!.TextContent.Trim());
        Assert.Empty(rows[0].QuerySelectorAll(".sch-rec"));
        var rec = rows[2].QuerySelector("a.sch-rec")!;
        Assert.Equal("Recording · 52 min", rec.TextContent.Trim());
        Assert.Equal($"/calls?tab=rooms&recording={recId}", rec.GetAttribute("href"));
        Assert.Empty(rows[0].QuerySelectorAll(".cd"));

        var more = cut.Find(".sch-more");
        Assert.Equal("Show 24 more", more.TextContent.Trim());
        more.Click();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll(".sch-past .sch-ev").Count));
        var second = api.Gets.Where(g => g.StartsWith("/api/meetings/past")).Last();
        var before = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + second).Query)["before"];
        Assert.Equal(p1.StartAt, DateTime.Parse(before!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.Empty(cut.FindAll(".sch-more"));

        cut.FindAll(".sch-past .sch-ev")[2].Click();
        Assert.Equal(5, Assert.Single(pickedPast).Attended);

        cut.Find(".sch-past-h").Click();
        Assert.Empty(cut.FindAll(".sch-past .sch-ev"));
    }

    // ---------------------------------------------------------------- schedule menu

    [Fact]
    public void Schedule_menu_offers_voice_call_and_meeting_room()
    {
        using var ctx = Context(new Api());
        var voice = 0; var room = 0; var closed = 0;
        var cut = ctx.Render<ScheduleMenu>(p => p.Add(c => c.OnVoice, () => voice++).Add(c => c.OnRoom, () => room++).Add(c => c.OnClose, () => closed++));
        Assert.Equal("menu", cut.Find(".sch-menu").GetAttribute("role"));
        Assert.Equal("Schedule", cut.Find(".sch-menu h4").TextContent.Trim());
        var opts = cut.FindAll(".sch-opt");
        Assert.Equal(2, opts.Count);
        Assert.Contains("Voice call", opts[0].TextContent);
        Assert.Contains("Audio only, join from anywhere", opts[0].TextContent);
        Assert.Contains("k-voice", opts[0].QuerySelector(".mk-kt")!.ClassList);
        Assert.Contains("Meeting room", opts[1].TextContent);
        Assert.Contains("Book one of 5 rooms · video, share, record", opts[1].TextContent);
        Assert.Contains("r-kalosi", opts[1].QuerySelector(".mk-kt")!.ClassList);
        opts[0].Click(); opts[1].Click();
        Assert.Equal((1, 1), (voice, room));
        cut.Find(".sch-menu").KeyDown("Escape");
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Voice_call_opens_the_calendar_form_on_the_voice_kind()
    {
        var url = new CalendarLinks(new PeopleLinks()).NewMeeting(kind: "voice", from: "From Calls");
        var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + url).Query);
        Assert.Equal(("1", "voice", "From Calls"), (q["new"], q["kind"], q["from"]));
    }

    // ---------------------------------------------------------------- detail additions

    [Fact]
    public void Past_detail_offers_Watch_recording_and_the_attendance()
    {
        using var ctx = Context(new Api());
        var m = M("Weekly ICT", At(6, 10), 60, "room", "kalosi");
        var recId = Guid.NewGuid();
        var model = MeetingDetailModel.FromMeeting(m, Me) with { Attended = 5, Recordings = [new(recId, At(6, 10, 1), 3120)] };
        var cut = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, model).Add(c => c.Now, NowUtc));
        Assert.Equal("Ended", cut.Find(".gcap.state").TextContent.Trim());
        var watch = cut.Find("a.bigjoin.watch");
        Assert.Contains("Watch recording", watch.TextContent);
        Assert.Equal($"/calls?tab=rooms&recording={recId}", watch.GetAttribute("href"));
        Assert.Contains("5 attended", cut.Find(".sub-h").TextContent);

        var bare = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, MeetingDetailModel.FromMeeting(m, Me)).Add(c => c.Now, NowUtc));
        Assert.Empty(bare.FindAll(".bigjoin"));
    }

    [Fact]
    public void Invitee_of_a_call_or_room_answers_in_the_detail()
    {
        using var ctx = Context(new Api());
        var answers = new List<string>();
        var m = M("Budget 2027 draft", At(14, 11), 60, "voice");
        var cut = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, MeetingDetailModel.FromMeeting(m, Me)).Add(c => c.Now, NowUtc)
            .Add(c => c.OnRespond, v => answers.Add(v)));
        var reply = cut.Find(".ir.reply");
        Assert.Contains("Your answer", reply.TextContent);
        Assert.Equal(["Going", "Maybe", "Can't go"], reply.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToArray());
        reply.QuerySelectorAll("button")[1].Click();
        Assert.Equal(["maybe"], answers);

        // The organizer has nothing to answer; in person answers in the header instead.
        var mine = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, MeetingDetailModel.FromMeeting(m with { OrganizerId = Me }, Me)).Add(c => c.Now, NowUtc)
            .Add(c => c.OnRespond, v => answers.Add(v)));
        Assert.Empty(mine.FindAll(".ir.reply"));
        var person = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, MeetingDetailModel.FromMeeting(M("Site visit", At(14, 9, 30), 60, "inperson"), Me))
            .Add(c => c.Now, NowUtc).Add(c => c.OnRespond, v => answers.Add(v)));
        Assert.Empty(person.FindAll(".ir.reply"));
        Assert.Single(person.FindAll(".joinbar .rsvp"));
    }

    // ---------------------------------------------------------------- fixture

    private static BunitContext Context(Api api)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
            new DirectoryEmployeeDto(MeNik, "Bagas Tyo", null, null, null, null, null, null, "me@x", null, null, "on", UserId: Me));
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }

    private sealed class Api : HttpMessageHandler
    {
        public Queue<PastMeetingsPageInfo> PastPages { get; } = new();
        public List<string> Gets { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get) lock (Gets) Gets.Add(request.RequestUri.PathAndQuery);
            if (path == "/api/meetings/past" && PastPages.TryDequeue(out var page))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(page, Web), Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
