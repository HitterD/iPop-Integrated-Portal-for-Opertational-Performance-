using System.Globalization;
using System.Text.RegularExpressions;

namespace IPop.Web.Components;

public enum DatePickerMode { Date, Time, DateTime }

/// Pure calendar / parsing helpers behind the shared date & time pickers.
/// English names are explicit arrays so output never depends on the WASM
/// globalization data that happens to be loaded. Weeks start on Monday.
public static partial class DatePickerMath
{
    public static readonly string[] WeekdayMin = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    public static readonly string[] WeekdayShort = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    public static readonly string[] WeekdayLong = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
    public static readonly string[] MonthLong =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    public static readonly string[] MonthShort =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// Anchor date used when a time-only value travels through the DateTime-based core.
    public static readonly DateTime TimeAnchor = DateTime.MinValue;

    /// 0 = Monday … 6 = Sunday.
    public static int MondayIndex(DayOfWeek d) => ((int)d + 6) % 7;

    /// Six full Monday-first weeks (42 days) covering the given month, so the popover
    /// keeps a constant height while paging.
    public static DateOnly[] MonthGrid(int year, int month)
    {
        var first = new DateOnly(year, month, 1);
        var start = first.AddDays(-MondayIndex(first.DayOfWeek));
        var days = new DateOnly[42];
        for (var i = 0; i < days.Length; i++) days[i] = start.AddDays(i);
        return days;
    }

    public static DateOnly StartOfWeek(DateOnly d) => d.AddDays(-MondayIndex(d.DayOfWeek));
    public static DateOnly EndOfWeek(DateOnly d) => d.AddDays(6 - MondayIndex(d.DayOfWeek));

    /// The Monday strictly after <paramref name="today"/> ("Senin depan").
    public static DateOnly NextMonday(DateOnly today) => today.AddDays(7 - MondayIndex(today.DayOfWeek));

    public static bool IsOutOfRange(DateOnly d, DateOnly? min, DateOnly? max)
        => (min is { } a && d < a) || (max is { } b && d > b);

    public static DateOnly Clamp(DateOnly d, DateOnly? min, DateOnly? max)
    {
        if (min is { } a && d < a) return a;
        if (max is { } b && d > b) return b;
        return d;
    }

    public static IReadOnlyList<TimeOnly> TimeSlots(int stepMinutes)
    {
        var step = Math.Clamp(stepMinutes, 1, 720);
        var list = new List<TimeOnly>(1440 / step + 1);
        for (var m = 0; m < 1440; m += step) list.Add(new TimeOnly(m / 60, m % 60));
        return list;
    }

    /// Rounds a time up to the next step boundary (clamped to 23:59).
    public static TimeOnly CeilToStep(TimeOnly t, int stepMinutes)
    {
        var step = Math.Clamp(stepMinutes, 1, 720);
        var mins = t.Hour * 60 + t.Minute + (t.Second > 0 || t.Millisecond > 0 ? 1 : 0);
        var up = (mins + step - 1) / step * step;
        if (up >= 1440) return new TimeOnly(23, 59);
        return new TimeOnly(up / 60, up % 60);
    }

