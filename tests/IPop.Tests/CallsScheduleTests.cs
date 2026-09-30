using IPop.Web.Pages;
using Xunit;

namespace IPop.Tests;

/// Calls › Scheduled client rules: day headers, the 10-minute join window, RSVP labels,
/// duration chips, form validation and the reminder payload sent to the API.
public class CallsScheduleTests
{
    private static readonly DateTime Today = new(2026, 9, 27); // Sunday

    [Fact]
    public void DayLabel_TodayTomorrowThenEnglishShortDate()
    {
        Assert.Equal("Today", CallsSchedule.DayLabel(Today.AddHours(15), Today));
        Assert.Equal("Tomorrow", CallsSchedule.DayLabel(Today.AddDays(1), Today));
        Assert.Equal("Tue, Sep 29", CallsSchedule.DayLabel(Today.AddDays(2), Today));
        Assert.Equal("Mon, Oct 5", CallsSchedule.DayLabel(new DateTime(2026, 10, 5), Today));
        Assert.Equal("Mon, Jan 4, 2027", CallsSchedule.DayLabel(new DateTime(2027, 1, 4), Today));
    }

    [Fact]
    public void CanJoin_From10MinutesBeforeStartUntilEnd()
    {
        var start = new DateTime(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(30);
        Assert.False(CallsSchedule.CanJoin(start, end, start.AddMinutes(-11)));
        Assert.True(CallsSchedule.CanJoin(start, end, start.AddMinutes(-10)));
        Assert.True(CallsSchedule.CanJoin(start, end, start.AddMinutes(-5)));
        Assert.True(CallsSchedule.CanJoin(start, end, start.AddMinutes(29)));
        Assert.False(CallsSchedule.CanJoin(start, end, end));
    }

    [Theory]
    [InlineData("accept", "Going")]
    [InlineData("maybe", "Maybe")]
    [InlineData("decline", "Not going")]
    [InlineData("pending", "No response")]
    [InlineData(null, "No response")]
    public void RsvpLabel_Translates(string? status, string label) => Assert.Equal(label, CallsSchedule.RsvpLabel(status));

    [Fact]
    public void Durations_ChipsAndLabels()
    {
        Assert.Equal(30, CallsSchedule.ChipFor(TimeSpan.FromMinutes(30)));
        Assert.Null(CallsSchedule.ChipFor(TimeSpan.FromMinutes(45)));
        Assert.Equal("15 min", CallsSchedule.DurationLabel(TimeSpan.FromMinutes(15)));
        Assert.Equal("1 hr", CallsSchedule.DurationLabel(TimeSpan.FromMinutes(60)));
        Assert.Equal("1 hr 30 min", CallsSchedule.DurationLabel(TimeSpan.FromMinutes(90)));
    }

    [Fact]
    public void Validate_EndNeverBeforeStart_StartNotInPastForNewMeetings()
    {
        var now = new DateTime(2026, 9, 27, 10, 0, 0);
        Assert.NotNull(CallsSchedule.Validate("", now.AddHours(1), now.AddHours(2), now, true));
        Assert.NotNull(CallsSchedule.Validate("Rapat", now.AddHours(1), now.AddMinutes(30), now, true));
        Assert.NotNull(CallsSchedule.Validate("Rapat", now.AddHours(1), now.AddHours(1), now, true));
        Assert.NotNull(CallsSchedule.Validate("Rapat", now.AddHours(-1), now.AddHours(1), now, true));
        Assert.Null(CallsSchedule.Validate("Rapat", now.AddHours(-1), now.AddHours(1), now, false));
        Assert.Null(CallsSchedule.Validate("Rapat", now.AddHours(1), now.AddHours(2), now, true));
    }

    [Fact]
    public void ReminderPayload_NoneIsNullOnCreate_ZeroOnEdit()
    {
        Assert.Null(CallsSchedule.ReminderPayload("", isNew: true));
        Assert.Equal(0, CallsSchedule.ReminderPayload("", isNew: false));
        Assert.Equal(15, CallsSchedule.ReminderPayload("15", isNew: true));
        Assert.Equal(1440, CallsSchedule.ReminderPayload("1440", isNew: false));
    }

    [Fact]
    public void IsOnline_EmptyOrIpopMeeting()
    {
        Assert.True(CallsSchedule.IsOnline(null));
        Assert.True(CallsSchedule.IsOnline(CallsSchedule.OnlineLocation));
        Assert.False(CallsSchedule.IsOnline("Ruang Rapat 2"));
    }
}
