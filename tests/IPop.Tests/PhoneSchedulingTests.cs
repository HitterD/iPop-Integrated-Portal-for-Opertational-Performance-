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

    private sealed class CallsApi : HttpMessageHandler
    {
        public List<MeetingInfo> Upcoming { get; set; } = [];
        public (Guid MeetingId, Guid EventId, DateTime Start)? EventRef { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
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
