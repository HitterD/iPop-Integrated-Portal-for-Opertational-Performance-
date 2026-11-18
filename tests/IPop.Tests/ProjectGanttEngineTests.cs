using System.Text;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Shared fixture for the stage 3 Gantt service tests.
public sealed class GanttFixture : IDisposable
{
    // 2026-11-02 is a Monday.
    public static DateTime D(int day) => new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc);
    public readonly TestDb T = new();
    public readonly ProjectService Svc;
    public readonly Guid Owner, Member, Viewer, Outsider, ProjectId;

    public GanttFixture()
    {
        Owner = T.AddUser("G_O").Id; Member = T.AddUser("G_M").Id; Viewer = T.AddUser("G_V").Id; Outsider = T.AddUser("G_X").Id;
        Svc = new ProjectService(T.Verify());
        ProjectId = Svc.CreateProjectAsync(Owner, new CreateProjectRequest("Gantt QA", null, 2026, Owner, D(2), D(30), null, null), default).Result.Project!.Id;
        Svc.AddMemberAsync(Owner, ProjectId, new AddProjectMemberRequest(Member, "member"), default).Wait();
        Svc.AddMemberAsync(Owner, ProjectId, new AddProjectMemberRequest(Viewer, "viewer"), default).Wait();
    }

    public GanttService G() => new(T.Verify());

    public async Task<Guid> TaskAsync(string title, int? s, int? d, Guid? group = null, string status = "todo", Guid? parent = null)
    {
        var r = await Svc.CreateTaskAsync(Owner, new CreateTaskRequest(ProjectId, parent, title, null, status, null,
            s is null ? null : D(s.Value), d is null ? null : D(d.Value), null, null, null, group), default);
        Assert.True(r.Success, r.Error);
        return r.Task!.Id;
    }

    public async Task LinkAsync(Guid pred, Guid succ, string type = "FS", int lag = 0)
    {
        var existing = (await Svc.GetTaskAsync(succ, Owner, default))!.DependsOn ?? [];
        var links = existing.Select(x => new TaskLinkRequest(x.Id, x.LinkType, x.LagDays)).Append(new TaskLinkRequest(pred, type, lag)).ToList();
        var r = await Svc.SetTaskDependenciesAsync(Owner, succ, new SetTaskDependenciesRequest([], links), default);
        Assert.True(r.Success, r.Error);
    }

    public async Task<(DateTime? S, DateTime? D)> DatesAsync(Guid id)
    {
        using var v = T.Verify();
        var t = await v.ProjectTasks.AsNoTracking().SingleAsync(x => x.Id == id);
        return (t.StartDate, t.DueDate);
    }

    public void Dispose() => T.Dispose();
}

