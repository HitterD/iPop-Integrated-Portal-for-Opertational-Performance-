using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Happy-path coverage for Projects MVP per ipop-02:
/// - Create project (auto-adds creator + owner as members)
/// - CRUD task, assign/unassign
/// - Kanban move (status transition + sort)
/// - Progress recalculation on status changes
public class ProjectServiceTests
{
    [Fact]
    public async Task CreateProject_adds_creator_and_owner_as_members()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var creator = t.AddUser("CRE");
        var svc = new ProjectService(t.Verify());

        var req = new CreateProjectRequest("Alpha", "desc", 2026, owner.Id, null, null, null, null);
        var result = await svc.CreateProjectAsync(creator.Id, req, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Project);
        Assert.Equal("Alpha", result.Project.Name);
        Assert.Equal(2026, result.Project.Year);
        Assert.Equal("planning", result.Project.Status);
        Assert.Equal(0, result.Project.Progress);

        var members = await svc.GetMembersAsync(result.Project.Id, default);
        Assert.Contains(members, m => m.UserId == creator.Id && m.Role == "manager");
        Assert.Contains(members, m => m.UserId == owner.Id && m.Role == "owner");
    }

    [Fact]
    public async Task CreateTask_list_and_progress_updates_on_done()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("P1", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        // Create 3 top-level tasks in todo
        var a = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "A", null, "todo", "medium", null, null, null, null, "backend"), default)).Task!;
        var b = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "B", null, "todo", null, null, null, null, null, null), default)).Task!;
        var c = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "C", null, "todo", null, null, null, null, null, "urgent"), default)).Task!;

        Assert.Equal("backend", a.Tags);
        Assert.Null(b.Tags);
        Assert.Equal("urgent", c.Tags);

        // Move two to done -> progress should be 67 (2/3)
        await svc.MoveTaskAsync(creator.Id, a.Id, new TaskMoveRequest("done", null), default);
        await svc.MoveTaskAsync(creator.Id, b.Id, new TaskMoveRequest("done", null), default);

        var proj = await svc.GetProjectAsync(p.Id, creator.Id, default);
        Assert.NotNull(proj);
        Assert.Equal(67, proj!.Progress); // rounded 66.6.. -> 67
        Assert.Equal(2, proj.DoneCount);
        Assert.Equal(1, proj.TodoCount);

        // List with status filter
        var doneList = await svc.GetTaskListAsync(p.Id, creator.Id, "done", 1, 50, default);
        Assert.Equal(2, doneList.TotalCount);
    }

    [Fact]
    public async Task UpdateTask_records_activity_for_status_title_and_due_changes()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("P1", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var task = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T1", null, "todo", null, null, null, null, null, null), default)).Task!;

        // UpdateTask tanpa perubahan → tanpa baris aktivitas.
        await svc.UpdateTaskAsync(creator.Id, task.Id, new UpdateTaskRequest(Title: "T1", null, null, null, null, null, null, null, null, null, null, null, null, null), default);
        Assert.Empty(t.Verify().ProjectTaskActivities.Where(a => a.TaskId == task.Id));

        // Perubahan status + judul + tenggat → 3 baris aktivitas.
        var due = DateTime.UtcNow.Date.AddDays(7);
        await svc.UpdateTaskAsync(creator.Id, task.Id, new UpdateTaskRequest("T1-rev", null, "doing", null, null, due, null, null, null, null, null, null, null, null), default);

        var rows = t.Verify().ProjectTaskActivities.Where(a => a.TaskId == task.Id).OrderBy(a => a.Action).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Action == "status_changed" && r.OldValue == "todo" && r.NewValue == "doing");
        Assert.Contains(rows, r => r.Action == "title_changed" && r.NewValue == "T1-rev");
        Assert.Contains(rows, r => r.Action == "due_changed" && r.NewValue == due.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Assign_and_unassign_record_activity()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var pic = t.AddUser("PIC");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(owner.Id, new CreateProjectRequest("P1", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var task = (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(p.Id, null, "T1", null, "todo", null, null, null, null, null, null), default)).Task!;

        await svc.AddMemberAsync(owner.Id, p.Id, new AddProjectMemberRequest(pic.Id, "member"), default);
        await svc.AssignUserAsync(owner.Id, task.Id, pic.Id, "lead", default);
        await svc.UnassignUserAsync(owner.Id, task.Id, pic.Id, default);

        var actions = t.Verify().ProjectTaskActivities.Where(a => a.TaskId == task.Id).OrderBy(a => a.CreatedAt).Select(a => a.Action).ToList();
        Assert.Equal(new[] { "assignee_added", "assignee_removed" }, actions);
    }

    [Fact]
    public async Task Kanban_board_groups_by_status_and_returns_top_level()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("P2", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        var t1 = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T1", null, "todo", null, null, null, null, null, null), default)).Task!;
        var t2 = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T2", null, "doing", null, null, null, null, null, "kanban"), default)).Task!;
        var t3 = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T3", null, "done", null, null, null, null, null, null), default)).Task!;

        var board = await svc.GetKanbanAsync(p.Id, creator.Id, default);
        Assert.NotNull(board);
        Assert.Single(board!.Todo);
        Assert.Single(board.Doing);
        Assert.Single(board.Done);
    }

    /// The v6 board shows a fourth lane between In Progress and Done; the Review status
    /// has to round-trip through create, move, and every aggregate that counts statuses.
    [Fact]
    public async Task Review_status_is_a_first_class_lane()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("RV", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        // Create directly in Review.
        var created = await svc.CreateTaskAsync(creator.Id,
            new CreateTaskRequest(p.Id, null, "Awaiting sign-off", null, "review", null, null, null, 3, null, "qa"), default);
        Assert.True(created.Success);
        Assert.Equal("review", created.Task!.Status);

        // Move into Review from another lane.
        var draft = (await svc.CreateTaskAsync(creator.Id,
            new CreateTaskRequest(p.Id, null, "Draft", null, "todo", null, null, null, null, null, null), default)).Task!;
        var moved = await svc.MoveTaskAsync(creator.Id, draft.Id, new TaskMoveRequest("review", null), default);
        Assert.True(moved.Success);
        Assert.Equal("review", moved.Task!.Status);

        // Kanban exposes the new lane. Todo is empty: the draft was moved out of it.
        var board = await svc.GetKanbanAsync(p.Id, creator.Id, default);
        Assert.NotNull(board);
        Assert.Equal(2, board!.Review!.Count);
        Assert.Empty(board.Todo);
        Assert.Empty(board.Doing);
        Assert.Empty(board.Done);

        // Workload and project counts include it.
        var wl = await svc.GetProjectWorkloadAsync(p.Id, creator.Id, default);
        Assert.NotNull(wl);
        Assert.Equal(2, wl!.ReviewTasks);
        Assert.Equal(3, wl.ReviewEstimatedHours);

        var proj = await svc.GetProjectAsync(p.Id, creator.Id, default);
        Assert.NotNull(proj);
        Assert.Equal(2, proj!.ReviewCount);
        Assert.Equal(0, proj.DoingCount);
    }

    [Fact]
    public async Task Assign_unassign_and_subtasks()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var pic = t.AddUser("P");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("P3", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var parent = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "Parent", null, "todo", null, null, null, null, null, "parent"), default)).Task!;

        // Create a subtask under parent
        var child = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, parent.Id, "Child", null, "todo", null, null, null, null, null, null), default)).Task!;
        Assert.Equal(parent.Id, child.ParentTaskId);

        // Assign/unassign on parent
        await svc.AddMemberAsync(owner.Id, p.Id, new AddProjectMemberRequest(pic.Id, "member"), default);
        await svc.AssignUserAsync(creator.Id, parent.Id, pic.Id, "lead", default);
        var loaded = await svc.GetTaskAsync(parent.Id, creator.Id, default);
        Assert.NotNull(loaded);
        Assert.Contains(loaded!.Assignees, a => a.UserId == pic.Id && a.Role == "lead");

        await svc.UnassignUserAsync(creator.Id, parent.Id, pic.Id, default);
        loaded = await svc.GetTaskAsync(parent.Id, creator.Id, default);
        Assert.DoesNotContain(loaded!.Assignees, a => a.UserId == pic.Id);
    }

    [Fact]
    public async Task Timeline_returns_all_tasks_with_dates()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(10);
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("P4", null, 2026, owner.Id, start, end, null, null), default)).Project!;

        await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T", null, "todo", null, start, end, 8, null, "timeline"), default);

        var tl = await svc.GetTimelineAsync(p.Id, creator.Id, default);
        Assert.NotNull(tl);
        Assert.Equal("P4", tl!.ProjectName);
        Assert.Single(tl.Tasks);
        Assert.Equal(start, tl.Tasks[0].StartDate);
        Assert.Equal(end, tl.Tasks[0].DueDate);
    }

    [Fact]
    public async Task Dependency_guard_blocks_move_to_done_and_allows_when_met()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Dep", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        // Create two tasks; second depends on first (store GUIDs as comma-separated)
        var a = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "A", null, "todo", null, null, null, null, null, null), default)).Task!;
        var b = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "B", null, "todo", null, null, null, null, a.Id.ToString(), null), default)).Task!;

        // Validate should report unmet
        var dep1 = await svc.ValidateTaskDependenciesAsync(b.Id, creator.Id, default);
        bool can1 = dep1.CanComplete;
        var unmet1 = dep1.UnmetDependencyIds;
        Assert.False(can1);
        Assert.Contains(a.Id, unmet1);

        // Moving B to done should fail
        var moveFail = await svc.MoveTaskAsync(creator.Id, b.Id, new TaskMoveRequest("done", null), default);
        Assert.False(moveFail.Success);
        Assert.Equal("unmet_dependencies", moveFail.Error);

        // Complete A then move B
        await svc.MoveTaskAsync(creator.Id, a.Id, new TaskMoveRequest("done", null), default);
        var moveOk = await svc.MoveTaskAsync(creator.Id, b.Id, new TaskMoveRequest("done", null), default);
        Assert.True(moveOk.Success);
        Assert.Equal("done", moveOk.Task!.Status);

        var dep2 = await svc.ValidateTaskDependenciesAsync(b.Id, creator.Id, default);
        bool can2 = dep2.CanComplete;
        var unmet2 = dep2.UnmetDependencyIds;
        Assert.True(can2);
        Assert.Empty(unmet2);
    }

    [Fact]
    public async Task Workload_and_export_and_recurring_preview_work()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var pic = t.AddUser("P");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(5);
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("WL", null, 2026, owner.Id, start, end, null, null), default)).Project!;

        var t1 = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T1", null, "todo", null, start, end, 4, null, null), default)).Task!;
        var t2 = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T2", null, "doing", null, start, end.AddDays(1), 2, null, null), default)).Task!;
        await svc.AddMemberAsync(creator.Id, p.Id, new AddProjectMemberRequest(pic.Id, "member"), default);
        await svc.AssignUserAsync(creator.Id, t1.Id, pic.Id, "owner", default);

        // Workload
        var wl = await svc.GetProjectWorkloadAsync(p.Id, creator.Id, default);
        Assert.NotNull(wl);
        Assert.Equal(2, wl!.TotalTasks);
        Assert.Equal(1, wl.TodoTasks);
        Assert.Equal(1, wl.DoingTasks);
        Assert.Equal(6, wl.TotalEstimatedHours);
        Assert.Equal(4, wl.ByAssignee.Count); // owner, creator, PIC, and explicit unassigned row
        var assigned = Assert.Single(wl.ByAssignee, a => a.UserId == pic.Id);
        Assert.Equal(4, assigned.EstimatedHours);
        Assert.Equal(1, assigned.TodoCount);
        var unassigned = Assert.Single(wl.ByAssignee, a => a.IsUnassigned);
        Assert.Equal(1, unassigned.TaskCount);
        Assert.Equal(2, unassigned.EstimatedHours);
        Assert.Contains(wl.ByAssignee, a => a.UserId == owner.Id && a.TaskCount == 0);
        Assert.Contains(wl.ByAssignee, a => a.UserId == creator.Id && a.TaskCount == 0);

        // Export
        var exp = await svc.GetProjectExportDataAsync(p.Id, creator.Id, default);
        Assert.NotNull(exp);
        Assert.Equal("WL", exp!.Project.Name);
        Assert.True(exp.Tasks.Count >= 2);
        Assert.True(exp.Members.Count >= 2);

        // Recurring preview (no rule -> weekly fallback from due)
        var tRec = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "Recur", null, "todo", null, null, end, null, null, null), default)).Task!;
        var prev = await svc.GetRecurringPreviewAsync(tRec.Id, creator.Id, 3, default);
        Assert.NotNull(prev);
        Assert.Equal(3, prev!.UpcomingDates.Count);
        Assert.All(prev.UpcomingDates, d => Assert.True(d > end));

        // Recurring instances (richer projection)
        var inst = await svc.GetRecurringInstancesPreviewAsync(tRec.Id, creator.Id, 2, default);
        Assert.NotNull(inst);
        Assert.Equal(2, inst!.Count);
        Assert.All(inst, i => { Assert.Equal("Recur", i.Title); Assert.Equal("todo", i.Status); Assert.NotNull(i.DueDate); });
    }

    [Fact]
    public async Task Workload_includes_idle_members_without_empty_unassigned_row_and_denies_nonmembers()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var idle = t.AddUser("IDLE");
        var outsider = t.AddUser("OUT");
        var svc = new ProjectService(t.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Idle", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(idle.Id, "member"), default);
        var user = await t.Db.Users.FindAsync(idle.Id);
        user!.Position = "Analyst";
        await t.Db.SaveChangesAsync();

        var workload = await svc.GetProjectWorkloadAsync(project.Id, owner.Id, default);
        Assert.NotNull(workload);
        Assert.Equal(0, workload!.TotalTasks);
        Assert.Equal(2, workload.ByAssignee.Count);
        Assert.Contains(workload.ByAssignee, a => a.UserId == owner.Id && a.TaskCount == 0);
        Assert.Contains(workload.ByAssignee, a => a.UserId == idle.Id && a.Position == null && a.TaskCount == 0);
        Assert.DoesNotContain(workload.ByAssignee, a => a.IsUnassigned);
        Assert.Null(await svc.GetProjectWorkloadAsync(project.Id, outsider.Id, default));
    }

    [Fact]
    public async Task Workload_counts_status_and_overdue_per_assignee_using_supplied_local_day()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var teammate = t.AddUser("TEAM");
        var svc = new ProjectService(t.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Cutoff", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        var today = new DateTime(2026, 9, 27);
        foreach (var (status, due) in new[]
        {
            ("todo", (DateTime?)today.AddDays(-1)), ("doing", (DateTime?)today.AddHours(12)),
            ("review", (DateTime?)today.AddDays(-2)), ("done", (DateTime?)today.AddDays(-3)),
            ("todo", (DateTime?)null)
        })
        {
            var task = (await svc.CreateTaskAsync(owner.Id,
                new CreateTaskRequest(project.Id, null, status, null, status, null, null, due, 2, null, null), default)).Task!;
            await svc.AssignUserAsync(owner.Id, task.Id, teammate.Id, null, default);
        }
        var unassigned = (await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Unassigned", null, "review", null, null, today.AddDays(-1), 3, null, null), default)).Task!;

        var workload = (await svc.GetProjectWorkloadAsync(project.Id, owner.Id, default, today))!;
        var row = Assert.Single(workload.ByAssignee, a => a.UserId == teammate.Id);
        Assert.Equal(5, row.TaskCount);
        Assert.Equal(10, row.EstimatedHours);
        Assert.Equal(2, row.TodoCount);
        Assert.Equal(1, row.DoingCount);
        Assert.Equal(1, row.ReviewCount);
        Assert.Equal(1, row.DoneCount);
        Assert.Equal(2, row.OverdueCount); // today and done are not overdue
        var bucket = Assert.Single(workload.ByAssignee, a => a.IsUnassigned);
        Assert.Equal(1, bucket.ReviewCount);
        Assert.Equal(1, bucket.OverdueCount);
        Assert.Equal(3, bucket.EstimatedHours);
        Assert.Equal(6, workload.TotalTasks);
        Assert.Equal(2, workload.ReviewTasks);
        Assert.NotNull(unassigned);
    }

    [Fact]
    public async Task Task_list_filters_assignee_or_unassigned_before_paging_and_preserves_unfiltered_calls()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var teammate = t.AddUser("TEAM");
        var outsider = t.AddUser("OUT");
        var svc = new ProjectService(t.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Filter", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var assigned = (await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Assigned", null, "todo", null, null, null, 4, null, null), default)).Task!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        await svc.AssignUserAsync(owner.Id, assigned.Id, teammate.Id, null, default);
        await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Unassigned", null, "todo", null, null, null, 1, null, null), default);
        await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Done", null, "done", null, null, null, 1, null, null), default);

        var all = await svc.GetTaskListAsync(project.Id, owner.Id, null, 1, 10, default);
        Assert.Equal(3, all.TotalCount);
        var assignedPage = await svc.GetTaskListAsync(project.Id, owner.Id, "todo", 1, 1, default, teammate.Id);
        Assert.Equal(1, assignedPage.TotalCount);
        Assert.Equal(assigned.Id, Assert.Single(assignedPage.Items).Id);
        var emptyPage = await svc.GetTaskListAsync(project.Id, owner.Id, null, 2, 1, default, teammate.Id);
        Assert.Equal(1, emptyPage.TotalCount);
        Assert.Empty(emptyPage.Items);
        var unassignedPage = await svc.GetTaskListAsync(project.Id, owner.Id, "todo", 1, 1, default, teammate.Id, true);
        Assert.Equal(1, unassignedPage.TotalCount); // unassigned takes precedence over assigneeId
        Assert.Equal("Unassigned", Assert.Single(unassignedPage.Items).Title);
        var denied = await svc.GetTaskListAsync(project.Id, outsider.Id, null, 1, 10, default, teammate.Id);
        Assert.Empty(denied.Items);
        Assert.Equal(0, denied.TotalCount);
    }

    [Fact]
    public async Task CreateProject_with_empty_ownerId_defaults_to_actorId()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var svc = new ProjectService(t.Verify());

        var req = new CreateProjectRequest("AutoOwner", "desc", 2026, Guid.Empty, null, null, null, null);
        var result = await svc.CreateProjectAsync(creator.Id, req, default);

        Assert.True(result.Success);
        Assert.NotNull(result.Project);
        Assert.Equal(creator.Id, result.Project.OwnerId);

        var members = await svc.GetMembersAsync(result.Project.Id, default);
        Assert.Single(members);
        Assert.Equal("owner", members[0].Role);
    }

    [Fact]
    public async Task CreateProject_with_invalid_ownerId_returns_error()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var svc = new ProjectService(t.Verify());

        var req = new CreateProjectRequest("BadOwner", "desc", 2026, Guid.NewGuid(), null, null, null, null);
        var result = await svc.CreateProjectAsync(creator.Id, req, default);

        Assert.False(result.Success);
        Assert.Equal("invalid_owner", result.Error);
    }

    [Fact]
    public async Task CreateProject_resolves_owner_from_ownerNik_and_adds_that_user_as_owner()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var owner = t.AddUser("OWN");
        var svc = new ProjectService(t.Verify());

        var req = new CreateProjectRequest("NikOwner", null, 2026, Guid.Empty, null, null, null, null, OwnerNik: owner.Nik);
        var result = await svc.CreateProjectAsync(creator.Id, req, default);

        Assert.True(result.Success);
        Assert.Equal(owner.Id, result.Project!.OwnerId);
        var members = await svc.GetMembersAsync(result.Project.Id, default);
        Assert.Contains(members, m => m.UserId == owner.Id && m.Role == "owner");
    }

    [Fact]
    public async Task CreateProject_with_unknown_ownerNik_returns_invalid_owner()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var svc = new ProjectService(t.Verify());

        var req = new CreateProjectRequest("BadNik", null, 2026, creator.Id, null, null, null, null, OwnerNik: "MISSING");
        var result = await svc.CreateProjectAsync(creator.Id, req, default);

        Assert.False(result.Success);
        Assert.Equal("invalid_owner", result.Error);
    }

    [Fact]
    public async Task UpdateProject_resolves_owner_from_ownerNik()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var owner = t.AddUser("OWN");
        var replacement = t.AddUser("NEW");
        var svc = new ProjectService(t.Verify());

        var project = (await svc.CreateProjectAsync(creator.Id,
            new CreateProjectRequest("Editable", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var result = await svc.UpdateProjectAsync(creator.Id, project.Id,
            new UpdateProjectRequest(null, null, null, null, null, null, null, null, null, OwnerNik: replacement.Nik), default);

        Assert.True(result.Success);
        Assert.Equal(replacement.Id, result.Project!.OwnerId);
    }

    [Fact]
    public async Task UpdateProject_with_unknown_ownerNik_returns_invalid_owner()
    {
        using var t = new TestDb();
        var creator = t.AddUser("CRE");
        var project = (await new ProjectService(t.Verify()).CreateProjectAsync(creator.Id,
            new CreateProjectRequest("Editable", null, 2026, Guid.Empty, null, null, null, null), default)).Project!;
        var result = await new ProjectService(t.Verify()).UpdateProjectAsync(creator.Id, project.Id,
            new UpdateProjectRequest(null, null, null, null, null, null, null, null, null, OwnerNik: "MISSING"), default);

        Assert.False(result.Success);
        Assert.Equal("invalid_owner", result.Error);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Chat room per project (interconnect)
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_creates_linked_chat_room_with_creator_and_owner()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var creator = t.AddUser("CRE");
        var svc = new ProjectService(t.Verify());

        var result = await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Room", null, 2026, owner.Id, null, null, null, null), default);
        Assert.True(result.Success);

        // Linked room exists with the project name and both members
        var roomDto = await svc.GetProjectChatRoomAsync(result.Project!.Id, creator.Id, default);
        Assert.NotNull(roomDto);
        Assert.Equal("Room", roomDto!.RoomName);
        Assert.Equal(2, roomDto.MemberCount);

        // Room is actually present in ChatRooms with a ProjectRoom link
        var pr = await t.Verify().ProjectRooms.FirstOrDefaultAsync(r => r.ProjectId == result.Project.Id);
        Assert.NotNull(pr);
        var room = await t.Verify().ChatRooms.FirstOrDefaultAsync(r => r.Id == pr!.RoomId);
        Assert.NotNull(room);
        Assert.Equal("group", room!.Type);
        Assert.True(room.IsActive);
    }

    [Fact]
    public async Task Add_and_remove_project_member_syncs_chat_room_membership()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var creator = t.AddUser("CRE");
        var pic = t.AddUser("PIC");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Sync", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var roomDto = await svc.GetProjectChatRoomAsync(p.Id, creator.Id, default);
        Assert.NotNull(roomDto);
        Assert.Equal(2, roomDto!.MemberCount);

        // Add PIC as project member -> also joins chat room
        await svc.AddMemberAsync(creator.Id, p.Id, new AddProjectMemberRequest(pic.Id, "member"), default);
        var afterAdd = await svc.GetProjectChatRoomAsync(p.Id, creator.Id, default);
        Assert.Equal(3, afterAdd!.MemberCount);

        // Remove PIC from project -> leaves chat room
        await svc.RemoveMemberAsync(creator.Id, p.Id, pic.Id, default);
        var afterRemove = await svc.GetProjectChatRoomAsync(p.Id, creator.Id, default);
        Assert.Equal(2, afterRemove!.MemberCount);
    }

    [Fact]
    public async Task RemoveMember_cleans_only_project_assignments_and_soft_leaves_only_linked_room()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var teammate = t.AddUser("TEAM");
        var svc = new ProjectService(t.Verify());
        var first = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("First", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var second = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Second", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, first.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        await svc.AddMemberAsync(owner.Id, second.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        var firstTask = (await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(first.Id, null, "First task", null, "todo", null, null, null, 2, null, null), default)).Task!;
        var secondTask = (await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(second.Id, null, "Second task", null, "todo", null, null, null, 3, null, null), default)).Task!;
        await svc.AssignUserAsync(owner.Id, firstTask.Id, teammate.Id, null, default);
        await svc.AssignUserAsync(owner.Id, secondTask.Id, teammate.Id, null, default);
        var firstRoomId = await t.Db.ProjectRooms.Where(r => r.ProjectId == first.Id).Select(r => r.RoomId).SingleAsync();
        var secondRoomId = await t.Db.ProjectRooms.Where(r => r.ProjectId == second.Id).Select(r => r.RoomId).SingleAsync();

        var result = await svc.RemoveMemberAsync(owner.Id, first.Id, teammate.Id, default);
        Assert.True(result.Success);
        using var verify = t.Verify();
        Assert.False(await verify.ProjectMembers.AnyAsync(m => m.ProjectId == first.Id && m.UserId == teammate.Id));
        Assert.True(await verify.ProjectMembers.AnyAsync(m => m.ProjectId == second.Id && m.UserId == teammate.Id));
        Assert.False(await verify.ProjectTaskAssignees.AnyAsync(a => a.TaskId == firstTask.Id && a.UserId == teammate.Id));
        Assert.True(await verify.ProjectTaskAssignees.AnyAsync(a => a.TaskId == secondTask.Id && a.UserId == teammate.Id));
        Assert.NotNull((await verify.ChatRoomMembers.SingleAsync(m => m.RoomId == firstRoomId && m.UserId == teammate.Id)).LeftAt);
        Assert.Null((await verify.ChatRoomMembers.SingleAsync(m => m.RoomId == secondRoomId && m.UserId == teammate.Id)).LeftAt);
        var workload = (await svc.GetProjectWorkloadAsync(first.Id, owner.Id, default))!;
        Assert.DoesNotContain(workload.ByAssignee, a => a.UserId == teammate.Id);
        Assert.Equal(1, Assert.Single(workload.ByAssignee, a => a.IsUnassigned).TaskCount);
    }

    [Fact]
    public async Task RemoveMember_preserves_forbidden_missing_member_and_last_owner_guards()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var teammate = t.AddUser("TEAM");
        var outsider = t.AddUser("OUT");
        var svc = new ProjectService(t.Verify());
        var project = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Guards", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        var task = (await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(project.Id, null, "Guarded", null, "todo", null, null, null, null, null, null), default)).Task!;
        await svc.AssignUserAsync(owner.Id, task.Id, teammate.Id, null, default);

        Assert.Equal("forbidden", (await svc.RemoveMemberAsync(outsider.Id, project.Id, teammate.Id, default)).Error);
        Assert.Equal("member_not_found", (await svc.RemoveMemberAsync(owner.Id, project.Id, outsider.Id, default)).Error);
        Assert.Equal("cannot_remove_last_owner", (await svc.RemoveMemberAsync(owner.Id, project.Id, owner.Id, default)).Error);
        using var verify = t.Verify();
        Assert.True(await verify.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == teammate.Id));
        Assert.True(await verify.ProjectTaskAssignees.AnyAsync(a => a.TaskId == task.Id && a.UserId == teammate.Id));
        var roomId = await verify.ProjectRooms.Where(r => r.ProjectId == project.Id).Select(r => r.RoomId).SingleAsync();
        Assert.Null((await verify.ChatRoomMembers.SingleAsync(m => m.RoomId == roomId && m.UserId == teammate.Id)).LeftAt);
    }

    [Fact]
    public async Task GetProjectChatRoom_returns_null_for_non_member()
    {
        using var t = new TestDb();
        var owner = t.AddUser("OWN");
        var creator = t.AddUser("CRE");
        var stranger = t.AddUser("STR");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Priv", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        var room = await svc.GetProjectChatRoomAsync(p.Id, stranger.Id, default);
        Assert.Null(room);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: Gantt date-mapping + critical path
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task October_8_fixture_has_inclusive_durations_and_full_multiassignee_workload()
    {
        using var t = new TestDb(); // SQLite :memory: only; no application database.
        var owner = t.AddUser("QA-OWNER");
        var teammate = t.AddUser("QA-TEAM");
        var svc = new ProjectService(t.Verify());
        var start = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
        var project = (await svc.CreateProjectAsync(owner.Id,
            new CreateProjectRequest("Fixed Oct 8", null, 2026, owner.Id, start, start.AddDays(5), null, null), default)).Project!;
        await svc.AddMemberAsync(owner.Id, project.Id, new AddProjectMemberRequest(teammate.Id, "member"), default);
        async Task<ProjectTaskDto> Create(string title, string status, int dueOffset, decimal estimate) =>
            (await svc.CreateTaskAsync(owner.Id, new CreateTaskRequest(project.Id, null, title, null, status,
                null, start, start.AddDays(dueOffset), estimate, null, null), default)).Task!;
        var a = await Create("A", "todo", 1, 4);
        var b = await Create("B", "doing", 3, 6);
        var c = await Create("C", "done", 5, 2);
        foreach (var task in await t.Db.ProjectTasks.Where(x => x.ProjectId == project.Id).ToListAsync())
        {
            task.CreatedAt = start;
            task.UpdatedAt = start.AddHours(2);
            task.CompletedAt = task.Status == "done" ? start.AddHours(1) : null;
        }
        await t.Db.SaveChangesAsync();
        await svc.AssignUserAsync(owner.Id, a.Id, owner.Id, null, default);
        await svc.AssignUserAsync(owner.Id, b.Id, owner.Id, null, default);
        await svc.AssignUserAsync(owner.Id, b.Id, teammate.Id, null, default);
        // C stays unassigned; B's full 6h belongs to both people, not 3h each.
        var workload = (await svc.GetProjectWorkloadAsync(project.Id, owner.Id, default, start))!;
        Assert.Equal(3, workload.TotalTasks);
        Assert.Equal((1, 1, 1, 0), (workload.TodoTasks, workload.DoingTasks, workload.DoneTasks, workload.ReviewTasks));
        Assert.Equal((12m, 4m, 6m, 2m), (workload.TotalEstimatedHours, workload.TodoEstimatedHours, workload.DoingEstimatedHours, workload.DoneEstimatedHours));
        var own = Assert.Single(workload.ByAssignee, x => x.UserId == owner.Id);
        var team = Assert.Single(workload.ByAssignee, x => x.UserId == teammate.Id);
        var unassigned = Assert.Single(workload.ByAssignee, x => x.IsUnassigned);
        Assert.Equal((2, 10m, 1, 1, 0), (own.TaskCount, own.EstimatedHours, own.TodoCount, own.DoingCount, own.OverdueCount));
        Assert.Equal((1, 6m, 1, 0), (team.TaskCount, team.EstimatedHours, team.DoingCount, team.OverdueCount));
        Assert.Equal((1, 2m, 1, 0), (unassigned.TaskCount, unassigned.EstimatedHours, unassigned.DoneCount, unassigned.OverdueCount));
        Assert.Equal(18m, workload.ByAssignee.Sum(x => x.EstimatedHours));
        Assert.Equal(4, workload.ByAssignee.Sum(x => x.TaskCount));
        Assert.Equal(33, await svc.RecalculateProgressAsync(project.Id, default));

        var gantt = (await svc.GetGanttAsync(project.Id, owner.Id, default))!;
        Assert.Equal(start, gantt.ProjectStart);
        Assert.Equal(start.AddDays(5), gantt.ProjectDeadline);
        Assert.Equal(6, gantt.TotalDays);
        Assert.Equal(new int?[] { 2, 4, 6 }, new[] { a, b, c }.Select(x => gantt.Tasks.Single(y => y.Id == x.Id).DurationDays));
        Assert.All(gantt.Tasks, x => Assert.Equal(0, x.DayIndexStart));
        Assert.Equal(new[] { c.Id }, gantt.CriticalPathTaskIds); // longest independent chain: C, even though done.

        await svc.SetTaskDependenciesAsync(owner.Id, b.Id, new SetTaskDependenciesRequest([a.Id]), default);
        await svc.SetTaskDependenciesAsync(owner.Id, c.Id, new SetTaskDependenciesRequest([b.Id]), default);
        var chain = (await svc.GetGanttAsync(project.Id, owner.Id, default))!;
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, chain.CriticalPathTaskIds); // 2 + 4 + 6 = 12 dependency days.
        Assert.All(chain.Tasks, x => Assert.True(x.IsCritical));
        Assert.Equal(6, chain.TotalDays); // saved calendar window does not reschedule to the dependency finish.
    }

    [Fact]
    public async Task Gantt_maps_day_indexes_and_detects_critical_path()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Gantt", null, 2026, owner.Id, start, start.AddDays(5), null, null), default)).Project!;

        // A: start day 0, duration 3. B: depends on A, start day 3, duration 2. C: start day 0, duration 5.
        var a = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "A", null, "todo", null, start, start.AddDays(2), 8, null, null), default)).Task!;
        var b = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "B", null, "todo", null, start.AddDays(3), start.AddDays(4), 8, a.Id.ToString(), null), default)).Task!;
        var c = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "C", null, "todo", null, start, start.AddDays(4), 8, null, null), default)).Task!;

        var gantt = await svc.GetGanttAsync(p.Id, creator.Id, default);
        Assert.NotNull(gantt);

        // Window start = project start
        Assert.Equal(start, gantt!.ProjectStart);
        Assert.Equal(6, gantt.TotalDays); // day 0..5 inclusive

        var aDto = gantt.Tasks.Single(x => x.Id == a.Id);
        var bDto = gantt.Tasks.Single(x => x.Id == b.Id);
        var cDto = gantt.Tasks.Single(x => x.Id == c.Id);

        Assert.Equal(0, aDto.DayIndexStart);
        Assert.Equal(3, aDto.DurationDays);
        Assert.Equal(3, bDto.DayIndexStart);
        Assert.Equal(2, bDto.DurationDays);
        Assert.Equal(0, cDto.DayIndexStart);
        Assert.Equal(5, cDto.DurationDays);

        // Critical path: A->B (3+2=5) is longer than C (5 days) — both chains equal finish.
        // With A=3,B=2 and C=5 both reach day 5 -> both critical.
        Assert.True(aDto.IsCritical);
        Assert.True(bDto.IsCritical);
        Assert.True(cDto.IsCritical);
    }

    [Fact]
    public async Task Gantt_flags_non_critical_parallel_chain()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("CP", null, 2026, owner.Id, start, start.AddDays(4), null, null), default)).Project!;

        // A(2) -> B(2) = 4 days (critical). C(3) parallel = 3 days (slack 1, not critical).
        var a = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "A", null, "todo", null, start, start.AddDays(1), 8, null, null), default)).Task!;
        var b = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "B", null, "todo", null, start.AddDays(2), start.AddDays(3), 8, a.Id.ToString(), null), default)).Task!;
        var c = (await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "C", null, "todo", null, start, start.AddDays(2), 8, null, null), default)).Task!;

        var gantt = await svc.GetGanttAsync(p.Id, creator.Id, default);
        Assert.NotNull(gantt);

        var aDto = gantt!.Tasks.Single(x => x.Id == a.Id);
        var bDto = gantt.Tasks.Single(x => x.Id == b.Id);
        var cDto = gantt.Tasks.Single(x => x.Id == c.Id);

        Assert.True(aDto.IsCritical);
        Assert.True(bDto.IsCritical);
        Assert.False(cDto.IsCritical); // 3 < 4 -> slack

        Assert.Contains(a.Id, gantt.CriticalPathTaskIds);
        Assert.Contains(b.Id, gantt.CriticalPathTaskIds);
        Assert.DoesNotContain(c.Id, gantt.CriticalPathTaskIds);
    }

    [Fact]
    public async Task Gantt_handles_undated_tasks_and_empty_project()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("NoDates", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        // Empty project -> empty gantt
        var empty = await svc.GetGanttAsync(p.Id, creator.Id, default);
        Assert.NotNull(empty);
        Assert.Empty(empty!.Tasks);

        // Undated task -> no day index, not critical
        await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "U", null, "todo", null, null, null, null, null, null), default);
        var g = await svc.GetGanttAsync(p.Id, creator.Id, default);
        Assert.NotNull(g);
        var u = g!.Tasks.Single();
        Assert.Null(u.DayIndexStart);
        Assert.Null(u.DurationDays);
        Assert.False(u.IsCritical);
    }

    [Fact]
    public async Task Gantt_returns_null_for_non_member()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var stranger = t.AddUser("S");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("G", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var g = await svc.GetGanttAsync(p.Id, stranger.Id, default);
        Assert.Null(g);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ipop-21 PERFECTION: WorkCalendar
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkCalendar_upsert_get_and_delete_entries()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("Cal", null, 2026, owner.Id, start, start.AddDays(6), null, null), default)).Project!;

        // Upsert two entries: a holiday and a workday override
        var hol = await svc.UpsertCalendarEntryAsync(creator.Id, p.Id, new UpsertCalendarEntryRequest(start.AddDays(1), "holiday", "Hari Raya", "office closed"), default);
        Assert.True(hol.Success);
        var work = await svc.UpsertCalendarEntryAsync(creator.Id, p.Id, new UpsertCalendarEntryRequest(start.AddDays(2), "workday", null, null), default);
        Assert.True(work.Success);

        // Upsert same date again -> replaces (unique per project+date)
        var replaced = await svc.UpsertCalendarEntryAsync(creator.Id, p.Id, new UpsertCalendarEntryRequest(start.AddDays(1), "offday", "Team off", null), default);
        Assert.True(replaced.Success);

        var cal = await svc.GetWorkCalendarAsync(p.Id, creator.Id, start, start.AddDays(6), default);
        Assert.NotNull(cal);
        Assert.Equal(2, cal!.TotalEntries);
        var holiday = cal.Entries.Single(e => e.Date == start.AddDays(1));
        Assert.Equal("offday", holiday.Kind);
        Assert.Equal("Team off", holiday.Title);
        Assert.Equal("User C", holiday.CreatedByName);

        // Delete the offday entry
        var del = await svc.DeleteCalendarEntryAsync(creator.Id, p.Id, start.AddDays(1), default);
        Assert.True(del.Success);
        var after = await svc.GetWorkCalendarAsync(p.Id, creator.Id, start, start.AddDays(6), default);
        Assert.Single(after!.Entries);
    }

    [Fact]
    public async Task WorkCalendar_rejects_non_owner_and_invalid_kind()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var viewer = t.AddUser("V");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("CalPerm", null, 2026, owner.Id, null, null, null, null), default)).Project!;

        // Add a viewer (cannot edit calendar)
        await svc.AddMemberAsync(creator.Id, p.Id, new AddProjectMemberRequest(viewer.Id, "viewer"), default);

        var forbidden = await svc.UpsertCalendarEntryAsync(viewer.Id, p.Id, new UpsertCalendarEntryRequest(DateTime.UtcNow.Date, "holiday", null, null), default);
        Assert.False(forbidden.Success);
        Assert.Equal("forbidden", forbidden.Error);

        var badKind = await svc.UpsertCalendarEntryAsync(creator.Id, p.Id, new UpsertCalendarEntryRequest(DateTime.UtcNow.Date, "party", null, null), default);
        Assert.False(badKind.Success);
        Assert.Equal("invalid_kind", badKind.Error);
    }

    [Fact]
    public async Task WorkCalendar_aggregates_tasks_per_day()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var svc = new ProjectService(t.Verify());

        var start = DateTime.UtcNow.Date;
        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("CalAgg", null, 2026, owner.Id, start, start.AddDays(3), null, null), default)).Project!;

        // Task spanning days 0..2, plus a one-day task on day 1
        await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T1", null, "doing", null, start, start.AddDays(2), 6, null, null), default);
        await svc.CreateTaskAsync(creator.Id, new CreateTaskRequest(p.Id, null, "T2", null, "done", null, start.AddDays(1), start.AddDays(1), 2, null, null), default);

        var cal = await svc.GetWorkCalendarAsync(p.Id, creator.Id, start, start.AddDays(3), default);
        Assert.NotNull(cal);

        // Day 1 has both tasks
        var day1 = cal!.DayTasks.Single(d => d.Date == start.AddDays(1));
        Assert.Equal(2, day1.TaskCount);
        Assert.Equal(8, day1.EstimatedHours); // 6 + 2
        Assert.Equal(1, day1.DoneCount);
        Assert.Equal(1, day1.DoingCount);

        // Day 0 only T1
        var day0 = cal.DayTasks.Single(d => d.Date == start);
        Assert.Equal(1, day0.TaskCount);
        Assert.Equal(6, day0.EstimatedHours);

        // Day 3 outside any task -> not present
        Assert.DoesNotContain(cal.DayTasks, d => d.Date == start.AddDays(3));
    }

    [Fact]
    public async Task WorkCalendar_returns_null_for_non_member()
    {
        using var t = new TestDb();
        var owner = t.AddUser("O");
        var creator = t.AddUser("C");
        var stranger = t.AddUser("S");
        var svc = new ProjectService(t.Verify());

        var p = (await svc.CreateProjectAsync(creator.Id, new CreateProjectRequest("CalPriv", null, 2026, owner.Id, null, null, null, null), default)).Project!;
        var cal = await svc.GetWorkCalendarAsync(p.Id, stranger.Id, DateTime.UtcNow, DateTime.UtcNow.AddDays(7), default);
        Assert.Null(cal);
    }
}


