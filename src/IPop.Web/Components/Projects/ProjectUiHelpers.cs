using System;
using System.Collections.Generic;
using System.Linq;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

public static class ProjectUiHelpers
{
    private static readonly string[] AvatarColors =
    [
        "#2e7d5b", "#0d9488", "#0284c7", "#d97706",
        "#059669", "#dc2626", "#15803d", "#0891b2"
    ];

    public static string GetAvatarBg(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "#2e7d5b";
        var hash = (uint)name.GetHashCode();
        return AvatarColors[hash % (uint)AvatarColors.Length];
    }

    public static string GetInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "Budget (draft)" → "BD", not "B(".
        if (parts.Count(w => char.IsLetterOrDigit(w[0])) is > 0) parts = parts.Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpper();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpper();
    }

    /// v6 priority pill: lucide icon + title-cased label. Colors come from the `v6-pri.{key}` CSS class.
    public static (string Key, string Icon, string Label) GetPriorityPill(string? priority)
    {
        var key = (priority ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "urgent" => ("urgent", "flame", "Urgent"),
            "high" => ("high", "chevrons-up", "High"),
            "normal" or "medium" => ("normal", "chevron-up", key == "medium" ? "Medium" : "Normal"),
            "low" => ("low", "chevron-down", "Low"),
            "" => ("none", "minus", "Priority"),
            _ => ("normal", "chevron-up", char.ToUpperInvariant(key[0]) + key[1..]),
        };
    }

    /// v6 due state: "overdue" (red), "soon" (≤3 days, amber) or "" (neutral).
    public static string GetDueState(ProjectTaskDto t)
    {
        if (!t.DueDate.HasValue || t.Status == "done") return "";
        var days = (t.DueDate.Value.Date - DateTime.Today).TotalDays;
        return days < 0 ? "overdue" : days <= 3 ? "soon" : "";
    }

    public static bool IsAssignedToUser(ProjectTaskDto t, string? userNik)
    {
        if (t.Assignees.Count == 0 || string.IsNullOrEmpty(userNik)) return false;
        return t.Assignees.Any(a => a.Nik.Equals(userNik, StringComparison.OrdinalIgnoreCase));
    }

    public static DateTime StartOfWeek(DateTime dt)
    {
        int diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
        return dt.Date.AddDays(-1 * diff);
    }

    public static string GetTimelinePeriodLabel(string timelineScale, DateTime timelineAnchorDate)
    {
        if (timelineScale == "weekly")
        {
            var start = StartOfWeek(timelineAnchorDate);
            var end = start.AddDays(6);
            return $"{start:dd MMM} – {end:dd MMM yyyy}";
        }
        if (timelineScale == "yearly")
        {
            return $"Year {timelineAnchorDate.Year}";
        }
        return timelineAnchorDate.ToString("MMMM yyyy");
    }

    public static (DateTime start, DateTime end) GetTimelinePeriodBounds(string timelineScale, DateTime timelineAnchorDate)
    {
        if (timelineScale == "weekly")
        {
            var s = StartOfWeek(timelineAnchorDate);
            return (s, s.AddDays(7));
        }
        if (timelineScale == "yearly")
        {
            var s = new DateTime(timelineAnchorDate.Year, 1, 1);
            return (s, s.AddYears(1));
        }
        else
        {
            var s = new DateTime(timelineAnchorDate.Year, timelineAnchorDate.Month, 1);
            var daysInMonth = DateTime.DaysInMonth(timelineAnchorDate.Year, timelineAnchorDate.Month);
            return (s, s.AddDays(daysInMonth));
        }
    }

    public record GanttColItem(string Title, string Sub, bool IsToday, DateTime Start, DateTime End);

    public static List<GanttColItem> GetGanttColumns(string timelineScale, DateTime timelineAnchorDate)
    {
        var list = new List<GanttColItem>();
        if (timelineScale == "weekly")
        {
            var start = StartOfWeek(timelineAnchorDate);
            for (var i = 0; i < 7; i++)
            {
                var d = start.AddDays(i);
                list.Add(new GanttColItem(d.ToString("ddd"), d.ToString("dd MMM"), d.Date == DateTime.Today, d, d.AddDays(1)));
            }
        }
        else if (timelineScale == "yearly")
        {
            for (var m = 1; m <= 12; m++)
            {
                var d = new DateTime(timelineAnchorDate.Year, m, 1);
                var isCur = DateTime.Today.Year == timelineAnchorDate.Year && DateTime.Today.Month == m;
                list.Add(new GanttColItem(d.ToString("MMM"), $"Q{(m - 1) / 3 + 1}", isCur, d, d.AddMonths(1)));
            }
        }
        else
        {
            var year = timelineAnchorDate.Year;
            var month = timelineAnchorDate.Month;
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var curDay = DateTime.Today.Day;
            var isCurMonth = DateTime.Today.Year == year && DateTime.Today.Month == month;

            var first = new DateTime(year, month, 1);
            for (var startDay = 1; startDay <= daysInMonth; startDay += 7)
            {
                var endDay = Math.Min(daysInMonth, startDay + 6);
                var isToday = isCurMonth && curDay >= startDay && curDay <= endDay;
                list.Add(new GanttColItem($"W{(startDay - 1) / 7 + 1}", $"{startDay}–{endDay} {timelineAnchorDate:MMM}",
                    isToday, first.AddDays(startDay - 1), first.AddDays(endDay)));
            }
        }
        return list;
    }

    /// Period end is exclusive. Dates on tasks are calendar days, with due dates included.
    public record GanttPlacement(double Left, double Width, string Label, string Tooltip,
        string? OutsideNote, bool NeedsDates, bool IsInferred, bool IsInvalid);

    public static GanttPlacement CalculateGanttPlacement(TimelineTaskDto task, DateTime periodStart, DateTime periodEnd)
    {
        if (!task.DueDate.HasValue || (task.StartDate.HasValue && !task.DueDate.HasValue))
            return new(0, 0, "Set dates", "Set start and due dates", null, true, false, false);

        var due = task.DueDate.Value.Date;
        var inferred = !task.StartDate.HasValue;
        var start = inferred ? due.AddDays(-Math.Min(2, (due - DateTime.MinValue.Date).Days)) : task.StartDate!.Value.Date;
        var invalid = due < start;
        if (invalid) start = due; // Show a one-day marker without changing persisted values.

        var tooltip = invalid
            ? $"Invalid date range: saved start {task.StartDate:dd MMM yyyy} is after due {due:dd MMM yyyy}. Open task to correct dates."
            : inferred
                ? $"Due {due:dd MMM yyyy}. Bar starts two days earlier for display only; no start date is saved."
                : $"Start {start:dd MMM yyyy}; due {due:dd MMM yyyy} (inclusive).";
        var label = inferred ? $"Due {due:dd MMM}" : invalid ? $"Due {due:dd MMM} · check dates" : $"{start:dd MMM} – {due:dd MMM}";
        var pStart = periodStart.Date;
        var pEnd = periodEnd.Date;
        if (pEnd <= pStart) return new(0, 0, label, tooltip, null, false, inferred, invalid);
        if (due < pStart)
            return new(0, 0, label, tooltip, $"← Due {due:dd MMM yyyy}", false, inferred, invalid);
        if (start >= pEnd)
            return new(0, 0, label, tooltip, $"Starts {start:dd MMM yyyy} →", false, inferred, invalid);

        var totalDays = (pEnd - pStart).TotalDays;
        var clippedStart = start < pStart ? pStart : start;
        // Avoid DateTime overflow at MaxValue by computing the exclusive endpoint as a day offset.
        var clippedEndDays = Math.Min((due - pStart).TotalDays + 1, totalDays);
        var left = (clippedStart - pStart).TotalDays / totalDays * 100;
        var width = Math.Max(0, (clippedEndDays - (clippedStart - pStart).TotalDays) / totalDays * 100);
        return new(left, width, label, tooltip, null, false, inferred, invalid);
    }

    public static DateTime NavigateTimeline(string scale, DateTime anchor, int direction) => scale switch
    {
        "weekly" => anchor.AddDays(direction * 7),
        "yearly" => anchor.AddYears(direction),
        _ => anchor.AddMonths(direction)
    };

    public static double? GetTodayPosition(DateTime periodStart, DateTime periodEnd, DateTime today)
    {
        var date = today.Date;
        return date < periodStart.Date || date >= periodEnd.Date || periodEnd <= periodStart
            ? null
            : (date - periodStart.Date).TotalDays / (periodEnd.Date - periodStart.Date).TotalDays * 100;
    }

    public static string StatusLabel(string? status) => status switch
    {
        "todo" => "To Do",
        "doing" => "In Progress",
        "review" => "Review",
        "done" => "Done",
        _ => status ?? ""
    };
}

