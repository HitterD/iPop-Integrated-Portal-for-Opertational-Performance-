using IPop.Application.Projects;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Phase 2: table-backed dependencies, recurring completion, soft delete + undo, bulk actions,
/// saved views and the calendar feed.
public sealed class ProjectPhase2ServiceTests
{
    private static async Task<(TestDb T, ProjectService Svc, Guid Owner, Guid ProjectId)> SetupAsync(string name = "P2")
    {
        var t = new TestDb();
        var owner = t.AddUser(name + "_O");
        var svc = new ProjectService(t.Verify());
        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest(name, null, 2026, owner.Id, null, null, null, null), default)).Project!;
        return (t, svc, owner.Id, p.Id);
    }

    private static async Task<ProjectTaskDto> TaskAsync(ProjectService svc, Guid actor, Guid projectId, string title,
        DateTime? due = null, DateTime? start = null, Guid? parent = null, string status = "todo") =>
        (await svc.CreateTaskAsync(actor, new CreateTaskRequest(projectId, parent, title, null, status, null, start, due, null, null, null), default)).Task!;

    private static UpdateTaskRequest Update(string? status = null, bool? recurring = null, string? rule = null, string? description = null) =>
        new(null, description, status, null, null, null, null, null, null, null, recurring, rule, null, null);

    // ── Dependencies ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Dependencies_are_set_replaced_and_reported_as_blocked()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var a = await TaskAsync(svc, owner, p, "A");
        var b = await TaskAsync(svc, owner, p, "B");
        var c = await TaskAsync(svc, owner, p, "C");

        var set = await svc.SetTaskDependenciesAsync(owner, c.Id, new([a.Id, b.Id]), default);
        Assert.True(set.Success);
        Assert.True(set.Task!.IsBlocked);
        Assert.Equal(["A", "B"], set.Task.DependsOn!.Select(d => d.Title));

        var replaced = await svc.SetTaskDependenciesAsync(owner, c.Id, new([b.Id]), default);
        Assert.Equal(b.Id, Assert.Single(replaced.Task!.DependsOn!).Id);

        await svc.MoveTaskAsync(owner, b.Id, new TaskMoveRequest("done", null), default);
        var list = await svc.GetTasksAsync(p, null, default);
        Assert.False(list.Single(x => x.Id == c.Id).IsBlocked);
        Assert.Equal(2, await t.Verify().ProjectTaskActivities.CountAsync(x => x.TaskId == c.Id && x.Action == "dependencies_changed"));
    }

    [Fact]
    public async Task Dependencies_reject_self_parent_cross_project_cycles_and_outsiders()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var other = (await svc.CreateProjectAsync(owner, new CreateProjectRequest("Other", null, 2026, owner, null, null, null, null), default)).Project!;
        var a = await TaskAsync(svc, owner, p, "A");
        var b = await TaskAsync(svc, owner, p, "B");
        var c = await TaskAsync(svc, owner, p, "C");
        var sub = await TaskAsync(svc, owner, p, "Sub", parent: a.Id);
        var foreign = await TaskAsync(svc, owner, other.Id, "X");
        var outsider = t.AddUser("OUT");

        Assert.Equal("invalid_dependency", (await svc.SetTaskDependenciesAsync(owner, a.Id, new([a.Id]), default)).Error);
        Assert.Equal("invalid_dependency", (await svc.SetTaskDependenciesAsync(owner, sub.Id, new([a.Id]), default)).Error);
        Assert.Equal("invalid_dependency", (await svc.SetTaskDependenciesAsync(owner, a.Id, new([foreign.Id]), default)).Error);
        Assert.Equal("forbidden", (await svc.SetTaskDependenciesAsync(outsider.Id, a.Id, new([b.Id]), default)).Error);

        Assert.True((await svc.SetTaskDependenciesAsync(owner, b.Id, new([a.Id]), default)).Success);
        Assert.True((await svc.SetTaskDependenciesAsync(owner, c.Id, new([b.Id]), default)).Success);
        Assert.Equal("dependency_cycle", (await svc.SetTaskDependenciesAsync(owner, a.Id, new([c.Id]), default)).Error);
    }

    [Fact]
    public async Task Done_guard_applies_to_put_as_well_as_move()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var a = await TaskAsync(svc, owner, p, "A");
        var b = await TaskAsync(svc, owner, p, "B");
        await svc.SetTaskDependenciesAsync(owner, b.Id, new([a.Id]), default);

        Assert.Equal("unmet_dependencies", (await svc.UpdateTaskAsync(owner, b.Id, Update("done"), default)).Error);
        Assert.Equal("unmet_dependencies", (await svc.MoveTaskAsync(owner, b.Id, new TaskMoveRequest("done", null), default)).Error);
        Assert.True((await svc.UpdateTaskAsync(owner, b.Id, Update("doing"), default)).Success);

        // A deleted prerequisite no longer blocks.
        Assert.True((await svc.DeleteTaskAsync(owner, a.Id, default)).Success);
        Assert.True((await svc.UpdateTaskAsync(owner, b.Id, Update("done"), default)).Success);
    }

    // ── Recurring ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FREQ=DAILY", "2026-10-01", "2026-10-02")]
    [InlineData("FREQ=DAILY;INTERVAL=3", "2026-10-01", "2026-10-04")]
    [InlineData("FREQ=WEEKLY", "2026-10-01", "2026-10-08")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", "2026-10-01", "2026-10-15")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,FR", "2026-10-01", "2026-10-02")] // Thu → Fri same week
    [InlineData("FREQ=WEEKLY;BYDAY=MO,FR", "2026-10-02", "2026-10-05")] // Fri → next Mon
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", "2026-10-02", "2026-10-12")]
    [InlineData("FREQ=MONTHLY", "2026-01-31", "2026-02-28")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=31", "2026-02-10", "2026-02-28")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=5", "2026-10-10", "2026-11-05")]
    [InlineData("FREQ=MONTHLY;INTERVAL=3;BYMONTHDAY=15", "2026-10-01", "2027-01-15")]
    public void Next_occurrence_follows_the_rule(string rule, string from, string expected)
    {
        var r = TaskRecurrence.Parse(rule)!;
        Assert.Equal(DateTime.Parse(expected), r.Next(DateTime.Parse(from)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FREQ=YEARLY")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=DAILY;INTERVAL=100")]
    [InlineData("FREQ=DAILY;BYDAY=MO")]
    [InlineData("FREQ=WEEKLY;BYMONTHDAY=3")]
    [InlineData("FREQ=WEEKLY;BYDAY=XX")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=DAILY;COUNT=3")]
    public void Invalid_rules_are_rejected(string? rule) => Assert.Null(TaskRecurrence.Parse(rule));

    [Fact]
    public void Rules_are_canonicalised() =>
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO,FR", TaskRecurrence.Parse(" freq=weekly; byday=FR,MO ;interval=1")!.ToString());

    [Fact]
    public async Task Completing_a_recurring_task_spawns_the_next_once_with_copies()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var member = t.AddUser("R_M");
        await svc.AddMemberAsync(owner, p, new AddProjectMemberRequest(member.Id, "member"), default);
        var prereq = await TaskAsync(svc, owner, p, "Prereq", status: "done");
        var due = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
        var task = await TaskAsync(svc, owner, p, "Weekly report", due: due, start: due.AddDays(-2));
        await svc.AssignUserAsync(owner, task.Id, member.Id, "owner", default);
        await svc.SetTaskDependenciesAsync(owner, task.Id, new([prereq.Id]), default);
        await TaskAsync(svc, owner, p, "Sub", parent: task.Id, status: "done");

        Assert.Equal("invalid_recurrence", (await svc.UpdateTaskAsync(owner, task.Id, Update(rule: "FREQ=HOURLY"), default)).Error);
        var set = await svc.UpdateTaskAsync(owner, task.Id, Update(rule: "freq=weekly"), default);
        Assert.True(set.Task!.IsRecurring);
        Assert.Equal("FREQ=WEEKLY", set.Task.RecurrenceRule);

        var evidence = "[EVIDENCE_DATA]{\"x\":1}[/EVIDENCE_DATA]";
        var done = await svc.UpdateTaskAsync(owner, task.Id, Update("done", description: "Body\n\n" + evidence), default);
        var nextId = Assert.IsType<Guid>(done.Task!.NextRecurringTaskId);
        Assert.Null(done.Task.RecurrenceRule);

        var next = (await svc.GetTaskAsync(nextId, owner, default))!;
        Assert.Equal("todo", next.Status);
        Assert.Equal("Weekly report", next.Title);
        Assert.Equal("Body", next.Description);
        Assert.Equal(due.AddDays(7), next.DueDate);
        Assert.Equal(due.AddDays(5), next.StartDate);
        Assert.Equal(task.GroupId, next.GroupId);
        Assert.Equal("FREQ=WEEKLY", next.RecurrenceRule);
        Assert.Equal(member.Id, Assert.Single(next.Assignees).UserId);
        Assert.Equal(prereq.Id, Assert.Single(next.DependsOn!).Id);
        Assert.Equal(0, next.SubtaskCount);

        // Reopen + complete again: no second spawn.
        await svc.UpdateTaskAsync(owner, task.Id, Update("doing"), default);
        var again = await svc.MoveTaskAsync(owner, task.Id, new TaskMoveRequest("done", null), default);
        Assert.Null(again.Task!.NextRecurringTaskId);
        Assert.Equal(2, await t.Verify().ProjectTasks.CountAsync(x => x.Title == "Weekly report"));
    }

    // ── Soft delete + restore ────────────────────────────────────────────────

    [Fact]
    public async Task Delete_is_soft_hides_everywhere_and_restores_by_batch()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var a = await TaskAsync(svc, owner, p, "A");
        var sub = await TaskAsync(svc, owner, p, "Sub", parent: a.Id);
        var keep = await TaskAsync(svc, owner, p, "Keep");

        var del = await svc.DeleteTaskAsync(owner, a.Id, default);
        Assert.True(del.Success);
        Assert.Equal(new[] { a.Id, sub.Id }.OrderBy(x => x), del.TaskIds.OrderBy(x => x));
        Assert.Equal(keep.Id, Assert.Single(await svc.GetTasksAsync(p, null, default)).Id);
        var kanban = (await svc.GetKanbanAsync(p, owner, default))!;
        Assert.DoesNotContain(kanban.Todo, x => x.Id == a.Id);
        Assert.Null(await svc.GetTaskAsync(a.Id, owner, default));
        Assert.Equal(3, await t.Verify().ProjectTasks.IgnoreQueryFilters().CountAsync());

        Assert.Equal("restore_expired", (await svc.RestoreTasksAsync(owner, p, Guid.NewGuid(), default)).Error);
        Assert.True((await svc.RestoreTasksAsync(owner, p, del.BatchId!.Value, default)).Success);
        Assert.Equal(2, (await svc.GetTasksAsync(p, null, default)).Count);
        Assert.NotNull(await svc.GetTaskAsync(sub.Id, owner, default));
    }

    [Fact]
    public async Task Purge_removes_only_old_deleted_tasks()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var a = await TaskAsync(svc, owner, p, "A");
        await TaskAsync(svc, owner, p, "Sub", parent: a.Id);
        var b = await TaskAsync(svc, owner, p, "B");
        await svc.SetTaskDependenciesAsync(owner, b.Id, new([a.Id]), default);
        await svc.DeleteTaskAsync(owner, a.Id, default);

        Assert.Equal(0, await svc.PurgeDeletedTasksAsync(DateTime.UtcNow.AddDays(-7), default));
        Assert.Equal(2, await svc.PurgeDeletedTasksAsync(DateTime.UtcNow.AddMinutes(1), default));
        var db = t.Verify();
        Assert.Equal(b.Id, Assert.Single(await db.ProjectTasks.IgnoreQueryFilters().ToListAsync()).Id);
        Assert.Empty(await db.ProjectTaskDependencies.ToListAsync());
    }

    // ── Bulk ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bulk_status_skips_unmet_dependencies_and_applies_shared_evidence()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var a = await TaskAsync(svc, owner, p, "A");
        var b = await TaskAsync(svc, owner, p, "B");
        var c = await TaskAsync(svc, owner, p, "C");
        await svc.SetTaskDependenciesAsync(owner, c.Id, new([a.Id]), default);
        var evidence = "[EVIDENCE_DATA]{\"note\":\"ok\"}[/EVIDENCE_DATA]";

        Assert.Equal("invalid_evidence", (await svc.BulkUpdateTasksAsync(owner, p, new([a.Id], "status", "done", "plain"), default)).Error);
        var r = await svc.BulkUpdateTasksAsync(owner, p, new([a.Id, b.Id, c.Id], "status", "done", evidence), default);
        Assert.True(r.Success);
        Assert.Equal(2, r.Updated);
        Assert.Equal(new BulkSkippedDto(c.Id, "unmet_dependencies"), Assert.Single(r.Skipped));
        var tasks = await svc.GetTasksAsync(p, null, default);
        Assert.All(tasks.Where(x => x.Id != c.Id), x => { Assert.Equal("done", x.Status); Assert.Contains(evidence, x.Description); });
        Assert.Equal("todo", tasks.Single(x => x.Id == c.Id).Status);
    }

    [Fact]
    public async Task Bulk_assign_due_priority_group_and_delete()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var member = t.AddUser("B_M");
        await svc.AddMemberAsync(owner, p, new AddProjectMemberRequest(member.Id, "member"), default);
        var a = await TaskAsync(svc, owner, p, "A");
        var b = await TaskAsync(svc, owner, p, "B");
        var sub = await TaskAsync(svc, owner, p, "Sub", parent: a.Id);
        var group = (await svc.CreateGroupAsync(owner, p, new CreateProjectGroupRequest("Next"), default)).Group!;
        IReadOnlyList<Guid> both = [a.Id, b.Id];

        Assert.Equal(2, (await svc.BulkUpdateTasksAsync(owner, p, new(both, "assign", member.Id.ToString()), default)).Updated);
        Assert.Equal(2, (await svc.BulkUpdateTasksAsync(owner, p, new(both, "assign", member.Id.ToString()), default)).Skipped.Count);
        Assert.Equal(1, (await svc.BulkUpdateTasksAsync(owner, p, new([a.Id], "unassign", member.Id.ToString()), default)).Updated);
        Assert.Equal("invalid_value", (await svc.BulkUpdateTasksAsync(owner, p, new(both, "due", "10/05/2026"), default)).Error);
        Assert.True((await svc.BulkUpdateTasksAsync(owner, p, new(both, "due", "2026-10-05"), default)).Success);
        Assert.True((await svc.BulkUpdateTasksAsync(owner, p, new(both, "priority", "high"), default)).Success);
        Assert.Equal("invalid_value", (await svc.BulkUpdateTasksAsync(owner, p, new(both, "priority", "meh"), default)).Error);

        var g = await svc.BulkUpdateTasksAsync(owner, p, new([a.Id, sub.Id], "group", group.Id.ToString()), default);
        Assert.Equal(1, g.Updated);
        var lone = await svc.BulkUpdateTasksAsync(owner, p, new([sub.Id], "group", group.Id.ToString()), default);
        Assert.Equal("move_parent_instead", Assert.Single(lone.Skipped).Reason);

        var fresh = new ProjectService(t.Verify());
        var tasks = await fresh.GetTasksAsync(p, null, default);
        Assert.All(tasks, x => { Assert.Equal("high", x.Priority); Assert.Equal(new DateTime(2026, 10, 5), x.DueDate!.Value.Date); });
        Assert.Equal(group.Id, tasks.Single(x => x.Id == a.Id).GroupId);
        Assert.Equal(group.Id, (await fresh.GetTaskAsync(sub.Id, owner, default))!.GroupId);
        Assert.Single(tasks.Single(x => x.Id == b.Id).Assignees);

        var del = await fresh.BulkUpdateTasksAsync(owner, p, new(both, "delete", null), default);
        Assert.NotNull(del.BatchId);
        Assert.Empty(await fresh.GetTasksAsync(p, null, default));
        Assert.True((await fresh.RestoreTasksAsync(owner, p, del.BatchId!.Value, default)).Success);
        Assert.Equal(2, (await fresh.GetTasksAsync(p, null, default)).Count);
    }

    [Fact]
    public async Task Bulk_rejects_foreign_ids_outsiders_and_unknown_actions()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var other = (await svc.CreateProjectAsync(owner, new CreateProjectRequest("Other", null, 2026, owner, null, null, null, null), default)).Project!;
        var a = await TaskAsync(svc, owner, p, "A");
        var foreign = await TaskAsync(svc, owner, other.Id, "X");
        var outsider = t.AddUser("B_OUT");

        Assert.Equal("invalid_tasks", (await svc.BulkUpdateTasksAsync(owner, p, new([a.Id, foreign.Id], "priority", "low"), default)).Error);
        Assert.Equal("invalid_tasks", (await svc.BulkUpdateTasksAsync(owner, p, new([], "priority", "low"), default)).Error);
        Assert.Equal("forbidden", (await svc.BulkUpdateTasksAsync(outsider.Id, p, new([a.Id], "priority", "low"), default)).Error);
        Assert.Equal("invalid_action", (await svc.BulkUpdateTasksAsync(owner, p, new([a.Id], "explode", null), default)).Error);
        Assert.Equal("invalid_value", (await svc.BulkUpdateTasksAsync(owner, p, new([a.Id], "assign", outsider.Id.ToString()), default)).Error);
    }

    // ── Saved views ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Saved_views_are_private_unless_shared_by_a_manager()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        var member = t.AddUser("V_M");
        await svc.AddMemberAsync(owner, p, new AddProjectMemberRequest(member.Id, "member"), default);

        var mine = await svc.CreateSavedViewAsync(member.Id, p, new("Mine", "{\"tab\":\"list\",\"myTasks\":true}"), default);
        Assert.True(mine.Success);
        Assert.Equal("forbidden", (await svc.CreateSavedViewAsync(member.Id, p, new("Shared", "{}", true), default)).Error);
        var shared = await svc.CreateSavedViewAsync(owner, p, new("Team", "{\"priority\":\"high\"}", true), default);
        Assert.True(shared.Success);
        await svc.CreateSavedViewAsync(owner, p, new("Owner private", "{}"), default);

        var seen = await svc.GetSavedViewsAsync(p, member.Id, default);
        Assert.Equal(["Mine", "Team"], seen!.Select(v => v.Name));
        Assert.True(seen[0].IsMine);
        Assert.False(seen[1].IsMine);
        Assert.Null(await svc.GetSavedViewsAsync(p, t.AddUser("V_OUT").Id, default));

        Assert.Equal("forbidden", (await svc.UpdateSavedViewAsync(member.Id, shared.View!.Id, new("Hack", "{}", true), default)).Error);
        Assert.Equal("forbidden", (await svc.DeleteSavedViewAsync(member.Id, shared.View.Id, default)).Error);
        Assert.Equal("Renamed", (await svc.UpdateSavedViewAsync(member.Id, mine.View!.Id, new("Renamed", "{}"), default)).View!.Name);
        Assert.True((await svc.DeleteSavedViewAsync(member.Id, mine.View.Id, default)).Success);
        Assert.Equal("view_not_found", (await svc.DeleteSavedViewAsync(member.Id, mine.View.Id, default)).Error);
    }

    [Fact]
    public async Task Saved_views_validate_name_state_and_limit()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        Assert.Equal("invalid_name", (await svc.CreateSavedViewAsync(owner, p, new("  ", "{}"), default)).Error);
        Assert.Equal("invalid_name", (await svc.CreateSavedViewAsync(owner, p, new(new string('x', 41), "{}"), default)).Error);
        Assert.Equal("invalid_state", (await svc.CreateSavedViewAsync(owner, p, new("A", "[1]"), default)).Error);
        Assert.Equal("invalid_state", (await svc.CreateSavedViewAsync(owner, p, new("A", "{nope"), default)).Error);
        for (var i = 0; i < 20; i++) Assert.True((await svc.CreateSavedViewAsync(owner, p, new($"V{i}", "{}"), default)).Success);
        Assert.Equal("too_many_views", (await svc.CreateSavedViewAsync(owner, p, new("V20", "{}"), default)).Error);
    }

    [Fact]
    public void View_state_keeps_only_known_well_typed_keys()
    {
        var g = Guid.NewGuid();
        var json = ProjectService.SanitizeViewState(
            $"{{\"tab\":\"board\",\"priority\":\"<script>\",\"search\":\" hi \",\"myTasks\":\"yes\",\"overdue\":true,\"groupId\":\"{g}\",\"assigneeId\":\"nope\",\"evil\":1}}");
        Assert.Equal($"{{\"tab\":\"board\",\"search\":\"hi\",\"overdue\":true,\"groupId\":\"{g}\"}}", json);
    }

    // ── Calendar ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calendar_returns_tasks_overlapping_the_range()
    {
        var (t, svc, owner, p) = await SetupAsync();
        using var _ = t;
        DateTime D(int day) => new(2026, 10, day, 0, 0, 0, DateTimeKind.Utc);
        var inRange = await TaskAsync(svc, owner, p, "In", due: D(10));
        var spans = await TaskAsync(svc, owner, p, "Spans", start: new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc), due: D(2));
        var startOnly = await TaskAsync(svc, owner, p, "StartOnly", start: D(31));
        await TaskAsync(svc, owner, p, "Before", due: new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));
        await TaskAsync(svc, owner, p, "After", due: new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        await TaskAsync(svc, owner, p, "Undated");
        var blocker = await TaskAsync(svc, owner, p, "Blocker");
        await svc.SetTaskDependenciesAsync(owner, inRange.Id, new([blocker.Id]), default);

        var cal = (await svc.GetProjectCalendarAsync(p, owner, D(1), D(31), null, default))!;
        Assert.Equal(new[] { spans.Id, inRange.Id, startOnly.Id }, cal.Tasks.Select(x => x.Id));
        Assert.True(cal.Tasks.Single(x => x.Id == inRange.Id).IsBlocked);
        Assert.False(cal.Truncated);
        Assert.Null(await svc.GetProjectCalendarAsync(p, t.AddUser("C_OUT").Id, D(1), D(31), null, default));
    }
}