public sealed class ProjectLinkTypeTests
{
    [Fact]
    public async Task Links_persist_type_and_lag_and_legacy_DependsOn_stays_FS_zero()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3); var b = await f.TaskAsync("B", 4, 5); var c = await f.TaskAsync("C", 6, 6);
        await f.Svc.SetTaskDependenciesAsync(f.Owner, c, new SetTaskDependenciesRequest([a]), default);
        await f.LinkAsync(a, b, "SS", 2);
        var bDto = (await f.Svc.GetTaskAsync(b, f.Owner, default))!;
        var link = Assert.Single(bDto.DependsOn!);
        Assert.Equal(("SS", 2), (link.LinkType, link.LagDays));
        var cLink = Assert.Single((await f.Svc.GetTaskAsync(c, f.Owner, default))!.DependsOn!);
        Assert.Equal(("FS", 0), (cLink.LinkType, cLink.LagDays));

        // Changing only the type/lag of an existing link updates it.
        await f.Svc.SetTaskDependenciesAsync(f.Owner, b, new SetTaskDependenciesRequest([], [new TaskLinkRequest(a, "FF", -1)]), default);
        var changed = Assert.Single((await f.Svc.GetTaskAsync(b, f.Owner, default))!.DependsOn!);
        Assert.Equal(("FF", -1), (changed.LinkType, changed.LagDays));
    }

    [Fact]
    public async Task Rejects_bad_link_type_lag_and_cycles()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3); var b = await f.TaskAsync("B", 4, 5);
        Assert.Equal("invalid_link_type", (await f.Svc.SetTaskDependenciesAsync(f.Owner, b, new SetTaskDependenciesRequest([], [new TaskLinkRequest(a, "SF")]), default)).Error);
        Assert.Equal("invalid_link_type", (await f.Svc.SetTaskDependenciesAsync(f.Owner, b, new SetTaskDependenciesRequest([], [new TaskLinkRequest(a, "XX")]), default)).Error);
        Assert.Equal("invalid_lag", (await f.Svc.SetTaskDependenciesAsync(f.Owner, b, new SetTaskDependenciesRequest([], [new TaskLinkRequest(a, "FS", 366)]), default)).Error);
        await f.LinkAsync(a, b);
        Assert.Equal("dependency_cycle", (await f.Svc.SetTaskDependenciesAsync(f.Owner, a, new SetTaskDependenciesRequest([], [new TaskLinkRequest(b, "SS")]), default)).Error);
    }

    [Fact]
    public async Task Constraint_and_deadline_update_validates_and_clears()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        UpdateTaskRequest Req(string? type = null, DateTime? cd = null, DateTime? dl = null, bool clearC = false, bool clearD = false) =>
            new(null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                DeadlineDate: dl, ConstraintType: type, ConstraintDate: cd, ClearDeadline: clearD, ClearConstraint: clearC);

        Assert.Equal("invalid_constraint", (await f.Svc.UpdateTaskAsync(f.Owner, a, Req("bogus", GanttFixture.D(5)), default)).Error);
        Assert.Equal("invalid_constraint", (await f.Svc.UpdateTaskAsync(f.Owner, a, Req("snet"), default)).Error);
        var ok = await f.Svc.UpdateTaskAsync(f.Owner, a, Req("snet", GanttFixture.D(4), GanttFixture.D(20)), default);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(("snet", GanttFixture.D(4), GanttFixture.D(20)), (ok.Task!.ConstraintType, ok.Task.ConstraintDate, ok.Task.DeadlineDate));
        var cleared = await f.Svc.UpdateTaskAsync(f.Owner, a, Req(clearC: true, clearD: true), default);
        Assert.Null(cleared.Task!.ConstraintType); Assert.Null(cleared.Task.DeadlineDate);
    }
}

