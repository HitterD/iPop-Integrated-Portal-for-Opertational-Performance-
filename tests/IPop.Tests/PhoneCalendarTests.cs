using Bunit;
using IPop.Web.Components.Phone.Calendar;
using CalendarPage = IPop.Web.Pages.Calendar;
using Xunit;

namespace IPop.Tests;
public class PhoneCalendarTests
{
    [Fact]
    public void Week_strip_has_seven_days_selects_day_and_moves_week_without_exposing_busy_details()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var day = new DateTime(2026, 10, 14);
        DateTime? selected = null; var shift = 0;
        var busy = new CalendarPage.FeedItem("busy", "busy", "busy", "Busy", day.AddHours(10), day.AddHours(11), false, null, null, null, null, null, false, null, null);
        var cut = ctx.Render<WeekStripAgenda>(p => p.Add(x => x.Anchor, day).Add(x => x.Items, new[] { busy })
            .Add(x => x.OnDay, value => selected = value).Add(x => x.OnWeek, value => shift = value));
        Assert.Equal(7, cut.FindAll(".m-calendar-day").Count);
        Assert.Equal("true", cut.Find("button[data-date='2026-10-14']").GetAttribute("aria-selected"));
        cut.Find("button[data-date='2026-10-15']").Click(); Assert.Equal(day.AddDays(1), selected);
        cut.Find("button[aria-label='Next week']").Click(); Assert.Equal(1, shift);
        Assert.Contains("Busy", cut.Markup);
        Assert.DoesNotContain("NIK", cut.Markup);
    }
    [Fact]
    public void Agenda_dispatches_original_feed_item_and_has_now_line_today()
    {
        using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var day = DateTime.Today;
        var item = new CalendarPage.FeedItem("one", "meetings", "meeting", "Planning", day.AddHours(23).ToUniversalTime(), day.AddDays(1).ToUniversalTime(), false, null, "Boardroom", Guid.NewGuid(), Guid.NewGuid(), "pending", false, null, null, MeetingKind: "room", RoomKey: "arabica");
        CalendarPage.FeedItem? opened = null;
        var cut = ctx.Render<WeekStripAgenda>(p => p.Add(x => x.Anchor, day).Add(x => x.Items, new[] { item }).Add(x => x.OnOpen, value => opened = value));
        Assert.NotNull(cut.Find(".m-calendar-now"));
        cut.Find("button.m-calendar-event").Click(); Assert.Equal(item, opened);
        Assert.Contains("Planning", cut.Markup);
    }
}