/// Month/week calendar placement: each task spans StartDate..DueDate (either alone is a one-day
/// bar); spans are cut per week row and packed into lanes, and lanes past MaxLanes are counted
/// per day as "+N more".
public static class ProjectCalendarLayout
{
    public sealed record Segment(CalendarTaskDto Task, int Lane, int StartCol, int Span, bool ContinuesBefore, bool ContinuesAfter);

    public sealed record WeekRow(DateOnly[] Days, IReadOnlyList<Segment> Segments, int[] Hidden);

    public static (DateOnly Start, DateOnly End)? SpanOf(CalendarTaskDto t)
    {
        var s = t.StartDate ?? t.DueDate;
        var e = t.DueDate ?? t.StartDate;
        if (s is null || e is null) return null;
        var a = DateOnly.FromDateTime(s.Value);
        var b = DateOnly.FromDateTime(e.Value);
        return a <= b ? (a, b) : (b, a);
    }

    /// <paramref name="days"/> is a whole number of Monday-first weeks (a month grid or one week).
    public static List<WeekRow> Build(IReadOnlyList<DateOnly> days, IEnumerable<CalendarTaskDto> tasks, int maxLanes = 3)
    {
        var spans = tasks.Select(t => (Task: t, Span: SpanOf(t))).Where(x => x.Span is not null)
            .Select(x => (x.Task, Start: x.Span!.Value.Start, End: x.Span.Value.End)).ToList();
        var rows = new List<WeekRow>();
        for (var w = 0; w + 7 <= days.Count; w += 7)
        {
            var week = days.Skip(w).Take(7).ToArray();
            var ws = week[0];
            var we = week[6];
            var hidden = new int[7];
            var laneEnds = new List<int>(); // last occupied column per lane
            var segments = new List<Segment>();
            var inWeek = spans.Where(x => x.Start <= we && x.End >= ws)
                .OrderBy(x => x.Start < ws ? ws : x.Start)
                .ThenByDescending(x => x.End.DayNumber - x.Start.DayNumber)
                .ThenBy(x => x.Task.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var x in inWeek)
            {
                var from = x.Start < ws ? ws : x.Start;
                var to = x.End > we ? we : x.End;
                var col = from.DayNumber - ws.DayNumber;
                var span = to.DayNumber - from.DayNumber + 1;
                var lane = laneEnds.FindIndex(end => end < col);
                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(-1); }
                laneEnds[lane] = col + span - 1;
                if (lane >= maxLanes)
                {
                    for (var c = col; c < col + span; c++) hidden[c]++;
                    continue;
                }
                segments.Add(new Segment(x.Task, lane, col, span, x.Start < ws, x.End > we));
            }
            rows.Add(new WeekRow(week, segments, hidden));
        }
        return rows;
    }

    /// Tasks touching one day, for the agenda list and the "+N more" popover.
    public static List<CalendarTaskDto> TasksOn(DateOnly day, IEnumerable<CalendarTaskDto> tasks) =>
        tasks.Where(t => SpanOf(t) is { } s && s.Start <= day && s.End >= day)
            .OrderBy(t => t.Status == "done").ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();

    /// Moving a bar to <paramref name="target"/>: the due date lands there (or the start, for a
    /// start-only task) and the other end shifts by the same number of days.
    public static (DateTime? Start, DateTime? Due) MoveTo(CalendarTaskDto t, DateOnly target)
    {
        var anchor = t.DueDate ?? t.StartDate;
        if (anchor is null) return (t.StartDate, t.DueDate);
        var delta = target.DayNumber - DateOnly.FromDateTime(anchor.Value).DayNumber;
        return (t.StartDate?.Date.AddDays(delta), t.DueDate?.Date.AddDays(delta));
    }
}