public sealed class ProjectBaselineTests
{
    [Fact]
    public async Task Create_snapshots_dates_auto_names_and_activates_latest()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 4); await f.TaskAsync("Undated", null, null);
        var g = f.G();
        var b1 = (await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default)).Value!;
        Assert.Equal(("Plan 1", 2, true), (b1.Name, b1.TaskCount, b1.IsActive));
        var b2 = (await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(" Re-plan "), default)).Value!;
        Assert.Equal("Re-plan", b2.Name);
        var list = (await g.ListBaselinesAsync(f.Viewer, f.ProjectId, default)).Value!;
        Assert.Equal(new[] { false, true }, list.Select(x => x.IsActive));

        await g.ActivateBaselineAsync(f.Owner, f.ProjectId, b1.Id, default);
        Assert.Equal(b1.Id, Assert.Single((await g.ListBaselinesAsync(f.Owner, f.ProjectId, default)).Value!, x => x.IsActive).Id);
    }

    [Fact]
    public async Task Limit_of_five_and_delete_promotes_another()
    {
        using var f = new GanttFixture();
        await f.TaskAsync("A", 2, 3);
        var g = f.G();
        for (var i = 0; i < 5; i++) Assert.True((await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default)).Success);
        Assert.Equal("too_many_baselines", (await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default)).Error);
        var list = (await g.ListBaselinesAsync(f.Owner, f.ProjectId, default)).Value!;
        var active = list.Single(x => x.IsActive);
        Assert.True((await g.DeleteBaselineAsync(f.Owner, f.ProjectId, active.Id, default)).Success);
        var after = (await g.ListBaselinesAsync(f.Owner, f.ProjectId, default)).Value!;
        Assert.Equal(4, after.Count); Assert.Single(after, x => x.IsActive);
        Assert.Equal("baseline_not_found", (await g.DeleteBaselineAsync(f.Owner, f.ProjectId, active.Id, default)).Error);
    }

    [Fact]
    public async Task Permissions_owner_manager_only_for_writes_members_read()
    {
        using var f = new GanttFixture();
        await f.TaskAsync("A", 2, 3);
        var g = f.G();
        Assert.Equal("forbidden", (await g.CreateBaselineAsync(f.Member, f.ProjectId, new CreateBaselineRequest(), default)).Error);
        Assert.Equal("forbidden", (await g.CreateBaselineAsync(f.Viewer, f.ProjectId, new CreateBaselineRequest(), default)).Error);
        var b = (await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default)).Value!;
        Assert.Equal("forbidden", (await g.ActivateBaselineAsync(f.Member, f.ProjectId, b.Id, default)).Error);
        Assert.Equal("forbidden", (await g.DeleteBaselineAsync(f.Viewer, f.ProjectId, b.Id, default)).Error);
        Assert.True((await g.ListBaselinesAsync(f.Viewer, f.ProjectId, default)).Success);
        Assert.False((await g.ListBaselinesAsync(f.Outsider, f.ProjectId, default)).Success);
        Assert.Equal("project_not_found", (await g.CreateBaselineAsync(f.Owner, Guid.NewGuid(), new CreateBaselineRequest(), default)).Error);
    }

    [Fact]
    public async Task Variance_is_finish_vs_baseline_finish_in_working_days_on_v1_and_v2()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 4);              // Mon-Wed
        var g = f.G();
        await g.CreateBaselineAsync(f.Owner, f.ProjectId, new CreateBaselineRequest(), default);
        // Slip finish from Wed 4 to Mon 9 (Thu, Fri, Mon = 3 working days).
        await f.Svc.UpdateTaskAsync(f.Owner, a, new UpdateTaskRequest(null, null, null, null, null, GanttFixture.D(9), null, null, null, null, null, null, null, null), default);

        var v2 = (await g.GetGanttV2Async(f.Owner, f.ProjectId, null, null, default)).Value!;
        var row = v2.Rows.Single(r => r.Id == a);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(4), 3), (row.BaselineStart, row.BaselineDue, row.VarianceDays));

        var v1 = await g.WithBaselineAsync((await f.Svc.GetGanttAsync(f.ProjectId, f.Owner, default))!, null, default);
        var t = v1.Tasks.Single(x => x.Id == a);
        Assert.Equal((GanttFixture.D(4), 3), (t.BaselineDue, t.VarianceDays));
    }
}

public sealed class GanttV2Tests
{
    [Fact]
    public async Task Rows_carry_wbs_slack_critical_links_status_and_percent()
    {
        using var f = new GanttFixture();
        var g1 = (await f.Svc.GetGroupsAsync(f.ProjectId, f.Owner, default))!.First().Id;
        var a = await f.TaskAsync("A", 2, 2, g1);
        var x = await f.TaskAsync("X", 3, 5, g1);
        var y = await f.TaskAsync("Y", 3, 3, g1);
        var z = await f.TaskAsync("Z", 6, 6, g1);
        await f.LinkAsync(a, x); await f.LinkAsync(a, y, "FS", 0); await f.LinkAsync(x, z); await f.LinkAsync(y, z);
        var sub1 = await f.TaskAsync("A.1", null, null, g1, "done", a);
        await f.TaskAsync("A.2", null, null, g1, "todo", a);
        await f.Svc.AssignUserAsync(f.Owner, x, f.Member, null, default);

        var r = await f.G().GetGanttV2Async(f.Viewer, f.ProjectId, null, null, default);
        Assert.True(r.Success, r.Error);
        var v = r.Value!;
        Assert.Equal("Gantt QA", v.Project.Name);
        string Wbs(Guid id) => v.Rows.Single(q => q.Id == id).WbsPath;
        Assert.Equal(new[] { "1.1", "1.1.1", "1.1.2", "1.2", "1.3", "1.4" }, v.Rows.Select(q => q.WbsPath));
        Assert.Equal("1.1.1", Wbs(sub1));
        Assert.Equal(50, v.Rows.Single(q => q.Id == a).PercentDone);
        Assert.Equal(100, v.Rows.Single(q => q.Id == sub1).PercentDone);
        Assert.True(v.Rows.Single(q => q.Id == x).IsCritical);
        Assert.Equal(0, v.Rows.Single(q => q.Id == x).SlackDays);
        Assert.Equal(2, v.Rows.Single(q => q.Id == y).SlackDays);
        Assert.Equal(3, v.Rows.Single(q => q.Id == x).DurationDays);
        Assert.Equal(new[] { a, x, z }, v.CriticalPathTaskIds);
        Assert.Equal(GanttFixture.D(6), v.ProjectFinish);
        Assert.Equal(("FS", 0), (v.Rows.Single(q => q.Id == z).DependsOn.First().LinkType, v.Rows.Single(q => q.Id == z).DependsOn.First().LagDays));
        Assert.Equal(f.Member, Assert.Single(v.Rows.Single(q => q.Id == x).Assignees).UserId);
        Assert.Equal(("To do", "todo"), (v.Rows.Single(q => q.Id == a).Status, v.Rows.Single(q => q.Id == a).StageStatus));
        Assert.Empty(v.Conflicts);
        Assert.Single(v.Groups);
        Assert.False(v.Rows.Any(q => q.IsMilestoneRow));
    }

