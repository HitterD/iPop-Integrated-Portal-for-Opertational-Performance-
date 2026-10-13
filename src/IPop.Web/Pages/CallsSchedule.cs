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

    /// The meeting kind (voice | room | inperson, null = plain event) to colour by: the server's Kind when it sent a
    /// valid one, else the location fallback (MeetingKind.Infer — online/empty with people = voice, SJA Hall = room).
    public static string? KindOf(string? kind, string? location, bool hasPeople) =>
        IPop.Core.Entities.MeetingKind.Of(kind, location, hasPeople);

    /// Countdown chip text + state: "live" (running), "soon" (starts within 15 min), "later", "ended".
    public readonly record struct CountdownLabel(string Text, string State);

    public static readonly TimeSpan SoonWindow = TimeSpan.FromMinutes(15);

    /// "Live now"; "Starts in 10 min" (≤ 15 min, minutes rounded up); later today "Starts in 3 h 40 min"; "Tomorrow";
    /// later this (Monday-first) week the weekday ("Thu"); further out "Mon 19 Oct"; "Ended" once over.
    public static CountdownLabel Countdown(DateTime startUtc, DateTime endUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (nowUtc >= endUtc) return new("Ended", "ended");
        if (nowUtc >= startUtc) return new("Live now", "live");
        var left = startUtc - nowUtc;
        var mins = (int)Math.Ceiling(left.TotalMinutes);
        if (left <= SoonWindow) return new($"Starts in {mins} min", "soon");
        var start = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), zone).Date;
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        if (start == today)
            return new(mins < 60 ? $"Starts in {mins} min" : mins % 60 == 0 ? $"Starts in {mins / 60} h" : $"Starts in {mins / 60} h {mins % 60} min", "later");
        if (start == today.AddDays(1)) return new("Tomorrow", "later");
        var weekStart = today.AddDays(-DatePickerMath.MondayIndex(today.DayOfWeek));
        var day = DatePickerMath.WeekdayShort[DatePickerMath.MondayIndex(start.DayOfWeek)];
        return start < weekStart.AddDays(7)
            ? new(day, "later")
            : new($"{day} {start.Day} {DatePickerMath.MonthShort[start.Month - 1]}", "later");
    }

    /// Scheduled list group (v3 §5): Today (also anything that started earlier and still runs), Tomorrow, This week
    /// (to Sunday of the Monday-first week), Later. <paramref name="localStart"/> and <paramref name="today"/> are local.
    public readonly record struct DayGroup(string Key, string Label);

    public static DayGroup Group(DateTime localStart, DateTime today)
    {
        var d = localStart.Date; var t = today.Date;
        if (d <= t) return new("today", "Today");
        if (d == t.AddDays(1)) return new("tomorrow", "Tomorrow");
        var weekEnd = t.AddDays(6 - DatePickerMath.MondayIndex(t.DayOfWeek));
        return d <= weekEnd ? new("week", "This week") : new("later", "Later");
    }

    /// Schedule → Meeting room opens the booking sheet on the first room free now, else the first room.
    public static string DefaultRoom(IEnumerable<IPop.Application.MeetingRooms.RoomFreeDto>? rooms) =>
        rooms?.FirstOrDefault(r => r.Free)?.Key ?? IPop.Application.Hall.HallBooking.Rooms[0].Key;

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
