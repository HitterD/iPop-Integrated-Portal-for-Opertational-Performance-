using System.Globalization;

namespace IPop.Api.Endpoints;

/// Repeat rule for calendar events: "D" (daily), "W:1,4" (weekly on Mon and Thu; 1 = Monday … 7 = Sunday)
/// or "M" (monthly on the same date), "M:1,1" (first Monday), "M:-1,5" (last Friday), plus an end — ";U:2026-12-31" (until a date) or ";C:10" (count).
/// Expanded into separate occurrences when saved, at most <see cref="MaxOccurrences"/> and one year out.
public sealed record CalendarRecurrence(string Freq, IReadOnlyList<int> Days, DateOnly? Until, int? Count, int? MonthlyOrdinal = null, int? MonthlyWeekday = null)
{
    public const int MaxOccurrences = 100;
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(366);

    public static CalendarRecurrence? Parse(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;
        string? freq = null; List<int> days = []; DateOnly? until = null; int? count = null; int? monthlyOrdinal = null; int? monthlyWeekday = null;
        foreach (var part in rule.Split(';', StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':', 2);
            switch (kv[0])
            {
                case "D" or "M" when kv.Length == 1 && freq is null: freq = kv[0]; break;
                case "M" when kv.Length == 2 && freq is null:
                    var monthly = kv[1].Split(',');
                    if (monthly.Length != 2 || !int.TryParse(monthly[0], out var ordinal) ||
                        (ordinal != -1 && ordinal is not (>= 1 and <= 5)) ||
                        !int.TryParse(monthly[1], out var weekday) || weekday is < 1 or > 7) return null;
                    freq = "M"; monthlyOrdinal = ordinal; monthlyWeekday = weekday;
                    break;
                case "W" when freq is null:
                    freq = "W";
                    if (kv.Length > 1)
                    {
                        foreach (var day in kv[1].Split(','))
                        {
                            if (!int.TryParse(day, out var n) || n is < 1 or > 7) return null;
                            days.Add(n);
                        }
                        days = days.Distinct().Order().ToList();
                    }
                    break;
                case "U" when until is null && count is null && kv.Length == 2 && DateOnly.TryParseExact(kv[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u): until = u; break;
                case "C" when until is null && count is null && kv.Length == 2 && int.TryParse(kv[1], out var c) && c is > 0 and <= MaxOccurrences: count = c; break;
                default: return null;
            }
        }
        return freq is null ? null : new CalendarRecurrence(freq, days, until, count, monthlyOrdinal, monthlyWeekday);
    }

    public override string ToString()
    {
        var head = Freq == "W" && Days.Count > 0 ? $"W:{string.Join(',', Days)}" : Freq;
        if (Freq == "M" && MonthlyOrdinal is { } ordinal && MonthlyWeekday is { } weekday) head = $"M:{ordinal},{weekday}";
        return head + (Until is { } u ? $";U:{u:yyyy-MM-dd}" : Count is { } c ? $";C:{c}" : "");
    }

    /// Start times of matching occurrences on or after the requested start. <paramref name="first"/> is UTC; the weekday
    /// and date arithmetic happens in <paramref name="tz"/> so 09:00 stays 09:00 local.
    public List<DateTime> Expand(DateTime first, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(first, DateTimeKind.Utc), tz);
        var limit = local + Horizon;
        if (Until is { } u) limit = Min(limit, u.ToDateTime(TimeOnly.MaxValue));
        var max = Math.Min(Count ?? MaxOccurrences, MaxOccurrences);
        var days = Freq == "W" ? (Days.Count > 0 ? Days : [IsoDay(local.DayOfWeek)]) : [];
        var result = new List<DateTime>();
        for (var step = 0; result.Count < max && step <= 800; step++)
        {
            DateTime next;
            if (Freq == "D") next = local.AddDays(step);
            else if (Freq == "M")
            {
                if (MonthlyOrdinal is { } ordinal && MonthlyWeekday is { } weekday)
                {
                    var month = new DateTime(local.Year, local.Month, 1).AddMonths(step);
                    if (month > limit) break;
                    var firstDay = 1 + (weekday - IsoDay(month.DayOfWeek) + 7) % 7;
                    var day = ordinal == -1
                        ? firstDay + 7 * ((DateTime.DaysInMonth(month.Year, month.Month) - firstDay) / 7)
                        : firstDay + 7 * (ordinal - 1);
                    if (day > DateTime.DaysInMonth(month.Year, month.Month)) continue;
                    next = month.AddDays(day - 1).Add(local.TimeOfDay);
                    if (next < local) continue;
                }
                else
                {
                    next = local.AddMonths(step);
                    if (next > limit) break;
                    if (next.Day != local.Day) continue; // Skip short months, never drift.
                }
            }
            else next = local.AddDays(step);
            if (next > limit) break;
            if (Freq == "W" && !days.Contains(IsoDay(next.DayOfWeek))) continue;
            if (TryLocalToUtc(next, tz, out var utc)) result.Add(utc);
        }
        return result;
    }

    /// Invalid spring-forward wall times are skipped. Ambiguous fall-back times use the
    /// standard-time offset, matching TimeZoneInfo.ConvertTimeToUtc's deterministic policy.
    internal static bool TryLocalToUtc(DateTime local, TimeZoneInfo tz, out DateTime utc)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        utc = default;
        if (tz.IsInvalidTime(local)) return false;
        utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return true;
    }

    /// "Every Mon and Thu, until 31 Dec" — shown on the form and in details.
    public string Describe(DateTime firstLocal)
    {
        var what = Freq switch
        {
            "D" => "Every day",
            "M" when MonthlyOrdinal is { } ordinal && MonthlyWeekday is { } weekday =>
                $"Every month on the {(ordinal == -1 ? "last" : Ordinal(ordinal))} {DayName(weekday)}",
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
