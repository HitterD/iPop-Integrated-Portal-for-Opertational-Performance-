using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using IPop.Web.Components.Meetings;
using IPop.Web.Models;
using IPop.Web.Pages;
using IPop.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 Task 10 (mockup "Phone"): the booking sheet becomes a bottom sheet when the existing phone
/// check (ipop.mobile.isMobile, max 767 px) says so; Calls › Scheduled shows list and detail as separate screens, the
/// detail with a "Scheduled" back button that returns to the list.
[Collection("AvatarPhotos")]
public class PhoneSchedulingTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static readonly Guid Me = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static DateTime At(int h, int m = 0) => new DateTime(2026, 10, 13, h, m, 0, DateTimeKind.Utc).AddHours(-7);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---------------------------------------------------------------- booking sheet

    private static IRenderedComponent<BookingSheet> Sheet(BunitContext ctx) =>
        ctx.Render<BookingSheet>(p => p.Add(c => c.RoomKey, "kalosi").Add(c => c.Mode, BookingSheet.ModeBook).Add(c => c.PrefillStart, At(13))
            .Add(c => c.Now, At(9, 50)).Add(c => c.Zone, Wib).Add(c => c.RefreshDelay, TimeSpan.Zero));

    private static BookingSheetTests.Api SheetApi()
    {
        var api = new BookingSheetTests.Api();
        api.Days["kalosi"] = new("kalosi", "Kalosi", 20, new DateOnly(2026, 10, 13), At(7), At(19), []);
        return api;
    }

    [Fact]
    public void Booking_sheet_is_a_bottom_sheet_with_a_grab_handle_on_a_phone()
    {
        using var ctx = BookingSheetTests.Context(SheetApi());
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(true);
        var cut = Sheet(ctx);
        cut.WaitForAssertion(() => Assert.Contains("bottom", cut.Find("section.sheet2").ClassList));
        Assert.Contains("bottom", cut.Find(".sheet2-scrim").ClassList);
        Assert.Single(cut.FindAll("section.sheet2 > .sh-grab"));
        // The mockup's phone footer has no "More options…": Cancel + Book fill the width.
        Assert.Empty(cut.FindAll(".bs-more"));
        Assert.Equal("Book", cut.Find(".bs-go").TextContent.Trim());
    }

    [Fact]
    public void Booking_sheet_stays_centred_on_a_desktop()
    {
        using var ctx = BookingSheetTests.Context(SheetApi());
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(false);
        var cut = Sheet(ctx);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".tl-track")));
        Assert.DoesNotContain("bottom", cut.Find("section.sheet2").ClassList);
        Assert.Empty(cut.FindAll(".sh-grab"));
        Assert.Single(cut.FindAll(".bs-more"));
    }

    // ---------------------------------------------------------------- Calls › Scheduled

    private static MeetingInfo Meeting(string title, DateTime start) =>
        new(Guid.NewGuid(), title, start, start.AddHours(1), null, "Online · iPop Meeting", null, Me, "Bagas Tyo", "NIK990001", "aktif",
            [new(Me, "Bagas Tyo", "NIK990001", true, "accept", null)], 1, 0, 0, 0, "accept", null, "meeting-x", null, 10, null, "voice");

    private static IRenderedComponent<Calls> Page(BunitContext ctx) => ctx.Render<Calls>();

    [Fact]
    public void Scheduled_on_a_phone_opens_the_detail_as_its_own_screen_and_Back_returns_to_the_list()
    {
        using var ctx = CallsContext(new CallsApi { Upcoming = [Meeting("Weekly ICT", DateTime.UtcNow.AddHours(2))] }, phone: true);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sch-ev")));
        Assert.DoesNotContain("m-detail", cut.Find(".calls-v6").ClassList);

        cut.Find(".sch-ev").Click();
        cut.WaitForAssertion(() => Assert.Contains("m-detail", cut.Find(".calls-v6").ClassList));
        Assert.Equal("Weekly ICT", cut.Find(".mk-detail .band h2").TextContent.Trim());
        var back = cut.Find(".sch-mnav .sch-back");
        Assert.Equal("Scheduled", back.TextContent.Trim());
        Assert.Single(ctx.JSInterop.Invocations["ipop.mobile.pushDetail"]);

        back.Click();
        cut.WaitForAssertion(() => Assert.DoesNotContain("m-detail", cut.Find(".calls-v6").ClassList));
        Assert.Single(ctx.JSInterop.Invocations["ipop.mobile.popDetail"]);
        Assert.Single(cut.FindAll(".sch-ev"));
    }

    /// QA Task 11: leaving /calls for the Calendar form ("More options…" in the booking sheet) must not be answered by the
    /// Calls page's own deep-link handler: it saw "new=1" in the Calendar address and re-navigated to a blank form.
    [Fact]
    public void Leaving_Calls_for_a_prefilled_Calendar_form_keeps_that_address()
    {
        using var ctx = CallsContext(new CallsApi(), phone: false);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".calls-v6")));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        const string target = "/calendar?new=1&kind=room&room=kalosi&title=Standup";
        nav.NavigateTo(target);
        cut.WaitForState(() => true);
        Assert.EndsWith(target, nav.Uri);
    }

    /// QA Task 11: Edit in the Scheduled detail opened the Calendar's read-only detail, so the organizer had to press Edit
    /// again; it now asks Calendar to open the form (edit=1). Open in Calendar keeps the detail.
    [Theory]
    [InlineData(".act.edit", true)]
    [InlineData(".act.calendar", false)]
    public void Edit_opens_the_Calendar_form_and_Open_in_Calendar_the_detail(string button, bool edit)
    {
        var start = DateTime.UtcNow.AddHours(2);
        var m = Meeting("Weekly ICT", start);
        var evId = Guid.NewGuid();
        var api = new CallsApi { Upcoming = [m], EventRef = (m.Id, evId, start) };
        using var ctx = CallsContext(api, phone: false);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sch-ev")));
        cut.Find(".sch-ev").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".mk-detail " + button)));
        cut.Find(".mk-detail " + button).Click();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(() => Assert.Contains($"/calendar?event={evId}", nav.Uri));
        Assert.Equal(edit, nav.Uri.Contains("edit=1"));
    }

    [Fact]
    public void Phone_Back_from_the_browser_closes_the_detail_too()
    {
        using var ctx = CallsContext(new CallsApi { Upcoming = [Meeting("Weekly ICT", DateTime.UtcNow.AddHours(2))] }, phone: true);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sch-ev")));
        cut.Find(".sch-ev").Click();
        cut.WaitForAssertion(() => Assert.Contains("m-detail", cut.Find(".calls-v6").ClassList));
        cut.InvokeAsync(() => cut.Instance.CloseMobileDetail());
        cut.WaitForAssertion(() => Assert.DoesNotContain("m-detail", cut.Find(".calls-v6").ClassList));
    }

    [Fact]
    public void Scheduled_on_a_desktop_keeps_list_and_detail_side_by_side()
    {
        using var ctx = CallsContext(new CallsApi { Upcoming = [Meeting("Weekly ICT", DateTime.UtcNow.AddHours(2))] }, phone: false);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sch-ev")));
        cut.Find(".sch-ev").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".mk-detail")));
        Assert.DoesNotContain("m-detail", cut.Find(".calls-v6").ClassList);
    }

    /// v3 gaps: Schedule → Voice call opens the short voice form on the Calls page instead of leaving for Calendar.
    [Fact]
    public void Schedule_Voice_call_opens_the_short_form_and_saving_reloads_Scheduled()
    {
        var api = new CallsApi();
        using var ctx = CallsContext(api, phone: false);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".sch-btn")));
        cut.Find(".sch-btn").Click();
        cut.FindAll(".sch-opt")[0].Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("section.vc-sheet")));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        Assert.Contains("/calls", nav.Uri);
        Assert.Empty(cut.FindAll(".sch-menu"));
        var before = api.UpcomingGets;
        cut.Find("input.vc-name").Input("Budget");
        cut.Find(".vc-go").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("section.vc-sheet")));
        Assert.True(api.UpcomingGets > before);
        Assert.Equal("voice", JsonDocument.Parse(api.Posts.Single()).RootElement.GetProperty("kind").GetString());
    }

    /// v3 gaps (mockup "Phone · Scheduled list"): large title, Recents / Scheduled / Rooms segmented control, icon buttons
    /// for search / test devices / new call, and a floating Schedule button opening the same Voice call / Meeting room menu.
    /// The phone chrome is rendered once and shown only at ≤ 767 px by CSS (like .m-back), so desktop is unchanged.
    [Fact]
    public void Phone_header_has_a_large_title_segmented_tabs_and_a_floating_Schedule_button()
    {
        using var ctx = CallsContext(new CallsApi { Upcoming = [Meeting("Weekly ICT", DateTime.UtcNow.AddHours(2))] }, phone: true);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".sch-ev")));
        Assert.Equal("Calls & Meetings", cut.Find(".cl-ptitle").TextContent.Trim());
        Assert.Equal(["Recents", "Scheduled", "Rooms"], cut.FindAll(".calls-tabs button .cl-seg-short").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Equal("tablist", cut.Find(".calls-tabs").GetAttribute("role"));
        Assert.Equal("true", cut.FindAll(".calls-tabs button")[1].GetAttribute("aria-selected"));
        Assert.Equal(["Search calls", "Test devices", "New call"], cut.FindAll(".cl-pnav button").Select(b => b.GetAttribute("aria-label")).ToArray());

        var fab = cut.Find("button.cl-pfab");
        Assert.Equal("Schedule", fab.TextContent.Trim());
        Assert.Equal("menu", fab.GetAttribute("aria-haspopup"));
        fab.Click();
        Assert.Equal(2, cut.FindAll(".sch-menu .sch-opt").Count);
        cut.FindAll(".sch-opt")[0].Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("section.vc-sheet")));
    }

    [Fact]
    public void Phone_search_button_reveals_the_search_field()
    {
        using var ctx = CallsContext(new CallsApi(), phone: true);
        var cut = Page(ctx);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".cl-pnav")));
        Assert.DoesNotContain("cl-psearch", cut.Find(".calls-v6").ClassList);
        cut.Find(".cl-pnav button[aria-label='Search calls']").Click();
        Assert.Contains("cl-psearch", cut.Find(".calls-v6").ClassList);
    }

    [Fact]
    public void Phone_chrome_is_hidden_on_desktop_and_shown_by_the_phone_media_query()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(Path.Combine(dir, "src", "IPop.Web"))) dir = Path.GetDirectoryName(dir);
        var css = File.ReadAllText(Path.Combine(dir!, "src/IPop.Web/wwwroot/css/v6/calls.css"));
        var desktop = css[..css.IndexOf("/* Phone (v3 gaps", StringComparison.Ordinal)];
        Assert.Matches(@"\.cl-pnav, \.cl-ptitle, \.cl-pfab, \.cl-seg-short \{ display:none; \}", desktop);
        var phone = css[css.IndexOf("/* Phone (v3 gaps", StringComparison.Ordinal)..];
        Assert.Contains("@media (max-width: 767px)", phone);
        foreach (var sel in new[] { ".cl-ptitle", ".cl-pfab", ".calls-tabs", ".cl-pnav" }) Assert.Contains(sel, phone);
    }

    // ---------------------------------------------------------------- fixture

    private static BunitContext CallsContext(CallsApi api, bool phone)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("ipop.mobile.pushDetail", _ => true).SetResult(phone);
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(phone);
        var http = new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
        var js = ctx.JSInterop.JSRuntime;
        var auth = new AuthState(http, js);
        typeof(AuthState).GetProperty(nameof(AuthState.Me))!.SetValue(auth,
            new DirectoryEmployeeDto("NIK990001", "Bagas Tyo", null, null, null, null, null, null, "me@x", null, null, "on", UserId: Me));
        var toasts = new ToastService();
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(toasts);
        ctx.Services.AddSingleton(new CallState(auth, http, js));
        ctx.Services.AddSingleton(new NotificationState(auth, http, toasts, js));
        ctx.Services.AddSingleton(new BadgeState(auth));
        ctx.Services.AddSingleton(new ConfirmService());
        ctx.Services.AddSingleton<PeopleLinks>();
        ctx.Services.AddSingleton<CalendarLinks>();
        ctx.Services.AddSingleton(new InviteApi(auth));
        ctx.Services.AddSingleton(new MeetingDraftHandoff());
        ctx.Services.AddScoped<PhotoViewerState>();
        ctx.Services.AddScoped<OverlayStack>();
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/calls?tab=scheduled");
        return ctx;
    }

    /// QA Task 11 (390 px): phone inputs are 16 px (no iOS zoom), so the desktop 92 px time field cut "17:00" to "17:…".
    [Fact]
    public void Phone_calendar_form_time_fields_are_wide_enough_for_HH_mm_at_16px()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "IPop.slnx")) && !System.IO.Directory.Exists(Path.Combine(dir, "src", "IPop.Web"))) dir = Path.GetDirectoryName(dir);
        var css = File.ReadAllText(Path.Combine(dir!, "src/IPop.Web/wwwroot/css/mobile/calendar.css"));
        var m = System.Text.RegularExpressions.Regex.Match(css, @"\.cal-row-ctl \.dp-time\s*\{[^}]*width:\s*(\d+)px");
        Assert.True(m.Success, "mobile calendar.css sets no .cal-row-ctl .dp-time width");
        Assert.True(int.Parse(m.Groups[1].Value) >= 108, $"time field {m.Groups[1].Value}px is too narrow for 16 px HH:mm + clock icon");
    }

    private sealed class CallsApi : HttpMessageHandler
    {
        public List<MeetingInfo> Upcoming { get; set; } = [];
        public (Guid MeetingId, Guid EventId, DateTime Start)? EventRef { get; set; }
        public int UpcomingGets;
        public List<string> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/calendar/events")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (Posts) Posts.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
            return await Answer(request);
        }

        private Task<HttpResponseMessage> Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/meetings/upcoming") Interlocked.Increment(ref UpcomingGets);
            if (request.Method == HttpMethod.Get && path == "/api/meetings/upcoming")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(Upcoming, Web), Encoding.UTF8, "application/json") });
            if (EventRef is { } er && request.Method == HttpMethod.Get && path == $"/api/calendar/meetings/{er.MeetingId}/event")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { eventId = er.EventId, startAt = er.Start }, Web), Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
