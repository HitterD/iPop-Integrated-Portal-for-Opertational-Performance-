namespace IPop.Application.Projects;

/// The small RRULE subset the drawer's "Ulangi" control produces:
/// FREQ=DAILY|WEEKLY|MONTHLY, optional INTERVAL=1..99, WEEKLY may carry BYDAY=MO,TU,..,
/// MONTHLY may carry BYMONTHDAY=1..31 (clamped to the month's last day).
/// Shared by the API (validation, spawning the next instance) and the Web preview.
public sealed record TaskRecurrence(string Freq, int Interval, IReadOnlyList<DayOfWeek> ByDay, int? ByMonthDay)
{
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static TaskRecurrence? Parse(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule) || rule.Length > 200) return null;
        string? freq = null;
        var interval = 1;
        var byDay = new List<DayOfWeek>();
        int? byMonthDay = null;
        foreach (var part in rule.Trim().ToUpperInvariant().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2 || kv[1].Length == 0) return null;
            switch (kv[0])
            {
                case "FREQ" when freq is null && kv[1] is "DAILY" or "WEEKLY" or "MONTHLY":
                    freq = kv[1];
                    break;
                case "INTERVAL" when int.TryParse(kv[1], out var iv) && iv is >= 1 and <= 99:
                    interval = iv;
                    break;
                case "BYDAY" when byDay.Count == 0:
                    foreach (var code in kv[1].Split(',', StringSplitOptions.TrimEntries))
                    {
                        var idx = Array.IndexOf(DayCodes, code);
                        if (idx < 0) return null;
                        if (!byDay.Contains((DayOfWeek)idx)) byDay.Add((DayOfWeek)idx);
                    }
                    break;
                case "BYMONTHDAY" when byMonthDay is null && int.TryParse(kv[1], out var md) && md is >= 1 and <= 31:
                    byMonthDay = md;
                    break;
                default:
                    return null;
            }
        }
        if (freq is null) return null;
        if (byDay.Count > 0 && freq != "WEEKLY") return null;
        if (byMonthDay is not null && freq != "MONTHLY") return null;
        byDay.Sort();
        return new TaskRecurrence(freq, interval, byDay, byMonthDay);
    }

    public static bool IsValid(string? rule) => Parse(rule) is not null;

    /// Canonical text form, so equal rules compare equal and the stored value stays tidy.
    public override string ToString()
    {
        var s = $"FREQ={Freq}";
        if (Interval != 1) s += $";INTERVAL={Interval}";
        if (ByDay.Count > 0) s += ";BYDAY=" + string.Join(',', ByDay.Select(d => DayCodes[(int)d]));
        if (ByMonthDay is { } md) s += $";BYMONTHDAY={md}";
        return s;
    }

    /// The first occurrence strictly after <paramref name="from"/> (date part only; Kind preserved).
    public DateTime Next(DateTime from)
    {
        var d = from.Date;
        switch (Freq)
        {
            case "DAILY":
                return d.AddDays(Interval);
            case "WEEKLY" when ByDay.Count == 0:
                return d.AddDays(7 * Interval);
            case "WEEKLY":
            {
                // Later days in the same week first, then the first listed day `Interval` weeks on.
                var weekStart = d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday
                foreach (var day in ByDay.OrderBy(x => ((int)x + 6) % 7))
                {
                    var candidate = weekStart.AddDays(((int)day + 6) % 7);
                    if (candidate > d) return candidate;
                }
                var first = ByDay.OrderBy(x => ((int)x + 6) % 7).First();
                return weekStart.AddDays(7 * Interval + ((int)first + 6) % 7);
            }
            default: // MONTHLY
            {
                var target = ByMonthDay ?? from.Day;
                var thisMonth = Clamp(d.Year, d.Month, target, from.Kind);
                if (ByMonthDay is not null && thisMonth > d && Interval == 1) return thisMonth;
                var m = new DateTime(d.Year, d.Month, 1, 0, 0, 0, from.Kind).AddMonths(Interval);
                return Clamp(m.Year, m.Month, target, from.Kind);
            }
        }
    }

    public IReadOnlyList<DateTime> Upcoming(DateTime from, int count)
    {
        var list = new List<DateTime>();
        var cur = from;
        for (var i = 0; i < Math.Clamp(count, 0, 104); i++) list.Add(cur = Next(cur));
        return list;
    }

    private static DateTime Clamp(int year, int month, int day, DateTimeKind kind) =>
        new(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, kind);
}
