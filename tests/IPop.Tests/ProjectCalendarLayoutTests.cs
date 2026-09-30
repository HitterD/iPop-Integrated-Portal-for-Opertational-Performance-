using IPop.Application.Projects;
using IPop.Web.Components;
using IPop.Web.Components.Projects;
using Xunit;

namespace IPop.Tests;

public class ProjectCalendarLayoutTests
{
    private static CalendarTaskDto Task(string title, DateTime? start, DateTime? due) =>
        new(Guid.NewGuid(), null, title, "todo", null, start, due, Guid.Empty, [], false, false);

    // Monday 2026-09-28 .. Sunday 2026-10-11: two full weeks.
    private static DateOnly[] TwoWeeks() =>
        Enumerable.Range(0, 14).Select(i => new DateOnly(2026, 9, 28).AddDays(i)).ToArray();

    [Fact]
    public void Overlapping_tasks_take_separate_lanes_and_disjoint_ones_share()
    {
        var a = Task("A", new DateTime(2026, 9, 28), new DateTime(2026, 9, 30));
        var b = Task("B", new DateTime(2026, 9, 29), new DateTime(2026, 9, 29));
        var c = Task("C", new DateTime(2026, 10, 1), new DateTime(2026, 10, 2));

        var week = ProjectCalendarLayout.Build(TwoWeeks()[..7], [a, b, c]).Single();

        var byTitle = week.Segments.ToDictionary(s => s.Task.Title);
        Assert.Equal(0, byTitle["A"].Lane);
        Assert.Equal(1, byTitle["B"].Lane);
        Assert.Equal(0, byTitle["C"].Lane); // A ended on Wednesday, C starts Thursday
        Assert.Equal((0, 3), (byTitle["A"].StartCol, byTitle["A"].Span));
        Assert.Equal((3, 2), (byTitle["C"].StartCol, byTitle["C"].Span));
    }

    [Fact]
    public void Span_crossing_a_week_is_split_with_continuation_flags()
    {
        var t = Task("Long", new DateTime(2026, 10, 2), new DateTime(2026, 10, 6));

        var rows = ProjectCalendarLayout.Build(TwoWeeks(), [t]);

        var first = rows[0].Segments.Single();
        var second = rows[1].Segments.Single();
        Assert.Equal((4, 3, false, true), (first.StartCol, first.Span, first.ContinuesBefore, first.ContinuesAfter));
        Assert.Equal((0, 2, true, false), (second.StartCol, second.Span, second.ContinuesBefore, second.ContinuesAfter));
    }

    [Fact]
    public void Lanes_beyond_the_limit_are_counted_per_day_as_hidden()
    {
        var day = new DateTime(2026, 9, 30);
        var tasks = Enumerable.Range(0, 5).Select(i => Task($"T{i}", day, day)).ToList();

        var week = ProjectCalendarLayout.Build(TwoWeeks()[..7], tasks, maxLanes: 3).Single();

        Assert.Equal(3, week.Segments.Count);
        Assert.Equal([0, 0, 2, 0, 0, 0, 0], week.Hidden);
    }

    [Fact]
    public void Single_dated_and_reversed_tasks_still_place_and_undated_are_skipped()
    {
        var dueOnly = Task("Due", null, new DateTime(2026, 9, 29));
        var reversed = Task("Rev", new DateTime(2026, 10, 1), new DateTime(2026, 9, 30));
        var undated = Task("None", null, null);

        var week = ProjectCalendarLayout.Build(TwoWeeks()[..7], [dueOnly, reversed, undated]).Single();

        Assert.DoesNotContain(week.Segments, s => s.Task.Title == "None");
        Assert.Equal((1, 1), week.Segments.Where(s => s.Task.Title == "Due").Select(s => (s.StartCol, s.Span)).Single());
        Assert.Equal((2, 2), week.Segments.Where(s => s.Task.Title == "Rev").Select(s => (s.StartCol, s.Span)).Single());
        Assert.Equal(["Due", "Rev"], ProjectCalendarLayout.TasksOn(new DateOnly(2026, 9, 29), [dueOnly, reversed, undated])
            .Concat(ProjectCalendarLayout.TasksOn(new DateOnly(2026, 9, 30), [dueOnly, reversed])).Select(t => t.Title));
    }

    [Fact]
    public void MoveTo_shifts_both_dates_by_the_due_delta()
    {
        var t = Task("Move", new DateTime(2026, 9, 28), new DateTime(2026, 9, 30));
        Assert.Equal((new DateTime(2026, 10, 3), new DateTime(2026, 10, 5)),
            ProjectCalendarLayout.MoveTo(t, new DateOnly(2026, 10, 5)));

        var startOnly = Task("Start", new DateTime(2026, 9, 28), null);
        Assert.Equal(((DateTime?)new DateTime(2026, 9, 26), (DateTime?)null),
            ProjectCalendarLayout.MoveTo(startOnly, new DateOnly(2026, 9, 26)));
    }

    [Fact]
    public void Month_grid_builds_six_weeks()
    {
        var rows = ProjectCalendarLayout.Build(DatePickerMath.MonthGrid(2026, 2), []);
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(DayOfWeek.Monday, r.Days[0].DayOfWeek));
    }
}