    [Fact]
    public async Task Holidays_milestones_conflicts_and_access()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 4); var b = await f.TaskAsync("B", 3, 5);
        await f.LinkAsync(a, b);
        await f.Svc.UpsertCalendarEntryAsync(f.Owner, f.ProjectId, new UpsertCalendarEntryRequest(GanttFixture.D(11), "holiday", "Founders day", null), default);
        await f.Svc.UpsertCalendarEntryAsync(f.Owner, f.ProjectId, new UpsertCalendarEntryRequest(GanttFixture.D(12), "workday", null, null), default);
        var ms = new MilestoneService(f.T.Verify());
        await ms.CreateAsync(f.Owner, f.ProjectId, new CreateMilestoneRequest("Go-live", GanttFixture.D(20), [b]), GanttFixture.D(2), default);

        var v = (await f.G().GetGanttV2Async(f.Member, f.ProjectId, null, null, default)).Value!;
        Assert.Equal(GanttFixture.D(11), Assert.Single(v.Holidays).Date);
        Assert.Equal("Founders day", v.Holidays[0].Title);
        var m = Assert.Single(v.Milestones);
        Assert.Equal(new[] { b }, m.LinkedTaskIds);
        Assert.Equal(new GanttConflictDto(a, b), Assert.Single(v.Conflicts));
        Assert.False((await f.G().GetGanttV2Async(f.Outsider, f.ProjectId, null, null, default)).Success);
    }

    [Fact]
    public async Task Weekend_gap_does_not_count_as_slack_and_group_filter_works()
    {
        using var f = new GanttFixture();
        var g1 = (await f.Svc.GetGroupsAsync(f.ProjectId, f.Owner, default))!.First().Id;
        var g2 = (await f.Svc.CreateGroupAsync(f.Owner, f.ProjectId, new CreateProjectGroupRequest("Phase 2"), default)).Group!.Id;
        var a = await f.TaskAsync("A", 5, 6, g1);        // Thu-Fri
        var b = await f.TaskAsync("B", 9, 9, g2);        // Mon after the weekend
        await f.LinkAsync(a, b);
        var all = (await f.G().GetGanttV2Async(f.Owner, f.ProjectId, null, null, default)).Value!;
        Assert.Equal(0, all.Rows.Single(r => r.Id == a).SlackDays);
        Assert.Equal(new[] { "1.1", "2.1" }, all.Rows.Select(r => r.WbsPath));
        var only = (await f.G().GetGanttV2Async(f.Owner, f.ProjectId, null, g2, default)).Value!;
        Assert.Equal(b, Assert.Single(only.Rows).Id);
    }

    [Fact]
    public async Task V1_gantt_still_works_with_link_types()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3); var b = await f.TaskAsync("B", 4, 5);
        await f.LinkAsync(a, b, "FS", 0);
        var v1 = (await f.Svc.GetGanttAsync(f.ProjectId, f.Owner, default))!;
        Assert.Equal(new[] { a, b }, v1.CriticalPathTaskIds);
    }
}

public sealed class GanttRescheduleTests
{
    private static RescheduleRequest Move(Guid id, int day, string mode = "cascade", int? due = null) =>
        new(id, GanttFixture.D(day), mode, due is null ? null : GanttFixture.D(due.Value));

