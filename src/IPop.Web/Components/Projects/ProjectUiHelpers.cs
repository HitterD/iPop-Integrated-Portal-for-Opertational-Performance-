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
        int hash = Math.Abs(name.GetHashCode());
        return AvatarColors[hash % AvatarColors.Length];
    }

    public static string GetInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpper();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpper();
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

    public record GanttColItem(string Title, string Sub, bool IsToday);

    public static List<GanttColItem> GetGanttColumns(string timelineScale, DateTime timelineAnchorDate)
    {
        var list = new List<GanttColItem>();
        if (timelineScale == "weekly")
        {
            var start = StartOfWeek(timelineAnchorDate);
            for (var i = 0; i < 7; i++)
            {
                var d = start.AddDays(i);
                list.Add(new GanttColItem(d.ToString("ddd"), d.ToString("dd MMM"), d.Date == DateTime.Today));
            }
        }
        else if (timelineScale == "yearly")
        {
            for (var m = 1; m <= 12; m++)
            {
                var d = new DateTime(timelineAnchorDate.Year, m, 1);
                var isCur = DateTime.Today.Year == timelineAnchorDate.Year && DateTime.Today.Month == m;
                list.Add(new GanttColItem(d.ToString("MMM"), $"Q{(m - 1) / 3 + 1}", isCur));
            }
        }
        else
        {
            var year = timelineAnchorDate.Year;
            var month = timelineAnchorDate.Month;
            var daysInMonth = DateTime.DaysInMonth(year, month);
            var curDay = DateTime.Today.Day;
            var isCurMonth = DateTime.Today.Year == year && DateTime.Today.Month == month;

            int step = Math.Max(1, daysInMonth / 4);
            for (int i = 0; i < 4; i++)
            {
                int startDay = (i * step) + 1;
                int endDay = (i == 3) ? daysInMonth : Math.Min(daysInMonth, (i + 1) * step);
                bool isTod = isCurMonth && curDay >= startDay && curDay <= endDay;
                list.Add(new GanttColItem($"W{i + 1}", $"{startDay}-{endDay} {timelineAnchorDate:MMM}", isTod));
            }
        }
        return list;
    }

    public static (double left, double width, bool hasDates, string dateLabel) CalculateGanttBar(TimelineTaskDto t, DateTime pStart, DateTime pEnd)
    {
        var hasDates = t.StartDate.HasValue || t.DueDate.HasValue;
        var tStart = t.StartDate ?? t.DueDate?.AddDays(-3) ?? DateTime.Today;
        var tEnd = t.DueDate ?? t.StartDate?.AddDays(3) ?? tStart.AddDays(3);
        if (tEnd <= tStart) tEnd = tStart.AddDays(1);

        var totalSeconds = (pEnd - pStart).TotalSeconds;
        if (totalSeconds <= 0) return (0, 100, hasDates, "");

        var clampStart = tStart < pStart ? pStart : (tStart > pEnd ? pEnd : tStart);
        var clampEnd = tEnd > pEnd ? pEnd : (tEnd < pStart ? pStart : tEnd);

        var left = Math.Max(0.0, ((clampStart - pStart).TotalSeconds / totalSeconds) * 100);
        var width = Math.Max(10.0, ((clampEnd - clampStart).TotalSeconds / totalSeconds) * 100);

        if (left + width > 100)
        {
            left = Math.Max(0.0, 100 - width);
        }

        var label = $"{tStart:dd MMM} – {tEnd:dd MMM}";
        return (left, width, hasDates, label);
    }
}
