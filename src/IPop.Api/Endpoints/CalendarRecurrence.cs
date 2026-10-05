using System.Globalization;

namespace IPop.Api.Endpoints;

/// Repeat rule for calendar events: "D" (daily), "W:1,4" (weekly on Mon and Thu; 1 = Monday … 7 = Sunday)
/// or "M" (monthly on the same date), plus an end — ";U:2026-12-31" (until a date) or ";C:10" (count).
/// Expanded into separate occurrences when saved, at most <see cref="MaxOccurrences"/> and one year out.
public sealed record CalendarRecurrence(string Freq, IReadOnlyList<int> Days, DateOnly? Until, int? Count)
{
    public const int MaxOccurrences = 100;
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(366);

    public static CalendarRecurrence? Parse(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        string? freq = null; List<int> days = []; DateOnly? until = null; int? count = null;
        foreach (var part in rule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2);
            switch (kv[0])
            {
                case "D": freq = "D"; break;
                case "M": freq = "M"; break;
                case "W":
                    freq = "W";
                    if (kv.Length > 1)
                        days = kv[1].Split(',').Select(d => int.TryParse(d, out var n) ? n : 0).Where(n => n is >= 1 and <= 7).Distinct().Order().ToList();
                    break;
                case "U" when kv.Length > 1 && DateOnly.TryParseExact(kv[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u): until = u; break;
                case "C" when kv.Length > 1 && int.TryParse(kv[1], out var c) && c > 0: count = Math.Min(c, MaxOccurrences); break;
            }
        }
        return freq is null ? null : new CalendarRecurrence(freq, days, until, count);
    }

    public override string ToString()
    {
        var head = Freq == "W" && Days.Count > 0 ? $"W:{string.Join(',', Days)}" : Freq;
        return head + (Until is { } u ? $";U:{u:yyyy-MM-dd}" : Count is { } c ? $";C:{c}" : "");
    }

    /// Start times of every occurrence, the first one included. <paramref name="first"/> is UTC; the weekday
    /// and date arithmetic happens in <paramref name="tz"/> so 09:00 stays 09:00 local.
    public List<DateTime> Expand(DateTime first, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(first, DateTimeKind.Utc), tz);
        var limit = local + Horizon;
        if (Until is { } u) limit = Min(limit, u.ToDateTime(TimeOnly.MaxValue));
        var max = Math.Min(Count ?? MaxOccurrences, MaxOccurrences);
        var days = Freq == "W" ? (Days.Count > 0 ? Days : [IsoDay(local.DayOfWeek)]) : [];
        var result = new List<DateTime>();
        void Add(DateTime l) => result.Add(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(l, DateTimeKind.Unspecified), tz));

        Add(local);
        for (var step = 1; result.Count < max; step++)
        {
            DateTime next;
            if (Freq == "D") next = local.AddDays(step);
            else if (Freq == "M")
            {
                var m = local.AddMonths(step);
                if (m.Day != local.Day) continue; // 31st in a 30-day month: skip, don't drift
                next = m;
            }
            else
            {
                next = local.AddDays(step);
                if (!days.Contains(IsoDay(next.DayOfWeek))) { if (step > 400) break; continue; }
            }
            if (next > limit || step > 800) break;
            Add(next);
        }
        return result;
    }

    /// "Every Mon and Thu, until 31 Dec" — shown on the form and in details.
    public string Describe(DateTime firstLocal)
    {
        var what = Freq switch
        {
            "D" => "Every day",
            "M" => $"Every month on the {Ordinal(firstLocal.Day)}",
            _ => "Every " + JoinNames((Days.Count > 0 ? Days : [IsoDay(firstLocal.DayOfWeek)]).Select(DayName).ToList()),
        };
        return what + (Until is { } u ? $", until {u:d MMM yyyy}" : Count is { } c ? $", {c} times" : "");
    }

    public static int IsoDay(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;
    private static string DayName(int iso) => iso switch { 1 => "Mon", 2 => "Tue", 3 => "Wed", 4 => "Thu", 5 => "Fri", 6 => "Sat", _ => "Sun" };
    private static string JoinNames(List<string> n) => n.Count <= 1 ? string.Concat(n) : string.Join(", ", n.Take(n.Count - 1)) + " and " + n[^1];
    private static string Ordinal(int d) => d + (d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
