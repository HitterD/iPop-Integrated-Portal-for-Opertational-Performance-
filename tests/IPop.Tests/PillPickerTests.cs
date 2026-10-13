using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 gaps (mockup screens 2 + 6): the date/time fields of the booking sheet, the voice short form and
/// the Calendar form are pills ("Today, 13 Oct ▾", "13:00") that open the shared month grid / 15-minute list.
public class PillPickerTests
{
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    [Theory]
    [InlineData(2026, 10, 13, "Today, 13 Oct")]
    [InlineData(2026, 10, 14, "Tomorrow, 14 Oct")]
    [InlineData(2026, 10, 21, "Wed 21 Oct")]
    [InlineData(2027, 1, 8, "Fri 8 Jan 2027")]
    public void Pill_label_says_today_tomorrow_or_the_weekday(int y, int m, int d, string expected) =>
        Assert.Equal(expected, DatePickerMath.FormatPill(new DateOnly(y, m, d), Today));

    [Fact]
    public void Date_pill_is_a_button_with_icon_label_and_chevron_that_opens_the_month()
    {
        using var ctx = NewContext();
        DateTime? got = null;
        var cut = ctx.Render<AppDatePicker<DateTime>>(p => p.Add(x => x.Value, new DateTime(2026, 10, 13)).Add(x => x.Pill, true)
            .Add(x => x.TodayDate, Today).Add(x => x.Min, new DateTime(2026, 10, 13)).Add(x => x.Max, new DateTime(2026, 12, 12))
            .Add(x => x.Class, "bs-date").Add(x => x.AriaLabel, "Date").Add(x => x.ValueChanged, v => got = v));
        Assert.Empty(cut.FindAll("input"));
        var pill = cut.Find("button.dp-pill.bs-date");
        Assert.Equal("Today, 13 Oct", pill.QuerySelector(".dp-pill-t")!.TextContent);
        Assert.Equal(2, pill.QuerySelectorAll("svg").Length);              // calendar icon + chevron
        Assert.Equal("dialog", pill.GetAttribute("aria-haspopup"));
        Assert.Equal("false", pill.GetAttribute("aria-expanded"));
        Assert.Equal("Date: Today, 13 Oct", pill.GetAttribute("aria-label"));

        pill.Click();
        Assert.Equal("true", cut.Find("button.dp-pill").GetAttribute("aria-expanded"));
        Assert.Equal("true", cut.Find(".dp-day[data-date='2026-10-12']").GetAttribute("aria-disabled"));   // before Min
        cut.Find(".dp-day[data-date='2026-10-21']").Click();
        Assert.Equal(new DateTime(2026, 10, 21), got);
        Assert.Empty(cut.FindAll(".dp-pop"));
    }

    [Fact]
    public void Compact_date_pill_is_text_only()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDatePicker<DateTime>>(p => p.Add(x => x.Value, new DateTime(2026, 10, 14)).Add(x => x.Pill, true)
            .Add(x => x.Compact, true).Add(x => x.TodayDate, Today));
        Assert.Empty(cut.Find("button.dp-pill").QuerySelectorAll("svg"));
        Assert.Equal("Tomorrow, 14 Oct", cut.Find("button.dp-pill").TextContent.Trim());
    }

    [Fact]
    public void Time_pill_opens_15_minute_slots_and_disabled_ones_cannot_be_picked()
    {
        using var ctx = NewContext();
        TimeOnly? got = null;
        var cut = ctx.Render<AppTimePicker<TimeOnly>>(p => p.Add(x => x.Value, new TimeOnly(13, 0)).Add(x => x.Pill, true)
            .Add(x => x.Class, "bs-start").Add(x => x.AriaLabel, "Start")
            .Add(x => x.IsTimeDisabled, t => t < new TimeOnly(10, 0)).Add(x => x.ValueChanged, v => got = v));
        var pill = cut.Find("button.dp-pill.bs-start");
        Assert.Equal("13:00", pill.TextContent.Trim());
        Assert.Contains("time", pill.ClassList);
        Assert.Equal("Start: 13:00", pill.GetAttribute("aria-label"));

        pill.KeyDown("ArrowDown");                                           // keyboard opens it too
        Assert.Equal(96, cut.FindAll(".dp-topt").Count);
        Assert.Equal("13:00", cut.Find(".dp-topt.sel").TextContent);
        var early = cut.FindAll(".dp-topt").Single(b => b.TextContent == "09:45");
        Assert.Equal("true", early.GetAttribute("aria-disabled"));
        early.Click();
        Assert.Null(got);
        cut.Find(".dp-times").KeyDown("ArrowDown");
        cut.Find(".dp-times").KeyDown("Enter");
        Assert.Equal(new TimeOnly(13, 15), got);
        Assert.Empty(cut.FindAll(".dp-pop"));
    }

    [Fact]
    public void Plain_pickers_keep_the_typed_field()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppTimePicker<TimeOnly>>(p => p.Add(x => x.Value, new TimeOnly(13, 0)));
        Assert.NotNull(cut.Find("input.dp-input"));
        Assert.Empty(cut.FindAll(".dp-pill"));
    }
}
