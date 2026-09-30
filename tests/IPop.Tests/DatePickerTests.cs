using Bunit;
using IPop.Web.Components;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

public class DatePickerMathTests
{
    [Fact]
    public void MonthGrid_StartsOnMonday_AndCoversSixWeeks()
    {
        var grid = DatePickerMath.MonthGrid(2026, 9); // 1 Sep 2026 is a Tuesday
        Assert.Equal(42, grid.Length);
        Assert.Equal(new DateOnly(2026, 8, 31), grid[0]);
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Contains(new DateOnly(2026, 9, 30), grid);
        Assert.All(Enumerable.Range(0, 6), w => Assert.Equal(DayOfWeek.Monday, grid[w * 7].DayOfWeek));
    }

    [Fact]
    public void MonthGrid_MonthStartingOnMonday_HasNoLeadingDays()
        => Assert.Equal(new DateOnly(2026, 6, 1), DatePickerMath.MonthGrid(2026, 6)[0]);

    [Theory]
    [InlineData("28/09/2026", 2026, 9, 28)]
    [InlineData("2026-09-28", 2026, 9, 28)]
    [InlineData("1/2/2026", 2026, 2, 1)]
    [InlineData("28.09.26", 2026, 9, 28)]
    [InlineData("28 Sep 2026", 2026, 9, 28)]
    [InlineData("Mon, 28 Sep 2026", 2026, 9, 28)]
    [InlineData("28 August 2026", 2026, 8, 28)]
    [InlineData("5 Agustus 2026", 2026, 8, 5)]
    [InlineData("  29/02/2028 ", 2028, 2, 29)]
    public void TryParseDate_AcceptsSupportedForms(string input, int y, int m, int d)
    {
        Assert.True(DatePickerMath.TryParseDate(input, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("31/02/2026")]
    [InlineData("29/02/2027")]
    [InlineData("13/13/2026")]
    [InlineData("2026/09")]
    [InlineData("besok")]
    public void TryParseDate_RejectsInvalid(string input) => Assert.False(DatePickerMath.TryParseDate(input, out _));

    [Theory]
    [InlineData("09:45", 9, 45)]
    [InlineData("9:05", 9, 5)]
    [InlineData("17.30", 17, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("8", 8, 0)]
    public void TryParseTime_AcceptsSupportedForms(string input, int h, int m)
    {
        Assert.True(DatePickerMath.TryParseTime(input, out var t));
        Assert.Equal(new TimeOnly(h, m), t);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("noon")]
    public void TryParseTime_RejectsInvalid(string input) => Assert.False(DatePickerMath.TryParseTime(input, out _));

    [Theory]
    [InlineData("28/09/2026 09:45", 9, 45)]
    [InlineData("2026-09-28T09:45", 9, 45)]
    [InlineData("28 Sep 2026 · 09:45", 9, 45)]
    [InlineData("28/09/2026", 8, 0)] // falls back to the supplied time
    public void TryParseDateTime_ParsesDateAndOptionalTime(string input, int h, int m)
    {
        Assert.True(DatePickerMath.TryParseDateTime(input, new TimeOnly(8, 0), out var v));
        Assert.Equal(new DateTime(2026, 9, 28, h, m, 0), v);
    }

    [Fact]
    public void Formatting_UsesIndonesianNames()
    {
        var d = new DateOnly(2026, 9, 28);
        Assert.Equal("Mon, 28 Sep 2026", DatePickerMath.FormatDate(d));
        Assert.Equal("Monday, 28 September 2026", DatePickerMath.FormatLong(d));
        Assert.Equal("28 Sep 2026 · 09:45", DatePickerMath.FormatDateTime(new DateTime(2026, 9, 28, 9, 45, 0)));
        Assert.Equal("August 2026", DatePickerMath.MonthTitle(2026, 8));
        Assert.Equal("Su", DatePickerMath.WeekdayMin[DatePickerMath.MondayIndex(DayOfWeek.Sunday)]);
    }

    [Fact]
    public void ClampAndRange_RespectMinMax()
    {
        DateOnly min = new(2026, 9, 10), max = new(2026, 9, 20);
        Assert.Equal(min, DatePickerMath.Clamp(new DateOnly(2026, 9, 1), min, max));
        Assert.Equal(max, DatePickerMath.Clamp(new DateOnly(2026, 9, 30), min, max));
        Assert.True(DatePickerMath.IsOutOfRange(new DateOnly(2026, 9, 9), min, max));
        Assert.False(DatePickerMath.IsOutOfRange(new DateOnly(2026, 9, 20), min, max));
        Assert.False(DatePickerMath.IsOutOfRange(new DateOnly(1900, 1, 1), null, null));
    }

    [Theory]
    [InlineData(2026, 9, 27, 2026, 9, 28)] // Sunday -> next day
    [InlineData(2026, 9, 28, 2026, 10, 5)] // Monday -> a week later
    [InlineData(2026, 10, 2, 2026, 10, 5)] // Friday
    public void NextMonday_IsStrictlyAfterToday(int y, int m, int d, int ey, int em, int ed)
        => Assert.Equal(new DateOnly(ey, em, ed), DatePickerMath.NextMonday(new DateOnly(y, m, d)));

    [Fact]
    public void TimeSlots_And_CeilToStep()
    {
        var slots = DatePickerMath.TimeSlots(15);
        Assert.Equal(96, slots.Count);
        Assert.Equal(new TimeOnly(23, 45), slots[^1]);
        Assert.Equal(new TimeOnly(9, 45), DatePickerMath.CeilToStep(new TimeOnly(9, 31), 15));
        Assert.Equal(new TimeOnly(9, 30), DatePickerMath.CeilToStep(new TimeOnly(9, 30), 15));
        Assert.Equal(new TimeOnly(23, 59), DatePickerMath.CeilToStep(new TimeOnly(23, 50), 15));
    }

    [Fact]
    public void PickerValue_RoundTripsAllSupportedTypes()
    {
        var dt = new DateTime(2026, 9, 28, 9, 45, 0);
        Assert.Equal(dt, PickerValue.FromCore<DateTime>(PickerValue.ToCore(dt)));
        Assert.Equal(new DateOnly(2026, 9, 28), PickerValue.FromCore<DateOnly?>(PickerValue.ToCore(new DateOnly(2026, 9, 28))));
        Assert.Equal(new TimeOnly(22, 15), PickerValue.FromCore<TimeOnly>(PickerValue.ToCore(new TimeOnly(22, 15))));
        Assert.Equal(TimeSpan.FromMinutes(90), PickerValue.FromCore<TimeSpan?>(PickerValue.ToCore(TimeSpan.FromMinutes(90))));
        Assert.Null(PickerValue.FromCore<DateTime?>(null));
        Assert.Throws<InvalidOperationException>(() => PickerValue.FromCore<DateTime>(null));
    }
}

public class DatePickerRenderTests
{
    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<OverlayStack>();
        return ctx;
    }

    [Fact]
    public void DatePicker_ShowsFormattedValue()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p.Add(x => x.Value, new DateTime(2026, 9, 28)));
        Assert.Equal("Mon, 28 Sep 2026", cut.Find("input.dp-input").GetAttribute("value"));
    }

