using IPop.Web.Components;

namespace IPop.Web.Pages;

/// Pure rules behind Calls › Scheduled (day headers, join window, RSVP labels, durations),
/// kept out of the page so they are unit-tested.
public static class CallsSchedule
{
    /// "Gabung" opens this long before the start and stays open until the end.
    public static readonly TimeSpan JoinLead = TimeSpan.FromMinutes(10);
    public const string JoinTooltip = "Bisa gabung 10 menit sebelum mulai";
    public const string OnlineLocation = "Online · iPop Meeting";

    public static readonly int[] DurationChoices = [15, 30, 60, 90];

    /// Reminder choices: "" = none, otherwise minutes before the start.
    public static readonly (string Value, string Label)[] ReminderChoices =
    [
        ("", "Tanpa pengingat"), ("5", "5 menit sebelumnya"), ("15", "15 menit sebelumnya"),
        ("30", "30 menit sebelumnya"), ("60", "1 jam sebelumnya"), ("1440", "1 hari sebelumnya"),
    ];

    public static bool CanJoin(DateTime startUtc, DateTime endUtc, DateTime nowUtc) =>
        nowUtc >= startUtc - JoinLead && nowUtc < endUtc;

    /// "Hari ini", "Besok", then "Sen, 29 Sep" (Indonesian, independent of loaded culture).
    public static string DayLabel(DateTime localDate, DateTime today)
    {
        var d = localDate.Date;
        if (d == today.Date) return "Hari ini";
        if (d == today.Date.AddDays(1)) return "Besok";
        var label = $"{DatePickerMath.WeekdayShort[DatePickerMath.MondayIndex(d.DayOfWeek)]}, {d.Day} {DatePickerMath.MonthShort[d.Month - 1]}";
        return d.Year == today.Year ? label : $"{label} {d.Year}";
    }

    public static string RsvpLabel(string? status) => status switch
    {
        "accept" => "Hadir",
        "maybe" => "Mungkin",
        "decline" => "Tidak",
        _ => "Belum dijawab",
    };

    public static string DurationLabel(TimeSpan d)
    {
        var m = (int)Math.Round(d.TotalMinutes);
        if (m < 60) return $"{m} mnt";
        return m % 60 == 0 ? $"{m / 60} jam" : $"{m / 60} j {m % 60} mnt";
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
        if (string.IsNullOrWhiteSpace(title)) return "Judul rapat wajib diisi.";
        if (isNew && start < now.AddMinutes(-1)) return "Waktu mulai sudah lewat.";
        if (end <= start) return "Waktu selesai harus setelah waktu mulai.";
        return null;
    }

    /// Server contract: create treats null as "no reminder"; update needs 0 to clear it.
    public static int? ReminderPayload(string? choice, bool isNew) =>
        int.TryParse(choice, out var m) && m > 0 ? m : isNew ? null : 0;
}