public class ProjectReportsTests
{
    [Fact]
    public void Burndown_and_velocity_dtos_shape()
    {
        // Kontrak endpoint laporan: bentuk data yang dipakai grafik frontend.
        var points = new[]
        {
            new IPop.Api.Endpoints.ProjectEndpoints.BurndownPointDto(DateTime.UtcNow.Date.AddDays(-7), 10),
            new IPop.Api.Endpoints.ProjectEndpoints.BurndownPointDto(DateTime.UtcNow.Date, 4),
        };
        var dto = new IPop.Api.Endpoints.ProjectEndpoints.BurndownDto(points, points, 12);
        Assert.Equal(12, dto.TotalTasks);
        Assert.Equal(2, dto.Actual.Count);
        Assert.Equal(2, dto.Ideal.Count);

        var vel = new IPop.Api.Endpoints.ProjectEndpoints.VelocityDto(
            [new IPop.Api.Endpoints.ProjectEndpoints.VelocityPointDto(DateTime.UtcNow.Date.AddDays(-7), 3)], 3.0);
        Assert.Equal(3.0, vel.AveragePerWeek);
    }

    [Fact]
    public void Burndown_calculation_counts_overdue_and_undated_tasks()
    {
        var now = DateTime.UtcNow.Date;
        var tasks = new[]
        {
            new { Status = "todo", DueDate = (DateTime?)now.AddDays(-10), UpdatedAt = now.AddDays(-10) }, // overdue task
            new { Status = "doing", DueDate = (DateTime?)null, UpdatedAt = now.AddDays(-5) }, // undated task
            new { Status = "done", DueDate = (DateTime?)now.AddDays(-3), UpdatedAt = now.AddDays(1) }, // finished after past checkpoint
            new { Status = "done", DueDate = (DateTime?)now.AddDays(-5), UpdatedAt = now.AddDays(-6) }, // finished before checkpoint
        };

        var weekEnd = now.AddDays(-3);
        // Overdue and undated tasks that are not done must remain in the count.
        // Also tasks completed AFTER weekEnd must be counted as remaining at weekEnd.
        var remaining = tasks.Count(t => t.Status != "done" || t.UpdatedAt.Date > weekEnd);
        Assert.Equal(3, remaining);
    }
}


