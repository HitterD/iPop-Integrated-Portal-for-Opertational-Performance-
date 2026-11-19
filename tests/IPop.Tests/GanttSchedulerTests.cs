using IPop.Infrastructure.Projects;
using Xunit;

namespace IPop.Tests;

public sealed class GanttSchedulerTests
{
    // 2026-11-02 is a Monday.
    private static DateTime D(int day) => new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), E = Guid.NewGuid();

    private static SchedPosition Pos(IEnumerable<SchedPosition> ps, Guid id) => ps.Single(p => p.TaskId == id);

    [Fact]
    public void FinishToStart_pushes_successor_to_next_working_day_after_predecessor()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)), new SchedTask(B, D(2), D(3)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B)]);
        Assert.Equal((D(4), D(5)), (Pos(p, B).Start, Pos(p, B).Due));
    }

    [Fact]
    public void FinishToStart_lag_and_lead_are_in_working_days()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)), new SchedTask(B, D(2), D(2)) };
        Assert.Equal(D(5), Pos(GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B, "FS", 1)]), B).Start);
        Assert.Equal(D(3), Pos(GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B, "FS", -1)]), B).Start);
    }

    [Fact]
    public void Weekends_are_skipped_and_duration_is_preserved_in_working_days()
    {
        // A Thu-Fri (5-6 Nov would be Thu/Fri: Nov 5 = Thu). B has 2 working days.
        var tasks = new[] { new SchedTask(A, D(5), D(6)), new SchedTask(B, D(5), D(6)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B)]);
        Assert.Equal((D(9), D(10)), (Pos(p, B).Start, Pos(p, B).Due)); // Mon-Tue
    }

    [Fact]
    public void Holidays_are_not_working_days()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(2)), new SchedTask(B, D(2), D(2)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B)], [D(3)]);
        Assert.Equal(D(4), Pos(p, B).Start);
    }

    [Fact]
    public void StartToStart_with_lag()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(6)), new SchedTask(B, D(2), D(3)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B, "SS", 2)]);
        Assert.Equal((D(4), D(5)), (Pos(p, B).Start, Pos(p, B).Due));
    }

    [Fact]
    public void FinishToFinish_aligns_finish_days()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(4)), new SchedTask(B, D(2), D(3)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B, "FF", 0)]);
        Assert.Equal((D(3), D(4)), (Pos(p, B).Start, Pos(p, B).Due));
    }

    [Fact]
    public void Single_mode_moves_only_the_task_and_reports_conflicts()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)), new SchedTask(B, D(4), D(5)) };
        var r = GanttScheduler.PreviewReschedule(tasks, [new SchedLink(A, B)], null, A, D(3), "single");
        var ch = Assert.Single(r.Changes);
        Assert.Equal((A, D(3), D(4)), (ch.TaskId, ch.NewStart, ch.NewDue));
        Assert.Equal(new SchedConflict(A, B), Assert.Single(r.Conflicts));
    }

    [Fact]
    public void Cascade_pushes_successors_transitively_just_enough()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)), new SchedTask(B, D(4), D(5)), new SchedTask(C, D(6), D(6)) };
        var links = new[] { new SchedLink(A, B), new SchedLink(B, C) };
        var r = GanttScheduler.PreviewReschedule(tasks, links, null, A, D(3), "cascade");
        Assert.Equal(3, r.Changes.Count);
        Assert.Equal((D(5), D(6)), (r.Changes.Single(x => x.TaskId == B).NewStart, r.Changes.Single(x => x.TaskId == B).NewDue));
        Assert.Equal(D(9), r.Changes.Single(x => x.TaskId == C).NewStart); // Fri 6 taken by B, so Mon 9
        Assert.Empty(r.Conflicts);
        Assert.Equal(D(9), r.NewFinish);
        Assert.Equal(D(6), r.OldFinish);
    }

    [Fact]
    public void Cascade_never_pulls_successors_earlier_and_leaves_slack_alone()
    {
        var tasks = new[] { new SchedTask(A, D(4), D(5)), new SchedTask(B, D(9), D(10)) };
        var r = GanttScheduler.PreviewReschedule(tasks, [new SchedLink(A, B)], null, A, D(2), "cascade");
        Assert.Equal(A, Assert.Single(r.Changes).TaskId);
        Assert.Empty(r.Conflicts);
    }

    [Fact]
    public void Resize_with_new_due_changes_duration()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)), new SchedTask(B, D(4), D(5)) };
        var r = GanttScheduler.PreviewReschedule(tasks, [new SchedLink(A, B)], null, A, D(2), "cascade", D(5));
        Assert.Equal((D(2), D(5)), (r.Changes.Single(x => x.TaskId == A).NewStart, r.Changes.Single(x => x.TaskId == A).NewDue));
        Assert.Equal(D(6), r.Changes.Single(x => x.TaskId == B).NewStart);
    }

    [Fact]
    public void Moving_to_a_weekend_lands_on_the_next_working_day()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(3)) };
        var r = GanttScheduler.PreviewReschedule(tasks, [], null, A, D(7), "single"); // Saturday
        Assert.Equal((D(9), D(10)), (r.Changes[0].NewStart, r.Changes[0].NewDue));
    }

    [Fact]
    public void Constraints_snet_raises_mso_fixes_fnlt_only_flags()
    {
        var tasks = new[]
        {
            new SchedTask(A, D(2), D(2)),
            new SchedTask(B, D(2), D(2), "snet", D(5)),
            new SchedTask(C, D(2), D(3), "mso", D(4)),
            new SchedTask(E, D(5), D(6), "fnlt", D(5)),
        };
        var p = GanttScheduler.EarliestSchedule(tasks, []);
        Assert.Equal(D(5), Pos(p, B).Start);
        Assert.Equal((D(4), D(5)), (Pos(p, C).Start, Pos(p, C).Due));
        Assert.Equal((D(5), D(6)), (Pos(p, E).Start, Pos(p, E).Due)); // unchanged
        var a = GanttScheduler.Analyze(tasks, []);
        Assert.Contains(E, a.ConstraintViolations);   // finishes Fri 6, allowed Thu 5
        Assert.Contains(B, a.ConstraintViolations);   // planned Mon, SNET Thu
        Assert.Contains(C, a.ConstraintViolations);   // planned Mon, must start Wed
        Assert.DoesNotContain(A, a.ConstraintViolations);
    }

    [Fact]
    public void Dragging_a_must_start_on_task_is_a_constraint_violation()
    {
        var tasks = new[] { new SchedTask(A, D(4), D(5), "mso", D(4)) };
        var r = GanttScheduler.PreviewReschedule(tasks, [], null, A, D(5), "single");
        Assert.Equal("constraint_violation", r.Error);
    }

    [Fact]
    public void Snet_holds_a_cascaded_successor_no_earlier_than_its_date()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(2)), new SchedTask(B, D(3), D(3), "snet", D(3)) };
        var p = GanttScheduler.EarliestSchedule(tasks, [new SchedLink(A, B)]);
        Assert.Equal(D(3), Pos(p, B).Start);
    }

    [Fact]
    public void Diamond_graph_slack_and_critical_path()
    {
        // S(1d) -> X(3d) -> F(1d)  and  S -> Y(1d) -> F. X path is critical, Y has 2 days of slack.
        var s = Guid.NewGuid(); var x = Guid.NewGuid(); var y = Guid.NewGuid(); var f = Guid.NewGuid();
        var tasks = new[]
        {
            new SchedTask(s, D(2), D(2)), new SchedTask(x, D(3), D(5)),
            new SchedTask(y, D(3), D(3)), new SchedTask(f, D(6), D(6)),
        };
        var links = new[] { new SchedLink(s, x), new SchedLink(s, y), new SchedLink(x, f), new SchedLink(y, f) };
        var a = GanttScheduler.Analyze(tasks, links);
        Assert.Equal(new[] { s, x, f }, a.CriticalPath);
        Assert.Equal(0, a.Floats[x].SlackDays);
        Assert.Equal(2, a.Floats[y].SlackDays);
        Assert.False(a.Floats[y].IsCritical);
        Assert.Equal(D(6), a.ProjectFinish);
        Assert.Empty(a.Conflicts);
    }

    [Fact]
    public void Slack_counts_working_days_not_weekend_days()
    {
        // Y ends Mon 2; F is Fri 6 and waits for Y (FS): float = Tue..Thu = 3 working days.
        var tasks = new[] { new SchedTask(A, D(2), D(2)), new SchedTask(B, D(6), D(6)), new SchedTask(C, D(2), D(6)) };
        var a = GanttScheduler.Analyze(tasks, [new SchedLink(A, B)]);
        Assert.Equal(3, a.Floats[A].SlackDays);
        Assert.True(a.Floats[C].IsCritical);
    }

    [Fact]
    public void Conflicts_list_links_the_plan_breaks()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(4)), new SchedTask(B, D(3), D(5)), new SchedTask(C, D(9), D(9)) };
        var c = GanttScheduler.DetectConflicts(tasks, [new SchedLink(A, B), new SchedLink(B, C)]);
        Assert.Equal(new SchedConflict(A, B), Assert.Single(c));
    }

    [Fact]
    public void Undated_tasks_are_unscheduled_and_a_single_date_lasts_one_day()
    {
        var tasks = new[] { new SchedTask(A, null, null), new SchedTask(B, D(4), null), new SchedTask(C, null, D(5)) };
        var a = GanttScheduler.Analyze(tasks, [new SchedLink(A, B)]);
        Assert.DoesNotContain(A, a.Floats.Keys);
        Assert.Equal(1, a.Floats[B].DurationDays);
        Assert.Equal(1, a.Floats[C].DurationDays);
    }

    [Fact]
    public void A_link_cycle_does_not_hang_or_throw()
    {
        var tasks = new[] { new SchedTask(A, D(2), D(2)), new SchedTask(B, D(3), D(3)) };
        var a = GanttScheduler.Analyze(tasks, [new SchedLink(A, B), new SchedLink(B, A)]);
        Assert.Equal(2, a.Floats.Count);
    }
}
