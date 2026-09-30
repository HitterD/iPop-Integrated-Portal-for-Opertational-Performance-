using IPop.Application.Projects;
using IPop.Web.Components.Projects;
using Xunit;

namespace IPop.Tests;

public class ProjectTimelineUiHelpersTests
{
    private static TimelineTaskDto Task(DateTime? start, DateTime? due, string status = "review") =>
        new(Guid.NewGuid(), "Example", null, status, null, start, due, null, null, [], 0, 0, null);

    [Fact]
    public void Scale_bounds_and_navigation_respect_calendar_rollovers()
    {
        var february = new DateTime(2024, 2, 20);
        Assert.Equal((new DateTime(2024, 2, 1), new DateTime(2024, 3, 1)),
            ProjectUiHelpers.GetTimelinePeriodBounds("monthly", february));
        Assert.Equal(new DateTime(2025, 2, 20), ProjectUiHelpers.NavigateTimeline("yearly", february, 1));
        Assert.Equal(new DateTime(2025, 1, 20), ProjectUiHelpers.NavigateTimeline("monthly", new DateTime(2024, 12, 20), 1));
        Assert.Equal(new DateTime(2025, 1, 2), ProjectUiHelpers.NavigateTimeline("weekly", new DateTime(2024, 12, 26), 1));
        Assert.Equal((new DateTime(2024, 12, 30), new DateTime(2025, 1, 6)),
            ProjectUiHelpers.GetTimelinePeriodBounds("weekly", new DateTime(2025, 1, 1)));
    }

    [Fact]
    public void Fully_dated_bar_includes_due_day_and_clips_to_period()
    {
        var placement = ProjectUiHelpers.CalculateGanttPlacement(
            Task(new DateTime(2024, 1, 30), new DateTime(2024, 2, 2)),
            new DateTime(2024, 2, 1), new DateTime(2024, 2, 11));
        Assert.Equal(0, placement.Left);
        Assert.Equal(20, placement.Width);
        Assert.Null(placement.OutsideNote);
        Assert.Contains("inclusive", placement.Tooltip);
    }

    [Fact]
    public void Due_only_bar_is_three_inclusive_days_without_claiming_saved_start()
    {
        var placement = ProjectUiHelpers.CalculateGanttPlacement(
            Task(null, new DateTime(2024, 2, 5)),
            new DateTime(2024, 2, 1), new DateTime(2024, 2, 11));
        Assert.Equal(20, placement.Left);
        Assert.Equal(30, placement.Width);
        Assert.True(placement.IsInferred);
        Assert.Equal("Due 05 Feb", placement.Label);
        Assert.Contains("no start date is saved", placement.Tooltip);
    }

    [Fact]
    public void Start_only_and_undated_tasks_require_dates()
    {
        var periodStart = new DateTime(2024, 2, 1);
        var periodEnd = new DateTime(2024, 3, 1);
        foreach (var task in new[] { Task(periodStart, null), Task(null, null) })
        {
            var placement = ProjectUiHelpers.CalculateGanttPlacement(task, periodStart, periodEnd);
            Assert.True(placement.NeedsDates);
            Assert.Equal(0, placement.Width);
            Assert.Equal("Set dates", placement.Label);
        }
    }

    [Fact]
    public void Offscreen_and_invalid_ranges_do_not_produce_overflow_or_negative_width()
    {
        var from = new DateTime(2024, 2, 1);
        var to = new DateTime(2024, 3, 1);
        var before = ProjectUiHelpers.CalculateGanttPlacement(Task(new DateTime(2024, 1, 1), new DateTime(2024, 1, 3)), from, to);
        var after = ProjectUiHelpers.CalculateGanttPlacement(Task(new DateTime(2024, 3, 2), new DateTime(2024, 3, 5)), from, to);
        var invalid = ProjectUiHelpers.CalculateGanttPlacement(Task(new DateTime(2024, 2, 9), new DateTime(2024, 2, 4)), from, to);
        Assert.StartsWith("←", before.OutsideNote);
        Assert.EndsWith("→", after.OutsideNote);
        Assert.Equal(0, before.Width);
        Assert.Equal(0, after.Width);
        Assert.True(invalid.IsInvalid);
        Assert.True(invalid.Width > 0);
        Assert.InRange(invalid.Left + invalid.Width, 0, 100);
    }

    [Fact]
    public void Today_marker_tracks_actual_day_and_hides_outside_period()
    {
        var from = new DateTime(2024, 2, 1);
        var to = new DateTime(2024, 2, 11);
        Assert.Equal(40, ProjectUiHelpers.GetTodayPosition(from, to, new DateTime(2024, 2, 5)));
        Assert.Null(ProjectUiHelpers.GetTodayPosition(from, to, to));
    }
}