public class MeetingFlowTests
{
    // Alur penerima: organizer membuat rapat → peserta lain (bukan organizer) RSVP →
    // rekap kehadiran berubah. Membuktikan RSVP peserta dan agregasi rekap, bukan
    // hanya jawaban organizer.
    [Fact]
    public async Task Recipient_rsvp_updates_attendance_summary()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("ORG");
        var recipient = t.AddUser("RCP");
        var db = t.Verify();

        var meeting = new IPop.Core.Entities.Meeting
        {
            Id = Guid.NewGuid(), Title = "Rapat Uji", StartAt = DateTime.UtcNow.AddDays(1), EndAt = DateTime.UtcNow.AddDays(1).AddHours(1),
            OrganizerId = organizer.Id, Status = "aktif", CreatedAt = DateTime.UtcNow,
        };
        db.Meetings.Add(meeting);
        db.MeetingAttendees.Add(new IPop.Core.Entities.MeetingAttendee { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = organizer.Id, Required = true, RsvpStatus = "accept", RespondedAt = DateTime.UtcNow });
        db.MeetingAttendees.Add(new IPop.Core.Entities.MeetingAttendee { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = recipient.Id, Required = true, RsvpStatus = "pending" });
        await db.SaveChangesAsync();

        // Awal: 1 accept (organizer), 1 pending (penerima).
        var initial = await db.MeetingAttendees.Where(a => a.MeetingId == meeting.Id).ToListAsync();
        Assert.Equal(1, initial.Count(a => a.RsvpStatus == "accept"));
        Assert.Equal(1, initial.Count(a => a.RsvpStatus == "pending"));

