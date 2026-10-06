using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

/// Headline numbers for the Reports tab (docs/mockups/ipop-apple/project-reports.html).
public sealed record ReportTiles(
    int Total, int Done, int Percent, int DoneThisWeek,
    int OnTimeCount, int DoneWithDue, int? OnTimePercent,
    int Overdue, int OldestLateDays,
    int DueThisWeek, int DueTomorrow, DateTime WeekStart,
    double? Pace, double PaceDelta);

/// Pure report maths over the project's tasks, computed in the browser. Weeks start on Monday
/// (local time). A task is "done" when its status is done; it completed at CompletedAt (falls
/// back to UpdatedAt for rows older than that column). Only top-level tasks count.
public static class ProjectReportMath
{
    public const int PaceWeeks = 6;

    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// Timestamps arrive as UTC; the week a task belongs to is the viewer's local week.
    public static DateTime Local(DateTime d) => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;

    public static DateTime? DoneAt(ProjectTaskDto t) =>
        t.Status == "done" ? Local(t.CompletedAt ?? t.UpdatedAt) : null;

    public static bool IsDone(ProjectTaskDto t) => t.Status == "done";

    /// Due dates are calendar dates (midnight), never shifted by time zone.
    public static DateTime? DueDay(ProjectTaskDto t) => t.DueDate?.Date;

    public static IReadOnlyList<ProjectTaskDto> TopLevel(IEnumerable<ProjectTaskDto> tasks) =>
        tasks.Where(t => t.ParentTaskId is null).ToList();

    /// On time = completed no later than the end of its due day.
    public static bool OnTime(ProjectTaskDto t) =>
        DoneAt(t) is { } at && DueDay(t) is { } due && at < due.AddDays(1);

    /// Open tasks at the end of each of the last `weeks` weeks (oldest first); the last point
    /// is "now". Open at W = created by W and not completed by W.
    public static int[] Burndown(IEnumerable<ProjectTaskDto> tasks, DateTime now, int weeks)
    {
        var list = tasks.ToList();
        var thisWeek = WeekStart(now);
        var result = new int[weeks];
        for (var i = 0; i < weeks; i++)
        {
            var end = i == weeks - 1 ? now : thisWeek.AddDays(-7 * (weeks - 1 - i) + 7);
            result[i] = list.Count(t => Local(t.CreatedAt) <= end && (DoneAt(t) is not { } at || at > end));
        }
        return result;
    }

    /// Tasks completed in each of the last `weeks` weeks (oldest first); the last is this week so far.
    public static int[] Velocity(IEnumerable<ProjectTaskDto> tasks, DateTime now, int weeks)
    {
        var thisWeek = WeekStart(now);
        var result = new int[weeks];
        foreach (var t in tasks)
        {
            if (DoneAt(t) is not { } at) continue;
            var back = (int)Math.Floor((thisWeek - WeekStart(at)).TotalDays / 7);
            if (back >= 0 && back < weeks) result[weeks - 1 - back]++;
        }
        return result;
    }

    /// Monday of each of the last `weeks` weeks plus `future` weeks ahead (oldest first).
    public static DateTime[] Weeks(DateTime now, int weeks, int future = 0)
    {
        var thisWeek = WeekStart(now);
        return Enumerable.Range(0, weeks + future).Select(i => thisWeek.AddDays(7 * (i - (weeks - 1)))).ToArray();
    }

    public static double Average(IReadOnlyList<int> v) => v.Count == 0 ? 0 : v.Average();

    public static ReportTiles Tiles(IEnumerable<ProjectTaskDto> all, DateTime now)
    {
        var tasks = TopLevel(all);
        var today = now.Date;
        var weekStart = WeekStart(now);
        var done = tasks.Where(IsDone).ToList();
        var open = tasks.Where(t => !IsDone(t)).ToList();
        var doneWithDue = done.Where(t => t.DueDate is not null).ToList();
        var onTime = doneWithDue.Count(OnTime);
        var late = open.Where(t => DueDay(t) is { } d && d < today).ToList();
        var oldest = late.Count == 0 ? 0 : (int)(today - late.Min(t => DueDay(t)!.Value)).TotalDays;
        var weekEnd = weekStart.AddDays(6);
        var dueWeek = open.Count(t => DueDay(t) is { } d && d >= today && d <= weekEnd);
        var dueTomorrow = open.Count(t => DueDay(t) is { } d && d == today.AddDays(1));
        var vel = Velocity(tasks, now, PaceWeeks * 2);
        var last = vel[PaceWeeks..];
        var prev = vel[..PaceWeeks];
        double? pace = done.Count == 0 ? null : Average(last);
        return new ReportTiles(
            tasks.Count, done.Count, tasks.Count == 0 ? 0 : (int)Math.Round(100.0 * done.Count / tasks.Count),
            done.Count(t => DoneAt(t) >= weekStart),
            onTime, doneWithDue.Count, doneWithDue.Count == 0 ? null : (int)Math.Round(100.0 * onTime / doneWithDue.Count),
            late.Count, oldest, dueWeek, dueTomorrow, weekStart,
            pace, Average(last) - Average(prev));
    }

    /// Axis maximum with a round step (mockup niceMax).
    public static int NiceMax(double v)
    {
        var step = v <= 10 ? 2 : v <= 25 ? 5 : 10;
        return Math.Max(step * 2, (int)Math.Ceiling(v / step) * step);
    }
}
