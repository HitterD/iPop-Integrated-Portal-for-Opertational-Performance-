using IPop.Web.Components;

namespace IPop.Web.Pages;

/// Pure rules behind Calls › Scheduled (day headers, join window, RSVP labels, durations),
/// kept out of the page so they are unit-tested.
public static class CallsSchedule
{
    /// "Join" opens this long before the start and stays open until the end.
    public static readonly TimeSpan JoinLead = TimeSpan.FromMinutes(10);
    public const string JoinTooltip = "You can join 10 minutes before the start";
    public const string OnlineLocation = "Online · iPop Meeting";

    public static readonly int[] DurationChoices = [15, 30, 60, 90];

    /// Reminder choices: "" = none, otherwise minutes before the start.
    public static readonly (string Value, string Label)[] ReminderChoices =
    [
        ("", "No reminder"), ("5", "5 minutes before"), ("15", "15 minutes before"),
        ("30", "30 minutes before"), ("60", "1 hour before"), ("1440", "1 day before"),
    ];

    public static bool CanJoin(DateTime startUtc, DateTime endUtc, DateTime nowUtc) =>
        nowUtc >= startUtc - JoinLead && nowUtc < endUtc;

    /// "Today", "Tomorrow", then "Tue, Sep 29" (English, independent of loaded culture).
    public static string DayLabel(DateTime localDate, DateTime today)
    {
        var d = localDate.Date;
        if (d == today.Date) return "Today";
        if (d == today.Date.AddDays(1)) return "Tomorrow";
        var label = $"{DatePickerMath.WeekdayShort[DatePickerMath.MondayIndex(d.DayOfWeek)]}, {DatePickerMath.MonthShort[d.Month - 1]} {d.Day}";
        return d.Year == today.Year ? label : $"{label}, {d.Year}";
    }

    public static string RsvpLabel(string? status) => status switch
    {
        "accept" => "Going",
        "maybe" => "Maybe",
        "decline" => "Not going",
        _ => "No response",
    };

    public static string DurationLabel(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        if (m < 60) return $"{m} min";
        return m % 60 == 0 ? $"{m / 60} hr" : $"{m / 60} hr {m % 60} min";
    }

    /// The chip that matches an existing duration, or null for "Custom".
    public static int? ChipFor(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        return DurationChoices.Contains(m) ? m : null;
    }

    public static bool IsOnline(string? location) =>
        string.IsNullOrWhiteSpace(location) || location.Trim() == OnlineLocation;

    /// Form validation; null when the form can be saved.
    public static string? Validate(string? title, DateTime start, DateTime end, DateTime now, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Meeting title is required.";
        if (isNew && start < now.AddMinutes(-1)) return "The start time has already passed.";
        if (end <= start) return "End time must be after start time.";
        return null;
    }

    /// Server contract: create treats null as "no reminder"; update needs 0 to clear it.
    public static int? ReminderPayload(string? choice, bool isNew) =>
        int.TryParse(choice, out var m) && m > 0 ? m : isNew ? null : 0;
}
