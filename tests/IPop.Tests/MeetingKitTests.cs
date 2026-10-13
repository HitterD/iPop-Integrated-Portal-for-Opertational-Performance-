using System.Net;
using System.Text;
using Bunit;
using IPop.Web.Components.Meetings;
using IPop.Web.Models;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §1/§5 (mockup screens 1 + 4 detail): the shared kind badge, countdown, avatar stack and the
/// meeting detail used by Calls › Scheduled and Calendar.
[Collection("AvatarPhotos")]
public class MeetingKitTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Host = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string HostNik = "NIK778801", MeNik = "NIK778802";

    // ---------------------------------------------------------------- KindBadge / MeetingKinds

    [Theory]
    [InlineData("room", "kalosi", "kb r-kalosi", "video", "Kalosi")]
    [InlineData("room", "kapal-api", "kb r-kapal-api", "video", "Kapal Api")]
    [InlineData("voice", null, "kb k-voice", "headphones", "Voice call")]
    [InlineData("inperson", null, "kb k-person", "map-pin", "In person")]
    public void KindBadge_class_icon_and_label_per_kind(string kind, string? roomKey, string cls, string icon, string label)
    {
        using var ctx = Context(new Api());
        var cut = ctx.Render<KindBadge>(p => p.Add(c => c.Kind, kind).Add(c => c.RoomKey, roomKey));
        var kb = cut.Find("span.kb");
        Assert.Equal(cls, kb.GetAttribute("class"));
        Assert.Equal(icon, kb.QuerySelector("i")!.GetAttribute("data-lucide"));
        Assert.Equal(label, kb.TextContent.Trim());
        Assert.Equal(kind == "room", kb.QuerySelector(".sw") is not null);
    }

    [Fact]
    public void KindBadge_unknown_room_falls_back_to_voice_colour_and_null_kind_renders_nothing()
    {
        using var ctx = Context(new Api());
        var cut = ctx.Render<KindBadge>(p => p.Add(c => c.Kind, "room").Add(c => c.RoomKey, "gayo"));
        Assert.Equal("kb k-voice", cut.Find("span.kb").GetAttribute("class"));
        Assert.Equal("Meeting room", cut.Find("span.kb").TextContent.Trim());
        Assert.Empty(cut.FindAll(".sw"));

        var named = ctx.Render<KindBadge>(p => p.Add(c => c.Kind, "room").Add(c => c.RoomKey, "toraja").Add(c => c.RoomName, "Toraja hall"));
        Assert.Equal("Toraja hall", named.Find("span.kb").TextContent.Trim());

        Assert.Empty(ctx.Render<KindBadge>(p => p.Add(c => c.Kind, null)).Markup.Trim());
        Assert.Empty(ctx.Render<KindBadge>(p => p.Add(c => c.Kind, "weird")).Markup.Trim());
    }

    [Fact]
    public void KindBadge_compact_hides_the_label_but_keeps_it_accessible()
    {
        using var ctx = Context(new Api());
        var kb = ctx.Render<KindBadge>(p => p.Add(c => c.Kind, "voice").Add(c => c.Compact, true)).Find("span.kb");
        Assert.Contains("compact", kb.GetAttribute("class"));
        Assert.Equal("", kb.TextContent.Trim());
        Assert.Equal("Voice call", kb.GetAttribute("title"));
        Assert.Equal("Voice call", kb.GetAttribute("aria-label"));
    }

    [Fact]
    public void MeetingKinds_helpers()
    {
        Assert.Equal("r-toraja", MeetingKinds.CssClass("room", "toraja"));
        Assert.Equal("k-voice", MeetingKinds.CssClass("room", null));
        Assert.Equal("k-event", MeetingKinds.CssClass(null, null));
        Assert.Equal("calendar", MeetingKinds.Icon(null));
        Assert.Equal("Kapal Api", MeetingKinds.RoomName("kapal-api"));
        Assert.Null(MeetingKinds.RoomName("gayo"));
        Assert.Equal("1 h 30 min", MeetingKinds.Duration(TimeSpan.FromMinutes(90)));
        Assert.Equal("40 min", MeetingKinds.Duration(TimeSpan.FromMinutes(40)));
        Assert.Equal("2 h", MeetingKinds.Duration(TimeSpan.FromHours(2)));
        var today = new DateTime(2026, 10, 13);
        Assert.Equal("Today, Tuesday 13 October", MeetingKinds.DateLabel(today.AddHours(10), today));
        Assert.Equal("Wednesday 14 October", MeetingKinds.DateLabel(today.AddDays(1).AddHours(9), today));
        Assert.Equal("Friday 8 January 2027", MeetingKinds.DateLabel(new DateTime(2027, 1, 8, 9, 0, 0), today));
    }

    // ---------------------------------------------------------------- Countdown

    /// Tue 13 Oct 2026 08:00 WIB.
    private static readonly DateTime NowUtc = new(2026, 10, 13, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Countdown_labels()
    {
        Assert.Equal(new CallsSchedule.CountdownLabel("Live now", "live"),
            CallsSchedule.Countdown(NowUtc.AddMinutes(-5), NowUtc.AddMinutes(55), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Starts in 10 min", "soon"),
            CallsSchedule.Countdown(NowUtc.AddMinutes(10), NowUtc.AddMinutes(70), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Starts in 15 min", "soon"),
            CallsSchedule.Countdown(NowUtc.AddMinutes(15), NowUtc.AddMinutes(70), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Starts in 1 min", "soon"),
            CallsSchedule.Countdown(NowUtc.AddSeconds(20), NowUtc.AddMinutes(70), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Starts in 3 h 20 min", "later"),
            CallsSchedule.Countdown(NowUtc.AddMinutes(200), NowUtc.AddMinutes(260), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Starts in 2 h", "later"),
            CallsSchedule.Countdown(NowUtc.AddHours(2), NowUtc.AddHours(3), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Tomorrow", "later"),
            CallsSchedule.Countdown(NowUtc.AddDays(1), NowUtc.AddDays(1).AddHours(1), NowUtc, Wib));
        // Later this week: weekday; further: "Mon 19 Oct".
        Assert.Equal(new CallsSchedule.CountdownLabel("Thu", "later"),
            CallsSchedule.Countdown(NowUtc.AddDays(2), NowUtc.AddDays(2).AddHours(1), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Mon 19 Oct", "later"),
            CallsSchedule.Countdown(NowUtc.AddDays(6), NowUtc.AddDays(6).AddHours(1), NowUtc, Wib));
        Assert.Equal(new CallsSchedule.CountdownLabel("Ended", "ended"),
            CallsSchedule.Countdown(NowUtc.AddHours(-2), NowUtc.AddHours(-1), NowUtc, Wib));
    }

    [Fact]
    public void MeetingCountdown_renders_class_per_state()
    {
        using var ctx = Context(new Api());
        Render(NowUtc.AddMinutes(-5), "cd live", "Live now");
        Render(NowUtc.AddMinutes(10), "cd soon", "Starts in 10 min");
        Render(NowUtc.AddDays(1), "cd", null);
        var ended = ctx.Render<MeetingCountdown>(p => p.Add(c => c.Start, NowUtc.AddHours(-2)).Add(c => c.End, NowUtc.AddHours(-1)).Add(c => c.Now, NowUtc));
        Assert.Empty(ended.FindAll(".cd"));

        void Render(DateTime start, string cls, string? text)
        {
            var cut = ctx.Render<MeetingCountdown>(p => p.Add(c => c.Start, start).Add(c => c.End, start.AddHours(1)).Add(c => c.Now, NowUtc));
            var cd = cut.Find("span.cd");
            Assert.Equal(cls, cd.GetAttribute("class"));
            if (text is not null) Assert.Equal(text, cd.TextContent.Trim());
        }
    }

    // ---------------------------------------------------------------- AvatarStack

    [Fact]
    public void AvatarStack_shows_up_to_four_and_a_more_count()
    {
        using var ctx = Context(new Api());
        var people = Enumerable.Range(1, 6).Select(i => new MeetingPersonRef($"NIKX{i}", $"Person {i}")).ToList();
        var cut = ctx.Render<AvatarStack>(p => p.Add(c => c.People, people));
        Assert.Equal(4, cut.FindAll(".mk-stack .pav").Count);
        Assert.Equal("+2", cut.Find(".mk-stack .more").TextContent.Trim());
        var withExtra = ctx.Render<AvatarStack>(p => p.Add(c => c.People, people.Take(3).ToList()).Add(c => c.Extra, 5));
        Assert.Equal(3, withExtra.FindAll(".pav").Count);
        Assert.Equal("+5", withExtra.Find(".more").TextContent.Trim());
        var few = ctx.Render<AvatarStack>(p => p.Add(c => c.People, people.Take(2).ToList()));
        Assert.Empty(few.FindAll(".more"));
        Assert.DoesNotContain("NIKX", cut.Markup);
    }

    // ---------------------------------------------------------------- MeetingDetail

    private static readonly Guid MeetingId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private static MeetingInfo Meeting(string kind, DateTime start, int minutes = 60, Guid? organizer = null, string status = "aktif",
        string? agenda = "Ports, switches, Wi-Fi", int? reminder = 10)
    {
        var org = organizer ?? Host;
        var location = kind switch { "room" => "SJA Hall · Kalosi", "voice" => "Online · iPop Meeting", _ => "ICT office, 2nd floor" };
        List<MeetingAttendeeInfo> people =
        [
            new(Host, "Alphin Host", HostNik, true, "accept", null),
            new(Me, "Bagas Me", MeNik, true, org == Me ? "accept" : "pending", null),
            new(Guid.NewGuid(), "Citra Maybe", "NIK778803", true, "maybe", null),
            new(Guid.NewGuid(), "Dodi Pending", "NIK778804", true, "pending", null),
            new(Guid.NewGuid(), "Eka Declined", "NIK778805", false, "decline", null),
        ];
        return new MeetingInfo(MeetingId, "Weekly ICT", start, start.AddMinutes(minutes), null, location, null, org,
            org == Me ? "Bagas Me" : "Alphin Host", org == Me ? MeNik : HostNik, status, people, 2, 1, 1, 2,
            org == Me ? "accept" : "pending", agenda, "meeting-" + MeetingId.ToString("N"), null, reminder, null, kind,
            kind == "room" ? "kalosi" : null);
    }

    private static IRenderedComponent<MeetingDetail> Detail(BunitContext ctx, MeetingInfo m, DateTime now, Guid? me = null,
        Action<ComponentParameterCollectionBuilder<MeetingDetail>>? more = null) =>
        ctx.Render<MeetingDetail>(p =>
        {
            p.Add(c => c.Model, MeetingDetailModel.FromMeeting(m, me ?? Me)).Add(c => c.Now, now)
             .Add(c => c.OnEdit, () => { }).Add(c => c.OnCancel, () => { }).Add(c => c.OnOpenCalendar, () => { }).Add(c => c.OnChangeRoom, () => { });
            more?.Invoke(p);
        });

    [Fact]
    public void Join_is_disabled_with_a_caption_15_minutes_before_and_enabled_at_9()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var early = Detail(ctx, Meeting("room", start), start.AddMinutes(-15));
        var join = early.Find(".joinbar .bigjoin");
        Assert.True(join.HasAttribute("disabled"));
        Assert.Contains("Join Kalosi", join.TextContent);
        Assert.Equal("Join opens 10 min before", join.QuerySelector("small")!.TextContent.Trim());

        var open = Detail(ctx, Meeting("room", start), start.AddMinutes(-9));
        var link = open.Find("a.bigjoin");
        Assert.Equal("/calls?tab=rooms&join=kalosi", link.GetAttribute("href"));
        Assert.Contains("Join Kalosi", link.TextContent);

        var voice = Detail(ctx, Meeting("voice", start), start.AddMinutes(-9));
        var vj = voice.Find("a.bigjoin");
        Assert.Equal("/meet/meeting-" + MeetingId.ToString("N"), vj.GetAttribute("href"));
        Assert.Contains("Join call", vj.TextContent);
    }

    [Fact]
    public void Join_uses_OnJoin_when_given()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var joined = 0;
        var cut = Detail(ctx, Meeting("voice", start), start.AddMinutes(-5), more: p => p.Add(c => c.OnJoin, () => joined++));
        cut.Find("button.bigjoin").Click();
        Assert.Equal(1, joined);
    }

    [Fact]
    public void In_person_has_no_join_and_ended_or_cancelled_has_no_join()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var person = Detail(ctx, Meeting("inperson", start), start.AddMinutes(-2));
        Assert.Empty(person.FindAll(".bigjoin"));
        Assert.Contains("ICT office, 2nd floor", person.Find(".ir.where").TextContent);
        Assert.Contains("In person · no call link", person.Markup);

        var ended = Detail(ctx, Meeting("room", start), start.AddHours(2));
        Assert.Empty(ended.FindAll(".bigjoin"));
        Assert.Equal("Ended", ended.Find(".band-top .gcap.state").TextContent.Trim());

        var cancelled = Detail(ctx, Meeting("room", start, status: "cancelled"), start.AddMinutes(-2));
        Assert.Empty(cancelled.FindAll(".bigjoin"));
        Assert.Equal("Cancelled", cancelled.Find(".band-top .gcap.state").TextContent.Trim());
        Assert.Empty(cancelled.FindAll(".act.edit"));
    }

    /// QA Task 11: a past meeting (Calls › Past) offered Cancel and Change room; neither means anything once it ended.
    [Fact]
    public void Ended_meeting_offers_no_cancel_and_no_room_change()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(-2);
        var cut = Detail(ctx, Meeting("room", start, organizer: Me), start.AddHours(3));
        Assert.Equal("Ended", cut.Find(".band-top .gcap.state").TextContent.Trim());
        Assert.Empty(cut.FindAll(".act.cancel"));
        Assert.Empty(cut.FindAll(".ir .change"));
        Assert.Single(cut.FindAll(".act.edit"));
    }

    /// QA Task 11: the action row drew empty circles — its icons were <span data-lucide>, and ipop.icons only renders
    /// <i data-lucide>.
    [Fact]
    public void Action_icons_are_i_elements_that_ipop_icons_renders()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var cut = Detail(ctx, Meeting("voice", start, organizer: Me), DateTime.UtcNow);
        var acts = cut.FindAll(".acts .act");
        Assert.NotEmpty(acts);
        Assert.All(acts, a => Assert.NotNull(a.QuerySelector("i i[data-lucide]")));
        Assert.Empty(cut.FindAll("span[data-lucide]"));
    }

    [Fact]
    public void Header_has_kind_colour_caption_title_date_and_status()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddMinutes(10);
        var cut = Detail(ctx, Meeting("room", start), DateTime.UtcNow);
        Assert.Contains("r-kalosi", cut.Find(".band").GetAttribute("class"));
        Assert.Equal("Meeting room · Kalosi", cut.Find(".band-top .gcap").TextContent.Trim());
        Assert.Equal("Weekly ICT", cut.Find(".band h2").TextContent.Trim());
        Assert.Contains(start.ToLocalTime().ToString("HH:mm"), cut.Find(".band p").TextContent);
        Assert.Equal("Starts in 10 min", cut.Find(".band-top .gcap.state").TextContent.Trim());
        Assert.Contains("soon", cut.Find(".band-top .gcap.state").GetAttribute("class"));

        var voice = Detail(ctx, Meeting("voice", DateTime.UtcNow.AddMinutes(-5)), DateTime.UtcNow);
        Assert.Contains("k-voice", voice.Find(".band").GetAttribute("class"));
        Assert.Equal("Voice call", voice.Find(".band-top .gcap").TextContent.Trim());
        Assert.Equal("Live now", voice.Find(".band-top .gcap.state").TextContent.Trim());
        Assert.Contains("live", voice.Find(".band-top .gcap.state").GetAttribute("class"));

        var person = Detail(ctx, Meeting("inperson", DateTime.UtcNow.AddDays(2)), DateTime.UtcNow);
        Assert.Contains("k-person", person.Find(".band").GetAttribute("class"));
    }

    [Fact]
    public void Edit_cancel_and_change_only_for_the_organizer_message_host_only_for_others()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var attendee = Detail(ctx, Meeting("room", start), DateTime.UtcNow);
        Assert.Empty(attendee.FindAll(".act.edit"));
        Assert.Empty(attendee.FindAll(".act.cancel"));
        Assert.Empty(attendee.FindAll(".ir .change"));
        Assert.Single(attendee.FindAll(".act.host"));
        Assert.Single(attendee.FindAll(".act.calendar"));
        Assert.Single(attendee.FindAll(".act.copy"));

        int edits = 0, cancels = 0, changes = 0, opens = 0;
        var organizer = ctx.Render<MeetingDetail>(p => p
            .Add(c => c.Model, MeetingDetailModel.FromMeeting(Meeting("room", start, organizer: Me), Me)).Add(c => c.Now, DateTime.UtcNow)
            .Add(c => c.OnEdit, () => edits++).Add(c => c.OnCancel, () => cancels++)
            .Add(c => c.OnChangeRoom, () => changes++).Add(c => c.OnOpenCalendar, () => opens++));
        Assert.Empty(organizer.FindAll(".act.host"));
        organizer.Find(".act.edit").Click();
        organizer.Find(".act.cancel").Click();
        organizer.Find(".ir .change").Click();
        organizer.Find(".act.calendar").Click();
        Assert.Equal((1, 1, 1, 1), (edits, cancels, changes, opens));
        Assert.Contains("Bagas Me (you)", organizer.Find(".ir.organizer").TextContent);
    }

    [Fact]
    public void Rows_show_time_duration_repeat_agenda_and_reminder()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        var model = MeetingDetailModel.FromMeeting(Meeting("room", start, minutes: 90), Me) with { RepeatText = "Weekly on Tuesday" };
        var cut = ctx.Render<MeetingDetail>(p => p.Add(c => c.Model, model).Add(c => c.Now, DateTime.UtcNow));
        var when = cut.Find(".ir.when").TextContent;
        Assert.Contains("1 h 30 min", when);
        Assert.Contains("Weekly on Tuesday", when);
        Assert.Contains("Ports, switches, Wi-Fi", cut.Find(".ir.agenda").TextContent);
        Assert.Contains("10 min before", cut.Find(".ir.reminder").TextContent);
        Assert.Contains("Meeting room · up to 20 people", cut.Find(".ir.where").TextContent);

        var bare = Detail(ctx, Meeting("voice", start, agenda: null, reminder: null), DateTime.UtcNow);
        Assert.Empty(bare.FindAll(".ir.agenda"));
        Assert.Empty(bare.FindAll(".ir.reminder"));
        Assert.Contains("Audio only · join from anywhere, PC or phone", bare.Find(".ir.where").TextContent);
    }

    [Fact]
    public void People_are_grouped_going_no_response_declined_and_no_nik_is_rendered()
    {
        var api = new Api();
        api.Meetings[MeetingId] = $$"""{"id":"{{MeetingId}}","organizerId":"{{Host}}","roomPassword":"482917"}""";
        using var ctx = Context(api, Me);
        var cut = Detail(ctx, Meeting("room", DateTime.UtcNow.AddDays(1)), DateTime.UtcNow);
        var heads = cut.FindAll(".pgh").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(["Going · 2", "No response · 2", "Declined · 1"], heads);
        Assert.Contains("5 invited", cut.Find(".sub-h").TextContent);
        Assert.Contains("Bagas Me (you)", cut.Markup);
        Assert.Single(cut.FindAll(".prow.dim"));
        cut.WaitForAssertion(() => Assert.Contains("482 917", cut.Markup));
        foreach (var nik in new[] { HostNik, MeNik, "NIK778803", "NIK778804", "NIK778805" })
            Assert.DoesNotContain(nik, cut.Markup);
    }

    [Fact]
    public void Password_box_only_for_the_organizer_and_attendees_of_a_room_meeting()
    {
        var api = new Api();
        api.Meetings[MeetingId] = $$"""{"id":"{{MeetingId}}","organizerId":"{{Host}}","roomPassword":"482917"}""";
        using var ctx = Context(api, Me);
        var start = DateTime.UtcNow.AddDays(1);
        var invited = Detail(ctx, Meeting("room", start), DateTime.UtcNow);
        invited.WaitForAssertion(() => Assert.Single(invited.FindAll(".mrt-pw-box")));

        // Someone who opens it through a calendar share but is not on the invite: no box, no request.
        var outsider = Guid.NewGuid();
        var gets = api.MeetingGets;
        var cut = Detail(ctx, Meeting("room", start), DateTime.UtcNow, me: outsider);
        Thread.Sleep(80);
        Assert.Empty(cut.FindAll(".mrt-pw-box"));
        Assert.Equal(gets, api.MeetingGets);

        // Declined: not shown either. Voice: never.
        var declined = Meeting("room", start) is var m ? m with { Attendees = m.Attendees.Select(a => a.UserId == Me ? a with { RsvpStatus = "decline" } : a).ToList() } : null;
        Assert.False(MeetingDetailModel.FromMeeting(declined!, Me).ShowsPassword);
        Assert.False(MeetingDetailModel.FromMeeting(Meeting("voice", start), Me).ShowsPassword);
        Assert.True(MeetingDetailModel.FromMeeting(Meeting("room", start, organizer: Me), Me).ShowsPassword);
    }

    [Fact]
    public void Model_from_a_calendar_event_detail()
    {
        var start = DateTime.UtcNow.AddDays(1);
        var mid = Guid.NewGuid();
        var e = new Calendar.EventDetail(Guid.NewGuid(), "Weekly ICT", start, start.AddHours(1), false, "SJA Hall · Toraja", null, "Agenda text", 15,
            null, null, mid, null, "Alphin Host", true,
            [new Calendar.Person(Host, HostNik, "Alphin Host", "accept"), new Calendar.Person(Me, MeNik, "Bagas Me", "pending")],
            OwnerNik: HostNik, RecurrenceText: "Weekly on Tuesday", MeetingKind: "room", RoomKey: "toraja");
        var m = MeetingDetailModel.FromEvent(e, Me);
        Assert.Equal(("room", "toraja", "Agenda text", "Weekly on Tuesday", 15), (m.Kind, m.RoomKey, m.Agenda, m.RepeatText, m.ReminderMinutes));
        Assert.Equal("meeting-" + mid.ToString("N"), m.JoinRoomName);
        Assert.True(m.CanManage);
        Assert.True(m.ShowsPassword);
        Assert.Equal(HostNik, m.OrganizerKey);

        // Old row without a kind: inferred from the place.
        var old = MeetingDetailModel.FromEvent(e with { MeetingKind = null, RoomKey = null, Location = "Online · iPop Meeting" }, Me);
        Assert.Equal("voice", old.Kind);
        var oldRoom = MeetingDetailModel.FromEvent(e with { MeetingKind = null, RoomKey = null }, Me);
        Assert.Equal(("room", "toraja"), (oldRoom.Kind, oldRoom.RoomKey));
    }

    [Fact]
    public void Close_button_only_when_asked()
    {
        using var ctx = Context(new Api());
        var start = DateTime.UtcNow.AddDays(1);
        Assert.Empty(Detail(ctx, Meeting("voice", start), DateTime.UtcNow).FindAll(".xb"));
        var closed = 0;
        var cut = Detail(ctx, Meeting("voice", start), DateTime.UtcNow, more: p => p.Add(c => c.OnClose, () => closed++));
        cut.Find(".xb").Click();
        Assert.Equal(1, closed);
    }

    // ---------------------------------------------------------------- fixture

    private static BunitContext Context(Api api, Guid? me = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var auth = new AuthState(http, ctx.JSInterop.JSRuntime);
        if (me is { } id)
            typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
                new DirectoryEmployeeDto("ME", "Me", null, null, null, null, null, null, "me@x", null, null, "on", UserId: id));
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
        public Dictionary<Guid, string> Meetings { get; } = [];
        private int meetingGets;
        public int MeetingGets => Volatile.Read(ref meetingGets);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var seg = request.RequestUri!.AbsolutePath.Split('/');
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.StartsWith("/api/meetings/") && Guid.TryParse(seg[^1], out var mid))
            {
                Interlocked.Increment(ref meetingGets);
                return Task.FromResult(Meetings.TryGetValue(mid, out var m)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(m, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// QA Task 11: a clash whose title the viewer may not see (the server sends "Booked") read "Booked by “Booked”".
    [Theory]
    [InlineData("Finance sync", "Booked by “Finance sync”")]
    [InlineData("Booked", "Already booked")]
    [InlineData(null, "Already booked")]
    public void Clash_owner_text_hides_the_placeholder_title(string? bookedBy, string expected) =>
        Assert.Equal(expected, MeetingKinds.BookedByText(bookedBy));
}