    [Fact]
    public async Task Preview_does_not_write_apply_does_with_activity_and_revert_undoes()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3); var b = await f.TaskAsync("B", 4, 5);
        await f.LinkAsync(a, b);
        var g = f.G();

        var p = (await g.PreviewRescheduleAsync(f.Member, f.ProjectId, Move(a, 3), default)).Value!;
        Assert.Equal(2, p.Changes.Count);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(a));

        var applied = (await g.ApplyRescheduleAsync(f.Member, f.ProjectId, Move(a, 3), default)).Value!;
        Assert.Equal((GanttFixture.D(3), GanttFixture.D(4)), await f.DatesAsync(a));
        Assert.Equal((GanttFixture.D(5), GanttFixture.D(6)), await f.DatesAsync(b));
        using (var v = f.T.Verify())
            Assert.Equal(2, await v.ProjectTaskActivities.CountAsync(x => x.Action == "rescheduled"));

        var back = await g.RevertRescheduleAsync(f.Member, f.ProjectId,
            new RescheduleRevertRequest(applied.Changes.Select(c => new RevertChangeDto(c.TaskId, c.OldStart, c.OldDue)).ToList()), default);
        Assert.Equal(2, back.Value!.Reverted);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(a));
        Assert.Equal((GanttFixture.D(4), GanttFixture.D(5)), await f.DatesAsync(b));
    }

    [Fact]
    public async Task Single_mode_leaves_successors_and_reports_conflict()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3); var b = await f.TaskAsync("B", 4, 5);
        await f.LinkAsync(a, b);
        var r = (await f.G().ApplyRescheduleAsync(f.Owner, f.ProjectId, Move(a, 4, "single"), default)).Value!;
        Assert.Single(r.Changes);
        Assert.Equal(new GanttConflictDto(a, b), Assert.Single(r.Conflicts));
        Assert.Equal((GanttFixture.D(4), GanttFixture.D(5)), await f.DatesAsync(b));
    }

    [Fact]
    public async Task Resize_with_new_due_and_mso_violation()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        var r = (await f.G().ApplyRescheduleAsync(f.Owner, f.ProjectId, Move(a, 2, "single", 6), default)).Value!;
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(6)), await f.DatesAsync(a));
        Assert.Equal(GanttFixture.D(6), r.NewProjectFinish);

        await f.Svc.UpdateTaskAsync(f.Owner, a, new UpdateTaskRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            ConstraintType: "mso", ConstraintDate: GanttFixture.D(2)), default);
        Assert.Equal("constraint_violation", (await f.G().PreviewRescheduleAsync(f.Owner, f.ProjectId, Move(a, 4, "single"), default)).Error);
    }

    [Fact]
    public async Task Permissions_and_cross_project_rejection()
    {
        using var f = new GanttFixture();
        var a = await f.TaskAsync("A", 2, 3);
        var g = f.G();
        Assert.Equal("forbidden", (await g.PreviewRescheduleAsync(f.Viewer, f.ProjectId, Move(a, 4), default)).Error);
        Assert.Equal("forbidden", (await g.ApplyRescheduleAsync(f.Viewer, f.ProjectId, Move(a, 4), default)).Error);
        Assert.Equal("forbidden", (await g.ApplyRescheduleAsync(f.Outsider, f.ProjectId, Move(a, 4), default)).Error);
        Assert.Equal("forbidden", (await g.RevertRescheduleAsync(f.Viewer, f.ProjectId, new RescheduleRevertRequest([new RevertChangeDto(a, null, null)]), default)).Error);
        Assert.Equal("invalid_mode", (await g.PreviewRescheduleAsync(f.Owner, f.ProjectId, Move(a, 4, "bogus"), default)).Error);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(a));

        // A task from another project is rejected by apply, preview and revert.
        var other = (await f.Svc.CreateProjectAsync(f.Owner, new CreateProjectRequest("Other", null, 2026, f.Owner, null, null, null, null), default)).Project!.Id;
        var foreign = (await f.Svc.CreateTaskAsync(f.Owner, new CreateTaskRequest(other, null, "Foreign", null, "todo", null, GanttFixture.D(2), GanttFixture.D(3), null, null, null), default)).Task!.Id;
        Assert.Equal("task_not_found", (await g.ApplyRescheduleAsync(f.Owner, f.ProjectId, Move(foreign, 9), default)).Error);
        Assert.Equal("task_not_found", (await g.PreviewRescheduleAsync(f.Owner, f.ProjectId, Move(foreign, 9), default)).Error);
        var bad = await g.RevertRescheduleAsync(f.Owner, f.ProjectId, new RescheduleRevertRequest([new RevertChangeDto(a, null, null), new RevertChangeDto(foreign, GanttFixture.D(20), GanttFixture.D(21))]), default);
        Assert.Equal("task_not_found", bad.Error);
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(a));   // all-or-nothing
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(3)), await f.DatesAsync(foreign));
    }
}

