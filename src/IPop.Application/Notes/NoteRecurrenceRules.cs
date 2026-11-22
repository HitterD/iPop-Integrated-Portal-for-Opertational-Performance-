using IPop.Application.Projects;

namespace IPop.Application.Notes;

/// When a repeating page is due, and what its title says. Pure (no clock, no database) so every rule can be tested.
/// The schedule text is the same RRULE subset as Projects' repeating tasks (<see cref="TaskRecurrence"/>).
public static class NoteRecurrenceRules
{
    /// The hour (local time, Asia/Jakarta) from which a day's page may be made.
    public const int CreateFromHour = 6;
    public const int MaxRulesPerPerson = 20;

    public static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// The wall-clock time rules are judged in: Asia/Jakarta on every PC and pod (falls back to the Windows zone name, then UTC+7).
    public static DateTime LocalNow(DateTime utcNow)
    {
        foreach (var id in new[] { "Asia/Jakarta", "SE Asia Standard Time" })
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).AddHours(7);
    }

    private static readonly IReadOnlySet<string> ValidDays = new HashSet<string>(StringComparer.Ordinal)
        { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    /// "daily" | "workdays" | "weekly" (+ days) | "monthly" (+ day of month) → the stored rule, or null when it is not valid.
    public static string? FromChoice(string? kind, IReadOnlyList<string>? days, int? monthDay)
    {
        if (kind == "weekly")
        {
            if (days is not { Count: > 0 and <= 7 }) return null;
            var cleaned = new List<string>(days.Count);
            foreach (var raw in days)
            {
                if (raw is null) return null;
                var d = raw.Trim().ToUpperInvariant();
                if (!ValidDays.Contains(d)) return null;
                if (!cleaned.Contains(d)) cleaned.Add(d);
            }
            return TaskRecurrence.Parse("FREQ=WEEKLY;BYDAY=" + string.Join(',', cleaned))?.ToString();
        }
        string? rule = kind switch
        {
            "daily" => "FREQ=DAILY",
            "workdays" => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            "monthly" when monthDay is >= 1 and <= 31 => $"FREQ=MONTHLY;BYMONTHDAY={monthDay}",
            _ => null,
        };
        return TaskRecurrence.Parse(rule)?.ToString();
    }

    /// Is a page due for <paramref name="today"/>? It must be on or after the first allowed day, not already made for today, and the rule
    /// must fall on today (counted from the rule's start, so "every 2 weeks" keeps its rhythm).
    public static bool IsDueOn(string rule, DateOnly startsOn, DateOnly? lastCreatedFor, DateOnly today)
    {
        if (today < startsOn) return false;
        if (lastCreatedFor is { } last && last >= today) return false;
        var r = TaskRecurrence.Parse(rule);
        return r is not null && OccursOn(r, startsOn, today);
    }

    /// Does the rule produce an occurrence exactly on <paramref name="day"/>? Counted from the start day, which is always an occurrence
    /// of its own when it matches the rule's weekday / date (a weekly-on-Monday rule set up on a Monday makes that day's page), so "every
    /// 2 weeks" keeps the rhythm of the week it was set up in.
    public static bool OccursOn(TaskRecurrence r, DateOnly startsOn, DateOnly day)
    {
        if (day < startsOn) return false;
        var daysSince = day.DayNumber - startsOn.DayNumber;
        switch (r.Freq)
        {
            case "DAILY":
                return daysSince % r.Interval == 0;
            case "WEEKLY":
            {
                var weekday = day.DayOfWeek;
                var chosen = r.ByDay.Count > 0 ? r.ByDay : [startsOn.DayOfWeek];
                if (!chosen.Contains(weekday)) return false;
                // whole weeks between the Monday of the start week and the Monday of this week
                var weeks = (MondayOf(day).DayNumber - MondayOf(startsOn).DayNumber) / 7;
                return weeks % r.Interval == 0;
            }
            default: // MONTHLY
            {
                var target = r.ByMonthDay ?? startsOn.Day;
                var dueDay = Math.Min(target, DateTime.DaysInMonth(day.Year, day.Month));
                if (day.Day != dueDay) return false;
                var months = (day.Year - startsOn.Year) * 12 + day.Month - startsOn.Month;
                return months % r.Interval == 0;
            }
        }
    }

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// "Weekly ICT — 24 Nov 2026" (English month names, so the same title on every PC).
    public static string TitleFor(string? templateTitle, DateOnly day)
    {
        var name = string.IsNullOrWhiteSpace(templateTitle) ? "Untitled" : templateTitle.Trim();
        var date = day.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = $" — {date}";
        var full = name + suffix;
        // keep the date visible when the name is long (without splitting UTF-16 surrogate pairs): the title limit is 200
        if (full.Length <= 200) return full;
        var limit = Math.Max(0, 200 - suffix.Length);
        var info = new System.Globalization.StringInfo(name);
        var cut = new System.Text.StringBuilder();
        for (var i = 0; i < info.LengthInTextElements; i++)
        {
            var el = info.SubstringByTextElements(i, 1);
            if (cut.Length + el.Length > limit) break;
            cut.Append(el);
        }
        return (cut.Length == 0 ? "Untitled" : cut.ToString().TrimEnd()) + suffix;
    }

    /// Short, human text for the Repeat dialog ("Every weekday", "Every Monday and Wednesday", "Monthly on the 15th").
    public static string Describe(string? rule)
    {
        var r = TaskRecurrence.Parse(rule);
        if (r is null) return "Doesn’t repeat";
        switch (r.Freq)
        {
            case "DAILY": return r.Interval == 1 ? "Every day" : $"Every {r.Interval} days";
            case "WEEKLY" when r.ByDay.Count == 5 && Workdays.All(r.ByDay.Contains): return "Every weekday";
            case "WEEKLY" when r.ByDay.Count > 0:
                var names = r.ByDay.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()).ToList();
                var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
                return r.Interval == 1 ? $"Every {list}" : $"Every {r.Interval} weeks on {list}";
            case "WEEKLY": return r.Interval == 1 ? "Every week" : $"Every {r.Interval} weeks";
            default:
                var dayText = r.ByMonthDay is { } md ? $" on the {md}{Suffix(md)}" : "";
                return r.Interval == 1 ? $"Monthly{dayText}" : $"Every {r.Interval} months{dayText}";
        }
    }

    private static string Suffix(int n) => n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}
