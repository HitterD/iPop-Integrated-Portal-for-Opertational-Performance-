using IPop.Web.Components.Projects;
using Xunit;

namespace IPop.Tests;

public class ProjectTimeFormatTests
{
    [Theory]
    [InlineData(0, "0m")]
    [InlineData(-5, "0m")]
    [InlineData(45, "45m")]
    [InlineData(60, "1j")]
    [InlineData(90, "1j 30m")]
    [InlineData(600, "10j")]
    public void Minutes_formats_hours_and_minutes(int minutes, string expected) =>
        Assert.Equal(expected, ProjectTimeFormat.Minutes(minutes));

    [Fact]
    public void Elapsed_switches_to_hours_after_sixty_minutes()
    {
        Assert.Equal("0:05", ProjectTimeFormat.Elapsed(TimeSpan.FromSeconds(5)));
        Assert.Equal("4:07", ProjectTimeFormat.Elapsed(new TimeSpan(0, 4, 7)));
        Assert.Equal("1:04:07", ProjectTimeFormat.Elapsed(new TimeSpan(1, 4, 7)));
        Assert.Equal("0:00", ProjectTimeFormat.Elapsed(TimeSpan.FromSeconds(-3)));
    }

    [Theory]
    [InlineData(1, 30, 90)]
    [InlineData(null, 15, 15)]
    [InlineData(24, 0, 1440)]
    [InlineData(0, 0, null)]
    [InlineData(24, 1, null)]
    [InlineData(1, 60, null)]
    [InlineData(-1, 30, null)]
    public void ToMinutes_bounds_manual_entries(int? hours, int? minutes, int? expected) =>
        Assert.Equal(expected, ProjectTimeFormat.ToMinutes(hours, minutes));

    [Fact]
    public void Week_starts_on_monday_and_query_is_the_utc_instant_of_local_midnight()
    {
        var wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
        var start = ProjectTimeFormat.WeekStartLocal(new DateTime(2026, 9, 27)); // Sunday
        Assert.Equal(new DateTime(2026, 9, 21), start);
        Assert.Equal(DayOfWeek.Monday, ProjectTimeFormat.WeekStartLocal(new DateTime(2026, 9, 28)).DayOfWeek);
        Assert.Equal("2026-09-20T17%3A00%3A00Z", ProjectTimeFormat.WeekStartQuery(start, wib));
    }

    [Fact]
    public void Week_label_spans_months_in_indonesian()
    {
        Assert.Equal("21–27 Sep 2026", ProjectTimeFormat.WeekLabel(new DateTime(2026, 9, 21)));
        Assert.Equal("28 Sep – 4 Okt 2026", ProjectTimeFormat.WeekLabel(new DateTime(2026, 9, 28)));
    }
}