    public static DateTime TruncateToMinute(DateTime v) => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, v.Kind);

    // ---- formatting -------------------------------------------------------

    /// "Mon, 28 Sep 2026"
    public static string FormatDate(DateOnly d)
        => $"{WeekdayShort[MondayIndex(d.DayOfWeek)]}, {d.Day:00} {MonthShort[d.Month - 1]} {d.Year}";

    /// Pill label (meeting sheets, mockup v3): "Today, 13 Oct", "Tomorrow, 14 Oct", "Wed 21 Oct", "Fri 8 Jan 2027".
    public static string FormatPill(DateOnly d, DateOnly today)
    {
        var dm = $"{d.Day} {MonthShort[d.Month - 1]}";
        if (d == today) return "Today, " + dm;
        if (d == today.AddDays(1)) return "Tomorrow, " + dm;
        var s = $"{WeekdayShort[MondayIndex(d.DayOfWeek)]} {dm}";
        return d.Year == today.Year ? s : $"{s} {d.Year}";
    }

    /// "28 Sep 2026" (dense table cells)
    public static string FormatDateCompact(DateOnly d) => $"{d.Day:00} {MonthShort[d.Month - 1]} {d.Year}";

    /// "Monday, 28 September 2026" (screen-reader labels)
    public static string FormatLong(DateOnly d)
        => $"{WeekdayLong[MondayIndex(d.DayOfWeek)]}, {d.Day} {MonthLong[d.Month - 1]} {d.Year}";

    public static string FormatTime(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// "28 Sep 2026 · 09:45"
    public static string FormatDateTime(DateTime v)
        => $"{FormatDateCompact(DateOnly.FromDateTime(v))} · {FormatTime(TimeOnly.FromDateTime(v))}";

    /// "September 2026"
    public static string MonthTitle(int year, int month) => $"{MonthLong[month - 1]} {year}";

    // ---- parsing ----------------------------------------------------------

    private static readonly Dictionary<string, int> MonthNames = BuildMonthNames();

    private static Dictionary<string, int> BuildMonthNames()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Indonesian names stay accepted so typed input like "28 Agustus 2026" still parses.
        string[] id = ["januari", "februari", "maret", "april", "mei", "juni", "juli", "agustus", "september", "oktober", "november", "desember"];
        string[] idShort = ["jan", "feb", "mar", "apr", "mei", "jun", "jul", "agu", "sep", "okt", "nov", "des"];
        for (var i = 0; i < 12; i++)
        {
            map[MonthLong[i]] = i + 1;
            map[MonthShort[i]] = i + 1;
            map[id[i]] = i + 1;
            map[idShort[i]] = i + 1;
        }
        map["agt"] = 8; map["ags"] = 8; map["sept"] = 9; map["peb"] = 2; map["nop"] = 11;
        return map;
    }

    [GeneratedRegex(@"^(\d{4})-(\d{1,2})-(\d{1,2})$")] private static partial Regex IsoDate();
    [GeneratedRegex(@"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{4}|\d{2})$")] private static partial Regex DmyDate();
    [GeneratedRegex(@"^(\d{1,2})\s+([a-z]+)\.?\s+(\d{4})$")] private static partial Regex NamedDate();
    [GeneratedRegex(@"^[a-z]+,?\s+(?=\d)")] private static partial Regex LeadingWeekday();
    [GeneratedRegex(@"^(\d{1,2})(?:[:.](\d{2}))?$")] private static partial Regex HourMinute();
    [GeneratedRegex(@"^(\d{2})(\d{2})$")] private static partial Regex Compact4();
    [GeneratedRegex(@"(\d)t(\d)")] private static partial Regex IsoT();
    [GeneratedRegex(@"^(.*?)[\s,]+(\d{1,2}[:.]\d{2})$")] private static partial Regex TrailingTime();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private static string Normalize(string s)
        => Spaces().Replace(s.Replace('·', ' ').Replace(' ', ' ').Trim().ToLowerInvariant(), " ");

    /// Accepts dd/MM/yyyy (also d/M/yy, dots or dashes), yyyy-MM-dd, "28 Sep 2026"
    /// and the display form "Mon, 28 Sep 2026".
    public static bool TryParseDate(string? input, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = LeadingWeekday().Replace(Normalize(input), "");

        int y, m, d;
        Match mt;
        if ((mt = IsoDate().Match(s)).Success)
        {
            y = int.Parse(mt.Groups[1].Value); m = int.Parse(mt.Groups[2].Value); d = int.Parse(mt.Groups[3].Value);
        }
        else if ((mt = DmyDate().Match(s)).Success)
        {
            d = int.Parse(mt.Groups[1].Value); m = int.Parse(mt.Groups[2].Value); y = int.Parse(mt.Groups[3].Value);
            if (mt.Groups[3].Value.Length == 2) y += 2000;
        }
        else if ((mt = NamedDate().Match(s)).Success && MonthNames.TryGetValue(mt.Groups[2].Value, out m))
        {
            d = int.Parse(mt.Groups[1].Value); y = int.Parse(mt.Groups[3].Value);
        }
        else return false;

        if (y < 1 || y > 9999 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    /// Accepts HH:mm, H:mm, HH.mm, HHmm and a bare hour.
    public static bool TryParseTime(string? input, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = Normalize(input);
        int h, m;
        Match mt;
        if ((mt = HourMinute().Match(s)).Success)
        {
            h = int.Parse(mt.Groups[1].Value);
            m = mt.Groups[2].Success ? int.Parse(mt.Groups[2].Value) : 0;
        }
        else if ((mt = Compact4().Match(s)).Success)
        {
            h = int.Parse(mt.Groups[1].Value); m = int.Parse(mt.Groups[2].Value);
        }
        else return false;
        if (h > 23 || m > 59) return false;
        time = new TimeOnly(h, m);
        return true;
    }

    /// Date followed by an optional time ("28/09/2026 09:45", "2026-09-28T09:45",
    /// "28 Sep 2026 · 09:45"). A date without a time takes <paramref name="fallbackTime"/>.
    public static bool TryParseDateTime(string? input, TimeOnly fallbackTime, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = IsoT().Replace(Normalize(input), "$1 $2");
        var mt = TrailingTime().Match(s);
        string datePart = s;
        var time = fallbackTime;
        if (mt.Success)
        {
            if (!TryParseTime(mt.Groups[2].Value, out time)) return false;
            datePart = mt.Groups[1].Value;
        }
        if (!TryParseDate(datePart, out var d)) return false;
        value = d.ToDateTime(time);
        return true;
    }
}

/// Converts the pickers' supported bound types to and from the DateTime? the core works in.
public static class PickerValue
{
    public static bool IsNullable<T>() => default(T) is null;

    public static DateTime? ToCore(object? v) => v switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        TimeOnly t => DatePickerMath.TimeAnchor.Add(t.ToTimeSpan()),
        TimeSpan ts => DatePickerMath.TimeAnchor.Add(ts),
        _ => null,
    };

    public static T FromCore<T>(DateTime? v)
    {
        var t = typeof(T);
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (v is null)
        {
            if (!IsNullable<T>()) throw new InvalidOperationException($"Cannot assign an empty value to non-nullable {t.Name}.");
            return default!;
        }
        object o = u == typeof(DateTime) ? v.Value
            : u == typeof(DateOnly) ? DateOnly.FromDateTime(v.Value)
            : u == typeof(TimeOnly) ? TimeOnly.FromDateTime(v.Value)
            : u == typeof(TimeSpan) ? v.Value.TimeOfDay
            : throw new NotSupportedException($"Picker value type {t.Name} is not supported.");
        return (T)o;
    }

    public static void EnsureSupported<T>(params Type[] allowed)
    {
        var u = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (!allowed.Contains(u))
            throw new NotSupportedException($"{typeof(T).Name} is not supported here; use {string.Join(", ", allowed.Select(a => a.Name))} (optionally nullable).");
    }
}
