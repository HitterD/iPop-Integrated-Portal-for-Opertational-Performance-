using IPop.Application.Notes;
using Xunit;

namespace IPop.Tests;

public class NoteRecurrenceRulesTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Theory]
    [InlineData("daily", null, null, "FREQ=DAILY")]
    [InlineData("workdays", null, null, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData("weekly", "MO,WE", null, "FREQ=WEEKLY;BYDAY=MO,WE")]
    [InlineData("monthly", null, 15, "FREQ=MONTHLY;BYMONTHDAY=15")]
    public void The_dialog_choices_become_a_stored_rule(string kind, string? days, int? monthDay, string expected)
        => Assert.Equal(expected, NoteRecurrenceRules.FromChoice(kind, days?.Split(','), monthDay));

    [Theory]
    [InlineData("monthly", null, 40)]
    [InlineData("monthly", null, null)]
    [InlineData("hourly", null, null)]
    [InlineData("weekly", "XX", null)]
    [InlineData(null, null, null)]
    public void Anything_else_is_refused(string? kind, string? days, int? monthDay)
        => Assert.Null(NoteRecurrenceRules.FromChoice(kind, days?.Split(','), monthDay));

    [Fact]
    public void Workdays_fire_Monday_to_Friday_only_and_never_before_the_start_day()
    {
        var rule = NoteRecurrenceRules.FromChoice("workdays", null, null)!;
        var start = D(2026, 11, 19);                                                     // a Thursday
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 19)));    // the start day itself, it is a workday
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 20)));    // Friday
        Assert.False(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 21)));   // Saturday
        Assert.False(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 22)));   // Sunday
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 23)));    // Monday
        Assert.False(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 11, 18)));   // before the start: no back-filling
    }

    [Fact]
    public void A_day_already_made_is_not_made_again_and_missed_days_are_not_back_filled()
    {
        const string rule = "FREQ=DAILY";
        var start = D(2026, 11, 1);
        Assert.False(NoteRecurrenceRules.IsDueOn(rule, start, D(2026, 11, 19), D(2026, 11, 19)));
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, D(2026, 11, 17), D(2026, 11, 19)));   // only today is made, not the 18th as well
    }

    [Fact]
    public void Weekly_on_chosen_days_and_every_second_week_keep_their_rhythm()
    {
        var start = D(2026, 11, 2);                                                      // a Monday
        var weekly = NoteRecurrenceRules.FromChoice("weekly", ["MO", "WE"], null)!;
        Assert.True(NoteRecurrenceRules.IsDueOn(weekly, start, null, D(2026, 11, 2)));
        Assert.False(NoteRecurrenceRules.IsDueOn(weekly, start, null, D(2026, 11, 3)));
        Assert.True(NoteRecurrenceRules.IsDueOn(weekly, start, null, D(2026, 11, 4)));
        Assert.True(NoteRecurrenceRules.IsDueOn(weekly, start, null, D(2026, 11, 9)));

        const string biweekly = "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO";
        Assert.True(NoteRecurrenceRules.IsDueOn(biweekly, start, null, D(2026, 11, 2)));
        Assert.False(NoteRecurrenceRules.IsDueOn(biweekly, start, null, D(2026, 11, 9)));
        Assert.True(NoteRecurrenceRules.IsDueOn(biweekly, start, null, D(2026, 11, 16)));
    }

    [Fact]
    public void Monthly_on_a_date_clamps_to_the_end_of_short_months()
    {
        var rule = NoteRecurrenceRules.FromChoice("monthly", null, 31)!;
        var start = D(2026, 1, 31);
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 1, 31)));
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 2, 28)));     // February has no 31st
        Assert.False(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 2, 27)));
        Assert.True(NoteRecurrenceRules.IsDueOn(rule, start, null, D(2026, 3, 31)));
    }

    [Fact]
    public void An_unreadable_rule_is_never_due()
        => Assert.False(NoteRecurrenceRules.IsDueOn("garbage", D(2026, 1, 1), null, D(2026, 1, 2)));

    [Fact]
    public void Titles_end_with_the_date_in_English_and_stay_within_200_characters()
    {
        Assert.Equal("Weekly ICT — 24 Nov 2026", NoteRecurrenceRules.TitleFor("Weekly ICT", D(2026, 11, 24)));
        Assert.Equal("Untitled — 1 Jan 2027", NoteRecurrenceRules.TitleFor("  ", D(2027, 1, 1)));
        var long200 = NoteRecurrenceRules.TitleFor(new string('x', 400), D(2026, 11, 24));
        Assert.True(long200.Length <= 200);
        Assert.EndsWith("— 24 Nov 2026", long200);
    }

    [Theory]
    [InlineData("FREQ=DAILY", "Every day")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "Every weekday")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", "Every Monday")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE", "Every Monday and Wednesday")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=1", "Monthly on the 1st")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=22", "Monthly on the 22nd")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=13", "Monthly on the 13th")]
    [InlineData("nonsense", "Doesn’t repeat")]
    public void The_rule_is_described_in_plain_words(string rule, string text) => Assert.Equal(text, NoteRecurrenceRules.Describe(rule));
}