    [Fact]
    public void TypingValidDate_CommitsOnBlur()
    {
        using var ctx = NewContext();
        DateOnly? got = null;
        var cut = ctx.Render<AppDatePicker<DateOnly?>>(p => p
            .Add(x => x.Value, (DateOnly?)null)
            .Add(x => x.ValueChanged, v => got = v));
        var input = cut.Find("input.dp-input");
        input.Input("03/10/2026");
        input.Blur();
        Assert.Equal(new DateOnly(2026, 10, 3), got);
    }

    [Fact]
    public void TypingInvalidDate_RevertsAndFlagsError()
    {
        using var ctx = NewContext();
        var changed = false;
        var cut = ctx.Render<AppDatePicker<DateTime>>(p => p
            .Add(x => x.Value, new DateTime(2026, 9, 28))
            .Add(x => x.ValueChanged, _ => changed = true));
        cut.Find("input.dp-input").Input("31/02/2026");
        cut.Find("input.dp-input").Blur();
        Assert.False(changed);
        Assert.Equal("Mon, 28 Sep 2026", cut.Find("input.dp-input").GetAttribute("value"));
        Assert.Contains("dp-invalid", cut.Find(".dp").ClassList);
    }

    [Fact]
    public void TypedDateBeforeMin_IsRejected()
    {
        using var ctx = NewContext();
        var changed = false;
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p
            .Add(x => x.Value, (DateTime?)null)
            .Add(x => x.Min, new DateTime(2026, 9, 10))
            .Add(x => x.ValueChanged, _ => changed = true));
        cut.Find("input.dp-input").Input("01/09/2026");
        cut.Find("input.dp-input").Blur();
        Assert.False(changed);
    }

    [Fact]
    public void OpenPopover_RendersMondayFirstGrid_WithSelectionAndDisabledDays()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p
            .Add(x => x.Value, new DateTime(2026, 9, 28))
            .Add(x => x.Min, new DateTime(2026, 9, 5))
            .Add(x => x.AllowClear, true));
        cut.Find("input.dp-input").Click();

        Assert.Equal("September 2026", cut.Find(".dp-title").TextContent);
        var heads = cut.FindAll(".dp-wd").Select(e => e.TextContent).ToArray();
        Assert.Equal(new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" }, heads);
        var sel = cut.Find(".dp-day[aria-selected='true']");
        Assert.Equal("Monday, 28 September 2026", sel.GetAttribute("aria-label"));
        Assert.Equal("true", cut.Find(".dp-day[data-date='2026-09-04']").GetAttribute("aria-disabled"));
        Assert.Equal("0", sel.GetAttribute("tabindex"));
        Assert.NotNull(cut.Find(".dp-clear"));
    }

    [Fact]
    public void ClearChip_OnlyWhenAllowClear()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p.Add(x => x.Value, new DateTime(2026, 9, 28)));
        cut.Find("input.dp-input").Click();
        Assert.Empty(cut.FindAll(".dp-clear"));
    }

    [Fact]
    public void KeyboardNavigation_MovesFocusAndEnterSelects()
    {
        using var ctx = NewContext();
        DateTime? got = null;
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p
            .Add(x => x.Value, new DateTime(2026, 9, 28))
            .Add(x => x.ValueChanged, v => got = v));
        cut.Find("input.dp-input").Click();
        var grid = cut.Find(".dp-grid");
        grid.KeyDown("ArrowRight");
        cut.Find(".dp-grid").KeyDown("PageDown");
        Assert.Equal("October 2026", cut.Find(".dp-title").TextContent);
        cut.Find(".dp-grid").KeyDown("Enter");
        Assert.Equal(new DateTime(2026, 10, 29), got);
        Assert.Empty(cut.FindAll(".dp-pop"));
    }

    [Fact]
    public void Escape_ClosesThroughOverlayStack()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<AppDatePicker<DateTime?>>(p => p.Add(x => x.Value, (DateTime?)null));
        cut.Find("input.dp-input").Click();
        Assert.NotEmpty(cut.FindAll(".dp-pop"));
        var overlays = ctx.Services.GetRequiredService<OverlayStack>();
        cut.InvokeAsync(() => overlays.CloseTopAsync()).GetAwaiter().GetResult();
        Assert.Empty(cut.FindAll(".dp-pop"));
        Assert.False(overlays.Any);
    }

    [Fact]
    public void DateTimePicker_PickingDayKeepsTime_AndTimeSlotCommits()
    {
        using var ctx = NewContext();
        DateTime? got = null;
        var cut = ctx.Render<AppDateTimePicker<DateTime>>(p => p
            .Add(x => x.Value, new DateTime(2026, 9, 28, 9, 45, 0))
            .Add(x => x.ValueChanged, v => got = v));
        Assert.Equal("28 Sep 2026 · 09:45", cut.Find("input.dp-input").GetAttribute("value"));
        cut.Find("input.dp-input").Click();
        Assert.Equal(96, cut.FindAll(".dp-topt").Count);
        Assert.Equal("09:45", cut.Find(".dp-topt.sel").TextContent);
        cut.Find(".dp-day[data-date='2026-09-30']").Click();
        Assert.Equal(new DateTime(2026, 9, 30, 9, 45, 0), got);
        cut.FindAll(".dp-topt").Single(b => b.TextContent == "14:30").Click();
        Assert.Equal(new DateTime(2026, 9, 30, 14, 30, 0), got);
    }

    [Fact]
    public void TimePicker_TypesAndBindsTimeOnly()
    {
        using var ctx = NewContext();
        TimeOnly? got = null;
        var cut = ctx.Render<AppTimePicker<TimeOnly?>>(p => p
            .Add(x => x.Value, new TimeOnly(22, 0))
            .Add(x => x.ValueChanged, v => got = v));
        Assert.Equal("22:00", cut.Find("input.dp-input").GetAttribute("value"));
        cut.Find("input.dp-input").Input("7.15");
        cut.Find("input.dp-input").KeyDown("Enter");
        Assert.Equal(new TimeOnly(7, 15), got);
    }

    [Fact]
    public void UnsupportedType_Throws()
    {
        using var ctx = NewContext();
        Assert.ThrowsAny<Exception>(() => ctx.Render<AppDatePicker<string>>());
    }
}