public sealed class MsProjectTests
{
    private static async Task<GanttFixture> SeedAsync()
    {
        var f = new GanttFixture();
        var g1 = (await f.Svc.GetGroupsAsync(f.ProjectId, f.Owner, default))!.First().Id;
        var g2 = (await f.Svc.CreateGroupAsync(f.Owner, f.ProjectId, new CreateProjectGroupRequest("Rollout"), default)).Group!.Id;
        var a = await f.TaskAsync("Design", 2, 4, g1);
        var b = await f.TaskAsync("Build", 5, 10, g1);
        var c = await f.TaskAsync("Pilot", 11, 12, g2);
        await f.TaskAsync("Design review", 2, 2, g1, "todo", a);
        await f.LinkAsync(a, b, "FS", 1); await f.LinkAsync(b, c, "SS", 2); await f.LinkAsync(a, c, "FF", 0);
        await f.Svc.AssignUserAsync(f.Owner, a, f.Member, null, default);
        await f.Svc.UpdateTaskAsync(f.Owner, c, new UpdateTaskRequest(null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            DeadlineDate: GanttFixture.D(20), ConstraintType: "snet", ConstraintDate: GanttFixture.D(11)), default);
        await new MilestoneService(f.T.Verify()).CreateAsync(f.Owner, f.ProjectId, new CreateMilestoneRequest("Go-live", GanttFixture.D(13), [c]), GanttFixture.D(2), default);
        await f.Svc.UpsertCalendarEntryAsync(f.Owner, f.ProjectId, new UpsertCalendarEntryRequest(GanttFixture.D(18), "holiday", "Day off", null), default);
        return f;
    }

    private static async Task<(Guid Id, List<(string Name, string? Parent, DateTime? S, DateTime? D, string? C)> Tasks)> FreshAsync(GanttFixture f)
    {
        var p = (await f.Svc.CreateProjectAsync(f.Owner, new CreateProjectRequest("Fresh", null, 2026, f.Owner, null, null, null, null), default)).Project!.Id;
        await f.Svc.AddMemberAsync(f.Owner, p, new AddProjectMemberRequest(f.Member, "member"), default);
        return (p, []);
    }

