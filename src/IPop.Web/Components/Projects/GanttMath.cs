using System.Globalization;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

public enum GanttZoom { Day, Week, Month }

/// A bar (or milestone) on the grid, in pixels. Milestones have Left == Right (their centre).
public sealed record GanttBox(double Left, double Right, int Row, bool IsMilestone);

public sealed record GanttBand(int Day, int Length, string Label);

/// Date and pixel math for the v2 Gantt (docs/mockups/ipop-apple/projects-v2.html ganttView).
public static class GanttMath
{
    public const int RowHeight = 34;
    public const int HeaderHeight = 52;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static int PxPerDay(GanttZoom z) => z switch { GanttZoom.Day => 30, GanttZoom.Week => 13, _ => 5 };

    /// The Monday on or before three days ahead of the first date, so weekend stripes repeat from the origin.
    public static DateTime OriginFor(DateTime first)
    {
        var d = first.Date.AddDays(-3);
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1);
        return d;
    }

    /// Whole weeks from the origin to two weeks past the last date.
    public static int TotalDays(DateTime origin, DateTime last)
    {
        var n = (int)(last.Date - origin).TotalDays + 15;
        return (n + 6) / 7 * 7;
    }

    public static int DayIndex(DateTime origin, DateTime d) => (int)(d.Date - origin.Date).TotalDays;

    public static bool IsWeekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// Saturday + Sunday stripes: 5 clear days, 2 shaded, repeating from a Monday origin.
    public static string WeekendBackground(int px)
        => $"background-image:repeating-linear-gradient(90deg,transparent 0 {5 * px}px,var(--pv2g-wknd) {5 * px}px {7 * px}px)";

    public static double TodayX(DateTime origin, DateTime today, int px) => DayIndex(origin, today) * px;

    public static string FormatShort(DateTime d) => d.ToString("d MMM", En);
    public static string FormatLong(DateTime d) => d.ToString("ddd d MMM", En);

    public static IReadOnlyList<GanttBand> MonthBands(DateTime origin, int totalDays, GanttZoom zoom)
    {
        var list = new List<GanttBand>();
        for (var d = 0; d < totalDays; d++)
        {
            var x = origin.AddDays(d);
            if (x.Day != 1 && d != 0) continue;
            var left = DateTime.DaysInMonth(x.Year, x.Month) - x.Day + 1;
            list.Add(new GanttBand(d, Math.Min(left, totalDays - d),
                zoom == GanttZoom.Month ? x.ToString("MMM", En) : x.ToString("MMM yyyy", En)));
        }
        return list;
    }

    /// Day zoom: one cell per day (the day number). Otherwise one cell per week ("W46 · 9 Nov" / "W46").
    public static IReadOnlyList<GanttBand> SubBands(DateTime origin, int totalDays, GanttZoom zoom)
    {
        var list = new List<GanttBand>();
        if (zoom == GanttZoom.Day)
        {
            for (var d = 0; d < totalDays; d++) list.Add(new GanttBand(d, 1, origin.AddDays(d).Day.ToString(En)));
            return list;
        }
        for (var d = 0; d < totalDays; d += 7)
        {
            var x = origin.AddDays(d);
            var w = ISOWeek.GetWeekOfYear(x);
            list.Add(new GanttBand(d, 7, zoom == GanttZoom.Week ? $"W{w} · {x.Day} {x.ToString("MMM", En)}" : $"W{w}"));
        }
        return list;
    }

    /// Holidays and breaks merged into shaded bands: consecutive days, or days only weekends apart, are one band.
    public static IReadOnlyList<GanttBand> HolidayBands(IEnumerable<GanttHolidayDto> holidays, DateTime origin, int totalDays, GanttZoom zoom)
    {
        var days = holidays.GroupBy(h => h.Date.Date)
            .Select(g => (Date: g.Key, Title: g.Select(x => x.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))))
            .OrderBy(x => x.Date).ToList();
        var bands = new List<GanttBand>();
        for (var i = 0; i < days.Count;)
        {
            var first = days[i];
            var last = first;
            var j = i + 1;
            while (j < days.Count && OnlyWeekendsBetween(last.Date, days[j].Date)) { last = days[j]; j++; }
            i = j;
            var from = DayIndex(origin, first.Date);
            var to = DayIndex(origin, last.Date);
            if (to < 0 || from >= totalDays) continue;
            var multi = last.Date > first.Date;
            var title = string.IsNullOrWhiteSpace(first.Title) ? (multi ? "Break" : "Holiday") : first.Title!;
            var label = zoom == GanttZoom.Month ? (multi ? "Break" : "")
                : multi ? $"{title} {FormatShort(first.Date)} – {FormatShort(last.Date)}" : title;
            var start = Math.Max(0, from);
            bands.Add(new GanttBand(start, Math.Min(to, totalDays - 1) - start + 1, label));
        }
        return bands;
    }

    private static bool OnlyWeekendsBetween(DateTime a, DateTime b)
    {
        for (var d = a.AddDays(1); d < b; d = d.AddDays(1))
            if (!IsWeekend(d)) return false;
        return true;
    }

    /// Calendar days that N working days of slack cover after a task's finish (weekends and holidays skipped).
    public static int SlackCalendarDays(DateTime end, int slackWorkingDays, ISet<DateTime> holidays)
    {
        var d = end.Date;
        var n = 0;
        var left = slackWorkingDays;
        while (left > 0 && n < 3650)
        {
            d = d.AddDays(1);
            n++;
            if (!IsWeekend(d) && !holidays.Contains(d)) left--;
        }
        return n;
    }

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// Arrow route between two boxes, like the mockup. FS leaves the predecessor's finish and enters the
    /// successor's start (with a detour when the successor starts before the predecessor ends); SS leaves the
    /// predecessor's start; FF enters the successor's finish from the right.
    public static string ArrowPath(string linkType, GanttBox pred, GanttBox succ)
    {
        double y1 = pred.Row * RowHeight + 17, y2 = succ.Row * RowHeight + 17;
        switch (linkType)
        {
            case "SS":
            {
                var x1 = pred.IsMilestone ? pred.Left - 7 : pred.Left;
                var x2 = succ.IsMilestone ? succ.Left - 7 : succ.Left;
                var lead = Math.Min(x1, x2 - 6) - 8;
                return $"M{N(x1)} {N(y1)} H{N(lead)} V{N(y2)} H{N(x2 - 1)}";
            }
            case "FF":
            {
                var x1 = pred.IsMilestone ? pred.Right + 7 : pred.Right;
                var x2 = succ.IsMilestone ? succ.Right + 7 : succ.Right;
                var lead = Math.Max(x1, x2) + 8;
                return $"M{N(x1)} {N(y1)} H{N(lead)} V{N(y2)} H{N(x2 + 1)}";
            }
            default:
            {
                var x1 = pred.IsMilestone ? pred.Left + 7 : pred.Right;
                var x2 = succ.IsMilestone ? succ.Left - 7 : succ.Left;
                var p = $"M{N(x1)} {N(y1)} H{N(x1 + 6)}";
                p += x2 - 6 >= x1 + 6
                    ? $" V{N(y2)} H{N(x2 - 1)}"
                    : $" V{N(y1 + (y2 > y1 ? 17 : -17))} H{N(x2 - 8)} V{N(y2)} H{N(x2 - 1)}";
                return p;
            }
        }
    }

    public static string ConstraintText(string? type, DateTime? date)
    {
        if (type is null || date is null) return "";
        var d = FormatShort(date.Value);
        return type switch
        {
            "snet" => $"Start no earlier than {d}",
            "fnlt" => $"Finish no later than {d}",
            "mso" => $"Must start on {d}",
            _ => $"Date constraint {d}",
        };
    }
}
