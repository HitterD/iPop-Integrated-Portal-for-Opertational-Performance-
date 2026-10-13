using IPop.Web.Components.Meetings;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 gaps (mockup screen 6): the Calendar form's Starts/Ends rows are pills ("Wed 14 Oct" + "13:00").
/// Which dates and times the pills offer: new events start from today (now for today's times); a room booking keeps the
/// v3 limits (≤ 60 days, same day, ≤ 8 h after the start); an end is always after the start. All day hides the times.
public class CalendarFormPillTests
{
    private static readonly DateTime Today = new(2026, 10, 13);
    private static readonly DateTime NowLocal = new(2026, 10, 13, 9, 50, 0);

    [Fact]
    public void New_event_dates_start_today_and_rooms_stop_at_60_days()
    {
        Assert.Equal(((DateTime?)Today, (DateTime?)null), CalendarKinds.StartDateRange(isNew: true, MeetingKinds.Voice, Today));
        Assert.Equal(((DateTime?)Today, (DateTime?)Today.AddDays(60)), CalendarKinds.StartDateRange(isNew: true, MeetingKinds.Room, Today));
        Assert.Equal(((DateTime?)null, (DateTime?)null), CalendarKinds.StartDateRange(isNew: false, MeetingKinds.Room, Today));
    }

    [Theory]
    [InlineData(13, 9, 45, true)]    // today before now
    [InlineData(13, 10, 0, false)]
    [InlineData(14, 7, 0, false)]    // another day: any time
    public void New_start_times_today_are_from_now(int day, int h, int m, bool disabled) =>
        Assert.Equal(disabled, CalendarKinds.StartTimeDisabled(isNew: true, new DateTime(2026, 10, day), new TimeOnly(h, m), NowLocal));

    [Fact]
    public void Editing_never_disables_start_times() =>
        Assert.False(CalendarKinds.StartTimeDisabled(isNew: false, Today, new TimeOnly(7, 0), NowLocal));

    [Theory]
    [InlineData("", 13, 0, true)]      // == start
    [InlineData("", 13, 15, false)]
    [InlineData("", 23, 45, false)]    // events may be long
    [InlineData("room", 21, 0, false)] // 8 h after 13:00
    [InlineData("room", 21, 15, true)]
    [InlineData("room", 0, 0, true)]   // 11 h → over 8 h
    public void End_times_follow_the_start_and_room_limits(string kind, int h, int m, bool disabled) =>
        Assert.Equal(disabled, CalendarKinds.EndTimeDisabled(kind, new DateTime(2026, 10, 14, 13, 0, 0), new DateTime(2026, 10, 14), new TimeOnly(h, m)));

    [Fact]
    public void End_on_a_later_day_is_free_for_events_but_not_rooms()
    {
        var start = new DateTime(2026, 10, 14, 22, 0, 0);
        Assert.False(CalendarKinds.EndTimeDisabled("", start, new DateTime(2026, 10, 15), new TimeOnly(1, 0)));
        Assert.True(CalendarKinds.EndTimeDisabled(MeetingKinds.Room, start, new DateTime(2026, 10, 15), new TimeOnly(1, 0)));
    }
}

/// The Calendar page has no bUnit harness (it injects most of the app); its form rows are checked from the source.
public class CalendarFormPillMarkupTests
{
    private static string Src(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "IPop.Web"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }

    [Fact]
    public void Starts_and_Ends_rows_use_date_and_time_pills_with_the_limits()
    {
        var razor = Src("src/IPop.Web/Pages/Calendar.razor");
        foreach (var label in new[] { "Start date", "Start time", "End date", "End time" })
        {
            var m = System.Text.RegularExpressions.Regex.Match(razor, $"<App(Date|Time)Picker[^>]*AriaLabel=\"{label}\"[^>]*/>");
            Assert.True(m.Success, label);
            Assert.Contains("Pill=\"true\"", m.Value);
        }
        Assert.Contains("IsTimeDisabled=\"FormStartDisabled\"", razor);
        Assert.Contains("IsTimeDisabled=\"FormEndDisabled\"", razor);
        // All day still hides both times.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(razor, @"@if \(!form\.AllDay\) \{ <AppTimePicker").Count);
    }
}

public class CalendarRepeatMoveMarkupTests
{
    [Fact]
    public void Calendar_repeat_check_offers_every_free_room_not_just_the_first()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "IPop.Web"))) dir = Path.GetDirectoryName(dir);
        var razor = File.ReadAllText(Path.Combine(dir!, "src/IPop.Web/Pages/Calendar.razor"));
        var block = razor[razor.IndexOf("cal-rcheck-row", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("@if (formError", StringComparison.Ordinal)];
        Assert.DoesNotContain("FreeRooms.FirstOrDefault()", block);
        Assert.Contains("foreach (var room in occ.FreeRooms)", block);
        Assert.Contains("role=\"menuitemradio\"", block);
        Assert.Contains("mk-sw r-", block);
    }
}
