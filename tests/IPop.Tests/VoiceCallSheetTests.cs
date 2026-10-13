using System.Net;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using IPop.Web.Components.Meetings;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 gaps (spec §5 "Schedule → Voice call (short form)"): a short sheet — title, date, start/end,
/// participants — that saves a kind=voice meeting, with "More options…" to the full Calendar form (people in memory).
[Collection("AvatarPhotos")]
public class VoiceCallSheetTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static DateTime At(int h, int m = 0, int days = 0) => new DateTime(2026, 10, 13, h, m, 0, DateTimeKind.Utc).AddDays(days).AddHours(-7);
    private static readonly DateTime NowUtc = At(9, 50);

    private static IRenderedComponent<VoiceCallSheet> Render(BunitContext ctx, List<int>? saved = null, List<int>? closed = null) =>
        ctx.Render<VoiceCallSheet>(p => p.Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib)
            .Add(c => c.OnSaved, () => saved?.Add(1)).Add(c => c.OnClose, () => closed?.Add(1)));

    private static string Primary(IRenderedComponent<VoiceCallSheet> cut) => cut.Find(".vc-go").TextContent.Trim();

    [Fact]
    public void Short_form_has_title_date_times_and_participants_in_voice_colours()
    {
        using var ctx = BookingSheetTests.Context(new BookingSheetTests.Api());
        var cut = Render(ctx);
        Assert.Contains("k-voice", cut.Find("section.sheet2").ClassList);
        Assert.Equal("Schedule a voice call", cut.Find("#vc-title").TextContent.Trim());
        Assert.Contains("Audio only · join from anywhere, PC or phone", cut.Find(".sh-h small").TextContent);
        Assert.Equal("dialog", cut.Find("section.sheet2").GetAttribute("role"));
        Assert.NotNull(cut.Find("input.vc-name"));
        // Next quarter hour after 09:50, for 30 minutes.
        Assert.Equal("Today, 13 Oct", cut.Find("button.dp-pill.vc-date .dp-pill-t").TextContent);
        Assert.Equal("10:00", cut.Find("button.dp-pill.vc-start").TextContent.Trim());
        Assert.Equal("10:30", cut.Find("button.dp-pill.vc-end").TextContent.Trim());
        Assert.Equal("30 min", cut.Find(".durt").TextContent.Trim());
        Assert.NotNull(cut.Find(".ip-in"));                                        // InvitePicker
        Assert.Equal("More options…", cut.Find(".vc-more").TextContent.Trim());
        Assert.Equal("Schedule", Primary(cut));
        Assert.True(cut.Find(".vc-go").HasAttribute("disabled"));                  // needs a title
        Assert.Empty(cut.FindAll(".tl-track"));                                    // no room timeline
    }

    [Fact]
    public void Saving_posts_a_voice_meeting_with_the_people_and_times()
    {
        var api = new BookingSheetTests.Api();
        using var ctx = BookingSheetTests.Context(api);
        var saved = new List<int>();
        var cut = Render(ctx, saved);
        cut.Find("input.vc-name").Input("Budget 2027 draft");
        cut.Find("button.dp-pill.vc-start").Click();
        cut.FindAll(".dp-topt").Single(b => b.TextContent == "11:00").Click();       // keeps 30 min → 11:00–11:30
        Assert.Equal("11:30", cut.Find("button.dp-pill.vc-end").TextContent.Trim());
        cut.Find("button.dp-pill.vc-end").Click();
        cut.FindAll(".dp-topt").Single(b => b.TextContent == "12:00").Click();
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ip-row")));
        cut.Find(".ip-row").Click();
        cut.Find(".vc-go").Click();

        cut.WaitForAssertion(() => Assert.Single(saved));
        var b = JsonDocument.Parse(api.EventBodies.Single()).RootElement;
        Assert.Equal("Budget 2027 draft", b.GetProperty("title").GetString());
        Assert.Equal("voice", b.GetProperty("kind").GetString());
        Assert.Equal("Online · iPop Meeting", b.GetProperty("location").GetString());
        Assert.Equal(At(11), b.GetProperty("startAt").GetDateTime().ToUniversalTime());
        Assert.Equal(At(12), b.GetProperty("endAt").GetDateTime().ToUniversalTime());
        Assert.Equal(["NIK800001"], b.GetProperty("attendeeNiks").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(10, b.GetProperty("reminderMinutes").GetInt32());
        Assert.False(b.GetProperty("allDay").GetBoolean());
        Assert.DoesNotContain("NIK800001", cut.Markup);
    }

    [Fact]
    public void Past_starts_and_ends_before_the_start_cannot_be_picked()
    {
        using var ctx = BookingSheetTests.Context(new BookingSheetTests.Api());
        var cut = Render(ctx);
        cut.Find("button.dp-pill.vc-start").Click();
        string Dis(string t) => cut.FindAll(".dp-topt").Single(b => b.TextContent == t).GetAttribute("aria-disabled")!;
        Assert.Equal(("true", "false"), (Dis("09:45"), Dis("10:00")));
        cut.Find("button.dp-pill.vc-start").Click();
        cut.Find("button.dp-pill.vc-end").Click();
        Assert.Equal(("true", "false"), (Dis("10:00"), Dis("10:15")));
        cut.Find("button.dp-pill.vc-end").Click();
        cut.Find("button.dp-pill.vc-date").Click();
        Assert.Equal("true", cut.Find(".dp-day[data-date='2026-10-12']").GetAttribute("aria-disabled"));
    }

    [Fact]
    public void Server_error_is_shown_and_the_sheet_stays_open()
    {
        var api = new BookingSheetTests.Api { EventAnswer = (HttpStatusCode.BadRequest, """{"message":"Title is too long."}""") };
        using var ctx = BookingSheetTests.Context(api);
        var saved = new List<int>();
        var cut = Render(ctx, saved);
        cut.Find("input.vc-name").Input("x");
        cut.Find(".vc-go").Click();
        cut.WaitForAssertion(() => Assert.Equal("Title is too long.", cut.Find(".bs-err").TextContent.Trim()));
        Assert.Empty(saved);
    }

    [Fact]
    public void More_options_opens_the_Calendar_form_prefilled_with_people_in_memory()
    {
        using var ctx = BookingSheetTests.Context(new BookingSheetTests.Api());
        var cut = Render(ctx);
        cut.Find("input.vc-name").Input("Vendor sync");
        cut.Find(".ip-in").Focus();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ip-row")));
        cut.Find(".ip-row").Click();
        cut.Find(".vc-more").Click();

        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        var uri = new Uri(nav.Uri);
        Assert.Equal("/calendar", uri.AbsolutePath);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(("1", "voice", "Vendor sync", "From Calls"), (q["new"], q["kind"], q["title"], q["from"]));
        Assert.Equal(At(10), DateTime.Parse(q["start"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.Equal(At(10, 30), DateTime.Parse(q["end"]!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
        Assert.DoesNotContain("NIK", nav.Uri);
        Assert.Equal(["Alphin Santoso"], ctx.Services.GetRequiredService<MeetingDraftHandoff>().Take()!.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Escape_and_Cancel_close()
    {
        using var ctx = BookingSheetTests.Context(new BookingSheetTests.Api());
        var closed = new List<int>();
        var cut = Render(ctx, closed: closed);
        cut.Find("section.sheet2").KeyDown("Escape");
        cut.Find(".vc-cancel").Click();
        Assert.Equal(2, closed.Count);
    }

    [Fact]
    public void On_a_phone_it_is_a_bottom_sheet_without_More_options()
    {
        using var ctx = BookingSheetTests.Context(new BookingSheetTests.Api());
        ctx.JSInterop.Setup<bool>("ipop.mobile.isMobile").SetResult(true);
        var cut = Render(ctx);
        cut.WaitForAssertion(() => Assert.Contains("bottom", cut.Find("section.sheet2").ClassList));
        Assert.Single(cut.FindAll(".sh-grab"));
        Assert.Empty(cut.FindAll(".vc-more"));
    }
}