    [Fact]
    public async Task Export_then_import_round_trips_structure()
    {
        using var f = await SeedAsync();
        var g = f.G();
        var xml = (await g.ExportMsProjectAsync(f.Viewer, f.ProjectId, default)).Value!;
        Assert.EndsWith(".xml", xml.FileName);
        Assert.Contains("http://schemas.microsoft.com/project", xml.Xml);
        Assert.Contains("<Type>3</Type>", xml.Xml);          // SS
        Assert.Contains("<LinkLag>9600</LinkLag>", xml.Xml); // SS lag 2 days = 2 x 480 min x 10
        Assert.Contains("<LinkLag>4800</LinkLag>", xml.Xml); // FS lag 1 day
        var (fresh, _) = await FreshAsync(f);

        var dryR = await g.ImportMsProjectAsync(f.Owner, fresh, Encoding.UTF8.GetBytes(xml.Xml), "p.xml", true, default); Assert.True(dryR.Success, dryR.Error); var dry = dryR.Value!;
        Assert.True(dry.DryRun);
        using (var v0 = f.T.Verify()) Assert.Equal(0, await v0.ProjectTasks.CountAsync(t => t.ProjectId == fresh));

        var r = await g.ImportMsProjectAsync(f.Owner, fresh, Encoding.UTF8.GetBytes(xml.Xml), "p.xml", false, default);
        Assert.True(r.Success, r.Error);
        Assert.Equal((2, 4, 3, 1), (r.Value!.GroupCount, r.Value.TaskCount, r.Value.LinkCount, r.Value.MilestoneCount));
        Assert.Equal((dry.GroupCount, dry.TaskCount, dry.LinkCount, dry.MilestoneCount), (r.Value.GroupCount, r.Value.TaskCount, r.Value.LinkCount, r.Value.MilestoneCount));
        Assert.Empty(r.Value.Warnings);

        using var v = f.T.Verify();
        var groups = await v.ProjectGroups.Where(x => x.ProjectId == fresh).OrderBy(x => x.SortOrder).ToListAsync();
        Assert.Contains(groups, x => x.Name == "Rollout");
        Assert.Contains(await v.ProjectGroups.Where(x => x.ProjectId == fresh).Select(x => x.Name).ToListAsync(), n => n == "Tasks");
        var tasks = await v.ProjectTasks.Where(t => t.ProjectId == fresh).Include(t => t.Assignees).ToListAsync();
        var design = tasks.Single(t => t.Title == "Design");
        var build = tasks.Single(t => t.Title == "Build");
        var pilot = tasks.Single(t => t.Title == "Pilot");
        var review = tasks.Single(t => t.Title == "Design review");
        Assert.Equal((GanttFixture.D(2), GanttFixture.D(4)), (design.StartDate, design.DueDate));
        Assert.Equal(design.Id, review.ParentTaskId);
        Assert.Equal(f.Member, Assert.Single(design.Assignees).UserId);
        Assert.Equal(("snet", GanttFixture.D(11), GanttFixture.D(20)), (pilot.ConstraintType, pilot.ConstraintDate, pilot.DeadlineDate));
        Assert.Equal(groups.Single(x => x.Name == "Rollout").Id, pilot.GroupId);

        var links = await v.ProjectTaskDependencies.Where(d => d.Task.ProjectId == fresh).ToListAsync();
        var l1 = links.Single(l => l.TaskId == build.Id && l.DependsOnTaskId == design.Id);
        Assert.Equal(("FS", 1), (l1.LinkType, l1.LagDays));
        var l2 = links.Single(l => l.TaskId == pilot.Id && l.DependsOnTaskId == build.Id);
        Assert.Equal(("SS", 2), (l2.LinkType, l2.LagDays));
        Assert.Equal("FF", links.Single(l => l.TaskId == pilot.Id && l.DependsOnTaskId == design.Id).LinkType);

        var m = await v.ProjectMilestones.Include(x => x.LinkedTasks).SingleAsync(x => x.ProjectId == fresh);
        Assert.Equal(("Go-live", GanttFixture.D(13)), (m.Title, m.DueDate));
        Assert.Equal(pilot.Id, Assert.Single(m.LinkedTasks).TaskId);
    }

    private static string Mspdi(string tasks, string resources = "", string assignments = "") =>
        $"""<?xml version="1.0" encoding="UTF-8"?><Project xmlns="http://schemas.microsoft.com/project"><Tasks>{tasks}</Tasks><Resources>{resources}</Resources><Assignments>{assignments}</Assignments></Project>""";

    private static string T(int uid, string name, int level, string extra = "", bool summary = false) =>
        $"<Task><UID>{uid}</UID><ID>{uid}</ID><Name>{name}</Name><OutlineLevel>{level}</OutlineLevel><Summary>{(summary ? 1 : 0)}</Summary>{extra}</Task>";