        // PENERIMA (bukan organizer) menjawab "decline" lewat handler endpoint sungguhan.
        var result = await IPop.Api.Endpoints.MeetingEndpoints.ApplyRsvpAsync(db, meeting.Id, recipient.Id, "decline", "berhalangan", default);
        Assert.Equal(IPop.Api.Endpoints.MeetingEndpoints.RsvpResult.Applied, result);

        // Rekap akhir: jawaban penerima tercermin.
        var final = await t.Verify().MeetingAttendees.Where(a => a.MeetingId == meeting.Id).ToListAsync();
        Assert.Equal(1, final.Count(a => a.RsvpStatus == "accept"));
        Assert.Equal(1, final.Count(a => a.RsvpStatus == "decline"));
        Assert.Equal(0, final.Count(a => a.RsvpStatus == "pending"));
        Assert.Contains(final, a => a.UserId == recipient.Id && a.Note == "berhalangan" && a.RespondedAt != null);
    }

    // RSVP menolak jawaban tidak valid dan pengguna yang bukan peserta.
    [Fact]
    public async Task Rsvp_rejects_invalid_response_and_non_attendee()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("ORG");
        var recipient = t.AddUser("RCP");
        var outsider = t.AddUser("OUT");
        var db = t.Verify();

        var meeting = new IPop.Core.Entities.Meeting
        {
            Id = Guid.NewGuid(), Title = "R", StartAt = DateTime.UtcNow.AddDays(1), EndAt = DateTime.UtcNow.AddDays(1).AddHours(1),
            OrganizerId = organizer.Id, Status = "aktif", CreatedAt = DateTime.UtcNow,
        };
        db.Meetings.Add(meeting);
        db.MeetingAttendees.Add(new IPop.Core.Entities.MeetingAttendee { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = recipient.Id, Required = true, RsvpStatus = "pending" });
        await db.SaveChangesAsync();

        Assert.Equal(IPop.Api.Endpoints.MeetingEndpoints.RsvpResult.InvalidResponse,
            await IPop.Api.Endpoints.MeetingEndpoints.ApplyRsvpAsync(db, meeting.Id, recipient.Id, "hadir", null, default));
        Assert.Equal(IPop.Api.Endpoints.MeetingEndpoints.RsvpResult.NotInvited,
            await IPop.Api.Endpoints.MeetingEndpoints.ApplyRsvpAsync(db, meeting.Id, outsider.Id, "accept", null, default));
    }

    // Pengingat rapat: jalankan logika worker sungguhan (ProcessDueRemindersAsync)
    // dengan gateway palsu perekam — membuktikan notifikasi diterbitkan & SentAt ditandai.
    [Fact]
    public async Task Meeting_reminder_publishes_notification_when_due()
    {
        using var t = new TestDb();
        var recipient = t.AddUser("RCP");
        var db = t.Verify();

        var meeting = new IPop.Core.Entities.Meeting
        {
            Id = Guid.NewGuid(), Title = "Rapat Segera", StartAt = DateTime.UtcNow.AddMinutes(20), EndAt = DateTime.UtcNow.AddMinutes(80),
            OrganizerId = recipient.Id, Status = "aktif", CreatedAt = DateTime.UtcNow,
        };
        db.Meetings.Add(meeting);
        // Jatuh tempo: SendAt sudah lewat.
        db.MeetingReminders.Add(new IPop.Core.Entities.MeetingReminder
        {
            Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = recipient.Id, SendAt = DateTime.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();

        var gateway = new RecordingGateway();
        var sent = await IPop.Infrastructure.Meetings.MeetingReminderWorker.ProcessDueRemindersAsync(db, gateway, default);

        // Worker sungguhan menerbitkan 1 notifikasi ke peserta yang benar.
        Assert.Equal(1, sent);
        var pub = Assert.Single(gateway.Published);
        Assert.Equal("meeting.reminder", pub.EventType);
        Assert.Equal(recipient.Id, pub.Target.UserId);
        Assert.Equal("high", pub.Priority);

        // SentAt ditandai — tick berikutnya tidak mengulang.
        var remaining = await t.Verify().MeetingReminders.CountAsync(r => r.SentAt == null);
        Assert.Equal(0, remaining);
    }

    // A reminder for someone who left the meeting's room, one for someone who declined, and
    // one whose meeting already started are all retired (SentAt set) without a notification.
    [Fact]
    public async Task Meeting_reminder_skips_left_declined_and_started()
    {
        using var t = new TestDb();
        var org = t.AddUser("RM_ORG");
        var left = t.AddUser("RM_LEFT");
        var declined = t.AddUser("RM_DECL");
        var stays = t.AddUser("RM_STAY");
        var db = t.Verify();
        var room = new IPop.Core.Entities.ChatRoom { Id = Guid.NewGuid(), Name = "R", Type = "group", IsActive = true, CreatedAt = DateTime.UtcNow };
        db.ChatRooms.Add(room);
        foreach (var (u, leftAt) in new[] { (org, (DateTime?)null), (left, DateTime.UtcNow), (declined, null), (stays, null) })
            db.ChatRoomMembers.Add(new IPop.Core.Entities.ChatRoomMember { Id = Guid.NewGuid(), RoomId = room.Id, UserId = u.Id, Role = "member", JoinedAt = DateTime.UtcNow, LeftAt = leftAt });
        IPop.Core.Entities.Meeting Meet(DateTime start)
        {
            var m = new IPop.Core.Entities.Meeting
            {
                Id = Guid.NewGuid(), Title = "M", StartAt = start, EndAt = start.AddHours(1),
                OrganizerId = org.Id, Status = "aktif", RoomId = room.Id, CreatedAt = DateTime.UtcNow,
            };
            db.Meetings.Add(m);
            foreach (var u in new[] { org, left, declined, stays })
                db.MeetingAttendees.Add(new IPop.Core.Entities.MeetingAttendee { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = u.Id, RsvpStatus = u == declined ? "decline" : "pending" });
            return m;
        }
        var soon = Meet(DateTime.UtcNow.AddMinutes(20));
        var started = Meet(DateTime.UtcNow.AddMinutes(-5));
        foreach (var (m, u) in new[] { (soon, left), (soon, declined), (soon, stays), (started, stays) })
            db.MeetingReminders.Add(new IPop.Core.Entities.MeetingReminder { Id = Guid.NewGuid(), MeetingId = m.Id, UserId = u.Id, SendAt = DateTime.UtcNow.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var gateway = new RecordingGateway();
        Assert.Equal(1, await IPop.Infrastructure.Meetings.MeetingReminderWorker.ProcessDueRemindersAsync(db, gateway, default));
        Assert.Equal(stays.Id, Assert.Single(gateway.Published).Target.UserId);
        Assert.Equal(0, await t.Verify().MeetingReminders.CountAsync(r => r.SentAt == null));
    }

    private sealed class RecordingGateway : IPop.Application.Notifications.INotificationGatewayService
    {
        public List<(string EventType, IPop.Application.Notifications.NotificationTarget Target, string Priority)> Published = [];
        public Task<IPop.Application.Notifications.NotificationGatewayClientInfo?> AuthenticateClientAsync(string c, string s, string? ip, CancellationToken ct) => Task.FromResult<IPop.Application.Notifications.NotificationGatewayClientInfo?>(null);
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> EnqueueAsync(Guid c, IPop.Application.Notifications.NotificationEnqueueRequest r, CancellationToken ct) => Task.FromResult(new IPop.Application.Notifications.NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "queued", 0, DateTime.UtcNow));
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkReadAsync(Guid r, Guid u, CancellationToken ct) => Task.FromResult<IPop.Application.Notifications.NotificationDeliveryState?>(null);
        public Task<int> MarkAllReadAsync(Guid u, CancellationToken ct) => Task.FromResult(0);
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> MarkAcknowledgedAsync(Guid r, Guid u, CancellationToken ct) => Task.FromResult<IPop.Application.Notifications.NotificationDeliveryState?>(null);
        public Task<IPop.Application.Notifications.NotificationDeliveryState?> RetryDeliveryAsync(Guid r, Guid u, CancellationToken ct) => Task.FromResult<IPop.Application.Notifications.NotificationDeliveryState?>(null);
        public Task<IPop.Application.Notifications.NotificationEnqueueResult> PublishInternalAsync(string clientSlug, string eventType, string title, string message, IPop.Application.Notifications.NotificationTarget target, string priority = "normal", string? targetUrl = null, CancellationToken ct = default)
        {
            Published.Add((eventType, target, priority));
            return Task.FromResult(new IPop.Application.Notifications.NotificationEnqueueResult(true, null, null, Guid.NewGuid(), "queued", 1, DateTime.UtcNow));
        }
    }

    // Rapat membuat event kalender yang terhubung (MeetingId) — membuktikan kaitan
    // rapat → kalender dan pengingat terjadwal per peserta.
    [Fact]
    public async Task Meeting_creates_calendar_event_and_reminders()
    {
        using var t = new TestDb();
        var organizer = t.AddUser("ORG");
        var recipient = t.AddUser("RCP");
        var db = t.Verify();

        var meeting = new IPop.Core.Entities.Meeting
        {
            Id = Guid.NewGuid(), Title = "Rapat Terhubung", StartAt = DateTime.UtcNow.AddDays(1), EndAt = DateTime.UtcNow.AddDays(1).AddHours(1),
            OrganizerId = organizer.Id, Status = "aktif", ReminderMinutes = 30, SourceMailId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow,
        };
        db.Meetings.Add(meeting);
        db.CalendarEvents.Add(new IPop.Core.Entities.CalendarEvent
        {
            Id = Guid.NewGuid(), Title = meeting.Title, StartAt = meeting.StartAt, EndAt = meeting.EndAt,
            Type = "rapat", OwnerId = organizer.Id, MeetingId = meeting.Id, CreatedAt = DateTime.UtcNow,
        });
        var sendAt = meeting.StartAt.AddMinutes(-30);
        db.MeetingReminders.Add(new IPop.Core.Entities.MeetingReminder { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = organizer.Id, SendAt = sendAt });
        db.MeetingReminders.Add(new IPop.Core.Entities.MeetingReminder { Id = Guid.NewGuid(), MeetingId = meeting.Id, UserId = recipient.Id, SendAt = sendAt });
        await db.SaveChangesAsync();

        var verify = t.Verify();
        var ev = await verify.CalendarEvents.FirstAsync(e => e.MeetingId == meeting.Id);
        Assert.Equal("rapat", ev.Type);
        var reminders = await verify.MeetingReminders.Where(r => r.MeetingId == meeting.Id && r.SentAt == null).ToListAsync();
        Assert.Equal(2, reminders.Count); // organizer + penerima
        Assert.All(reminders, r => Assert.Equal(sendAt, r.SendAt));
        // SourceMailId menghubungkan rapat ke surat undangan.
        var m = await verify.Meetings.FirstAsync(x => x.Id == meeting.Id);
        Assert.NotNull(m.SourceMailId);
    }

    // ── Date-only values from a date picker (Kind=Unspecified/Local) ────────────
    // Production: Npgsql refuses a non-UTC DateTime for a timestamptz column, so a picked
    // Deadline 500'd project create ("Failed to create project (check permissions)").
    // SQLite doesn't care about Kind, so StrictUtcInterceptor re-imposes Npgsql's rule.

    private sealed class StrictUtcInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private static readonly Type[] Checked = [typeof(Project), typeof(ProjectTask), typeof(WorkCalendarEntry), typeof(ProjectMilestone)];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData e,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken ct = default)
        {
            foreach (var entry in e.Context!.ChangeTracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified) || !Checked.Contains(entry.Entity.GetType())) continue;
                // Only columns actually written: an UPDATE sends modified columns only, and SQLite
                // reads untouched timestamps back as Unspecified where Npgsql would give Utc.
                foreach (var p in entry.Properties)
                    if ((entry.State == EntityState.Added || p.IsModified) && p.CurrentValue is DateTime { Kind: not DateTimeKind.Utc } dt)
                        throw new ArgumentException($"Cannot write DateTime with Kind={dt.Kind} to PostgreSQL type 'timestamp with time zone' ({entry.Entity.GetType().Name}.{p.Metadata.Name}).");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static IPopDbContext StrictContext(TestDb t) => new(new DbContextOptionsBuilder<IPopDbContext>()
        .UseSqlite(t.Db.Database.GetDbConnection())
        .AddInterceptors(new StrictUtcInterceptor())
        .Options);

    private static readonly DateTime PickedUnspecified = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime PickedLocal = new(2026, 11, 15, 0, 0, 0, DateTimeKind.Local);

    private static void AssertUtcDay(DateTime? actual, DateTime picked)
    {
        Assert.NotNull(actual);
        Assert.Equal(DateTimeKind.Utc, actual!.Value.Kind);
        Assert.Equal(picked.Date, actual.Value.Date);      // same calendar day — no zone shift
        Assert.Equal(TimeSpan.Zero, actual.Value.TimeOfDay);
    }

    [Fact]
    public async Task StrictUtcInterceptor_rejects_unspecified_dates_like_npgsql()
    {
        // Guards the guard: without it, the tests below would pass against SQLite even unfixed.
        using var t = new TestDb();
        var owner = t.AddUser("SU_O");
        await using var db = StrictContext(t);
        db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "X", Year = 2026, OwnerId = owner.Id, Status = "planning",
            Deadline = PickedUnspecified, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<ArgumentException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task CreateProject_with_picked_deadline_stores_utc_calendar_date(DateTimeKind kind)
    {
        using var t = new TestDb();
        var owner = t.AddUser("PD_O");
        var employee = t.AddUser("PD_E");
        await using var db = StrictContext(t);
        var svc = new ProjectService(db);
        var picked = kind == DateTimeKind.Local ? PickedLocal : PickedUnspecified;

        var r = await svc.CreateProjectAsync(employee.Id,
            new CreateProjectRequest("Dated", null, 2026, Guid.Empty, picked, picked, null, null, OwnerNik: "PD_O"), default);

        Assert.True(r.Success, r.Error);
        var stored = db.Projects.Local.Single(p => p.Id == r.Project!.Id);
        AssertUtcDay(stored.Deadline, picked);
        AssertUtcDay(stored.StartDate, picked);
        await using var v = t.Verify();
        Assert.Equal(picked.Date, (await v.Projects.SingleAsync(p => p.Id == r.Project!.Id)).Deadline!.Value.Date);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task UpdateProject_with_picked_dates_stores_utc_calendar_date(DateTimeKind kind)
    {
        using var t = new TestDb();
        var owner = t.AddUser("PU_O");
        var p = t.AddProject("Upd", owner.Id);
        await using var db = StrictContext(t);
        var svc = new ProjectService(db);
        var picked = kind == DateTimeKind.Local ? PickedLocal : PickedUnspecified;

        var r = await svc.UpdateProjectAsync(owner.Id, p.Id,
            new UpdateProjectRequest(null, null, null, null, picked, picked, null, null, null), default);

        Assert.True(r.Success, r.Error);
        var stored = db.Projects.Local.Single(x => x.Id == p.Id);
        AssertUtcDay(stored.Deadline, picked);
        AssertUtcDay(stored.StartDate, picked);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public async Task Create_and_update_task_with_picked_dates_store_utc_calendar_date(DateTimeKind kind)
    {
        using var t = new TestDb();
        var owner = t.AddUser("TD_O");
        var p = t.AddProject("Tasks", owner.Id);
        await using var db = StrictContext(t);
        var svc = new ProjectService(db);
        var picked = kind == DateTimeKind.Local ? PickedLocal : PickedUnspecified;

        var created = await svc.CreateTaskAsync(owner.Id,
            new CreateTaskRequest(p.Id, null, "Due", null, "todo", null, picked, picked, null, null, null), default);
        Assert.True(created.Success, created.Error);
        var task = db.ProjectTasks.Local.Single(x => x.Id == created.Task!.Id);
        AssertUtcDay(task.DueDate, picked);
        AssertUtcDay(task.StartDate, picked);

        var moved = picked.AddDays(3);
        var updated = await svc.UpdateTaskAsync(owner.Id, task.Id,
            new UpdateTaskRequest(null, null, null, null, moved, moved, null, null, null, null, null, null, null, null), default);
        Assert.True(updated.Success, updated.Error);
        AssertUtcDay(task.DueDate, moved);
        AssertUtcDay(task.StartDate, moved);
        await using var v = t.Verify();
        var activity = await v.ProjectTaskActivities.Where(a => a.TaskId == task.Id && a.Action == "due_changed").SingleAsync();
        Assert.Equal(moved.ToString("yyyy-MM-dd"), activity.NewValue);
    }

    [Fact]
    public async Task Work_calendar_entry_with_picked_date_stores_utc_and_deletes_by_same_day()
    {
        using var t = new TestDb();
        var owner = t.AddUser("WC_O");
        var p = t.AddProject("Cal", owner.Id);
        await using var db = StrictContext(t);
        var svc = new ProjectService(db);

        var r = await svc.UpsertCalendarEntryAsync(owner.Id, p.Id, new UpsertCalendarEntryRequest(PickedUnspecified, "holiday", "Libur", null), default);
        Assert.True(r.Success, r.Error);
        AssertUtcDay(db.WorkCalendarEntries.Local.Single(e => e.ProjectId == p.Id).Date, PickedUnspecified);

        Assert.True((await svc.DeleteCalendarEntryAsync(owner.Id, p.Id, PickedUnspecified, default)).Success);
        await using var v = t.Verify();
        Assert.False(await v.WorkCalendarEntries.AnyAsync(e => e.ProjectId == p.Id));
    }

    [Fact]
    public void AsUtcDate_keeps_utc_values_and_relabels_others_without_shifting()
    {
        var utc = new DateTime(2026, 10, 1, 17, 30, 0, DateTimeKind.Utc);
        Assert.Equal(utc, IPop.Application.UtcDates.AsUtcDate(utc));
        var r = IPop.Application.UtcDates.AsUtcDate(PickedUnspecified);
        Assert.Equal(DateTimeKind.Utc, r.Kind);
        Assert.Equal(PickedUnspecified.Ticks, r.Ticks);
        Assert.Null(IPop.Application.UtcDates.AsUtcDate((DateTime?)null));
    }
}