/// Phase 3 time tracking: durations and timesheet weeks.
public static class ProjectTimeFormat
{
    /// Running timer clock: "4:07" under an hour, "1:04:07" after.
    public static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var h = (int)span.TotalHours;
        return h > 0 ? $"{h}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// Logged minutes as "1h 30m", "45m", "2h"; zero is "0m".
    public static string Minutes(int minutes)
    {
        if (minutes <= 0) return "0m";
        var h = minutes / 60;
        var m = minutes % 60;
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// Minutes for a manual entry typed as hours + minutes; null when out of range (1 min .. 24 h).
    public static int? ToMinutes(int? hours, int? minutes)
    {
        var h = hours ?? 0;
        var m = minutes ?? 0;
        if (h < 0 || m < 0 || m > 59) return null;
        var total = h * 60 + m;
        return total is >= 1 and <= 24 * 60 ? total : null;
    }

    /// Monday 00:00 of the week containing <paramref name="localDay"/>, in local time.
    public static DateTime WeekStartLocal(DateTime localDay) =>
        DateTime.SpecifyKind(ProjectUiHelpers.StartOfWeek(localDay), DateTimeKind.Local);

    /// Query value for the timesheet endpoints: the local Monday as a UTC instant, ISO with Z.
    public static string WeekStartQuery(DateTime weekStartLocal, TimeZoneInfo? zone = null)
    {
        var local = DateTime.SpecifyKind(weekStartLocal, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone ?? TimeZoneInfo.Local);
        return Uri.EscapeDataString(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// "22–28 Sep 2026" or "29 Sep – 5 Oct 2026".
    public static string WeekLabel(DateTime weekStartLocal)
    {
        var mon = IPop.Web.Components.DatePickerMath.MonthShort;
        var end = weekStartLocal.AddDays(6);
        return weekStartLocal.Month == end.Month
            ? $"{weekStartLocal.Day}–{end.Day} {mon[end.Month - 1]} {end.Year}"
            : $"{weekStartLocal.Day} {mon[weekStartLocal.Month - 1]} – {end.Day} {mon[end.Month - 1]} {end.Year}";
    }
}