    [Fact]
    public async Task Import_robustness_bad_xml_mpp_too_many_permissions()
    {
        using var f = new GanttFixture();
        var g = f.G();
        Assert.Equal("invalid_file", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes("not xml <<"), "a.xml", false, default)).Error);
        Assert.Equal("invalid_file", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes("<Other/>"), "a.xml", false, default)).Error);
        Assert.Equal("invalid_file", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, [], "a.xml", false, default)).Error);
        Assert.Equal("mpp_not_supported", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3], "plan.mpp", false, default)).Error);
        Assert.Equal("mpp_not_supported", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes(Mspdi("")), "plan.MPP", false, default)).Error);

        var many = string.Concat(Enumerable.Range(1, 2001).Select(i => T(i, $"T{i}", 1)));
        Assert.Equal("too_many_tasks", (await g.ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes(Mspdi(many)), "a.xml", false, default)).Error);
        var ok = string.Concat(Enumerable.Range(1, 2000).Select(i => T(i, $"T{i}", 1)));
        Assert.True((await g.ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes(Mspdi(ok)), "a.xml", true, default)).Success);

        var one = Encoding.UTF8.GetBytes(Mspdi(T(1, "X", 1)));
        Assert.Equal("forbidden", (await g.ImportMsProjectAsync(f.Member, f.ProjectId, one, "a.xml", false, default)).Error);
        Assert.Equal("forbidden", (await g.ImportMsProjectAsync(f.Viewer, f.ProjectId, one, "a.xml", false, default)).Error);
        using var v = f.T.Verify();
        Assert.Equal(0, await v.ProjectTasks.CountAsync());
    }

    [Fact]
    public async Task Import_warns_on_unknown_resource_and_SF_link_never_creates_users_and_is_additive()
    {
        using var f = new GanttFixture();
        await f.TaskAsync("Existing", 2, 3);
        var usersBefore = await f.T.Verify().Users.CountAsync();
        var tasks = T(1, "Phase", 1, summary: true)
            + T(2, "Alpha", 2, "<Start>2026-11-02T08:00:00</Start><Finish>2026-11-04T17:00:00</Finish><Duration>PT24H0M0S</Duration><PercentComplete>50</PercentComplete>")
            + T(3, "Beta", 2, "<Start>2026-11-05T08:00:00</Start><Duration>PT16H0M0S</Duration><PredecessorLink><PredecessorUID>2</PredecessorUID><Type>2</Type><LinkLag>0</LinkLag></PredecessorLink>")
            + T(5, "Gamma", 2, "<Start>2026-11-02T08:00:00</Start><Finish>2026-11-02T17:00:00</Finish><Duration>PT8H0M0S</Duration><PercentComplete>100</PercentComplete>")
            + T(4, "Orphan", 1);
        var res = "<Resource><UID>1</UID><Name>Nobody Real</Name></Resource><Resource><UID>2</UID><Name>user G_M</Name></Resource>";
        var asg = "<Assignment><TaskUID>2</TaskUID><ResourceUID>1</ResourceUID></Assignment><Assignment><TaskUID>2</TaskUID><ResourceUID>2</ResourceUID></Assignment>";
        var r = await f.G().ImportMsProjectAsync(f.Owner, f.ProjectId, Encoding.UTF8.GetBytes(Mspdi(tasks, res, asg)), "a.xml", false, default);
        Assert.True(r.Success, r.Error);
        Assert.Equal((1, 4, 1), (r.Value!.GroupCount, r.Value.TaskCount, r.Value.LinkCount));
        Assert.Contains(r.Value.Warnings, w => w.Contains("Nobody Real"));
        Assert.Contains(r.Value.Warnings, w => w.Contains("Start-to-Finish"));
        Assert.DoesNotContain(r.Value.Warnings, w => w.Contains("G_M"));

        using var v = f.T.Verify();
        Assert.Equal(usersBefore, await v.Users.CountAsync());
        Assert.Equal(5, await v.ProjectTasks.CountAsync(t => t.ProjectId == f.ProjectId));   // existing kept
        // The project's stored progress (lists, portfolio, header) follows the imported tasks: 1 of 5 done.
        Assert.Equal(20, (await v.Projects.SingleAsync(p => p.Id == f.ProjectId)).Progress);
        var alpha = await v.ProjectTasks.Include(t => t.Assignees).SingleAsync(t => t.Title == "Alpha");
        Assert.Equal(f.Member, Assert.Single(alpha.Assignees).UserId);
        Assert.Equal("doing", alpha.Status);
        var beta = await v.ProjectTasks.SingleAsync(t => t.Title == "Beta");
        Assert.Equal((GanttFixture.D(5), GanttFixture.D(6)), (beta.StartDate, beta.DueDate));   // 2 working days from Thu
        var orphan = await v.ProjectTasks.SingleAsync(t => t.Title == "Orphan");
        var phase = await v.ProjectGroups.SingleAsync(g => g.Name == "Phase");
        Assert.Equal(phase.Id, orphan.GroupId);   // no summary of its own: first (new) group
        Assert.Equal("FS", (await v.ProjectTaskDependencies.SingleAsync()).LinkType);
    }

    [Fact]
    public async Task Export_is_readable_by_viewers_only_inside_the_project()
    {
        using var f = await SeedAsync();
        Assert.True((await f.G().ExportMsProjectAsync(f.Viewer, f.ProjectId, default)).Success);
        Assert.False((await f.G().ExportMsProjectAsync(f.Outsider, f.ProjectId, default)).Success);
    }
}
