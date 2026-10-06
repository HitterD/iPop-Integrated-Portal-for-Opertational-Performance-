using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Project templates (mockup project-reports.html, artboard 2): who may use a template, Save as
/// template with sections / milestones / assignees and relative dates, New project from a
/// template, built-in seeding, delete rules; plus the milestone Owner field.
public sealed class ProjectTemplateTests
{
    private static readonly DateTime SourceStart = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NewStart = new(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Ctx : IDisposable
    {
        public required TestDb T;
        public required User Owner, Colleague, OtherDept, Outsider;
        public required Guid DeptA;
        public void Dispose() => T.Dispose();
        public ProjectService Projects() => new(T.Verify());
        public ProjectTemplateService Svc() { var db = T.Verify(); return new ProjectTemplateService(db, new ProjectService(db)); }
    }

    private static Ctx Setup()
    {
        var t = new TestDb();
        var a = new Department { Id = Guid.NewGuid(), Code = "ICT", Name = "ICT" };
        var b = new Department { Id = Guid.NewGuid(), Code = "QA", Name = "QA" };
        t.Db.Departments.AddRange(a, b);
        t.Db.SaveChanges();
        var owner = t.AddUser("TPL_O"); var colleague = t.AddUser("TPL_C"); var other = t.AddUser("TPL_D"); var outsider = t.AddUser("TPL_X");
        owner.DepartmentId = a.Id; colleague.DepartmentId = a.Id; other.DepartmentId = b.Id;
        t.Db.SaveChanges();
        return new Ctx { T = t, Owner = owner, Colleague = colleague, OtherDept = other, Outsider = outsider, DeptA = a.Id };
    }

    private static async Task<Guid> SaveAsync(Ctx c, User who, string name, string visibility) =>
        (await c.Svc().CreateAsync(who.Id, new CreateProjectTemplateRequest(name, null, null, Visibility: visibility), default)).Value!.Id;

    [Fact]
    public async Task List_shows_only_templates_the_caller_may_use()
    {
        using var c = Setup();
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        await SaveAsync(c, c.Owner, "Mine private", "private");
        await SaveAsync(c, c.Owner, "ICT only", "department");
        await SaveAsync(c, c.Owner, "For all", "everyone");
        await SaveAsync(c, c.OtherDept, "QA only", "department");

        async Task<List<string>> Names(User u) => (await c.Svc().ListAsync(u.Id, false, default)).Select(x => x.Name).ToList();

        var mine = await Names(c.Owner);
        Assert.Equal(["Software sprint", "IT procurement", "Event planning"], mine.Take(3));
        Assert.Contains("Mine private", mine); Assert.Contains("ICT only", mine); Assert.Contains("For all", mine);
        Assert.DoesNotContain("QA only", mine);

        var colleague = await Names(c.Colleague);
        Assert.Contains("ICT only", colleague); Assert.Contains("For all", colleague);
        Assert.DoesNotContain("Mine private", colleague); Assert.DoesNotContain("QA only", colleague);

        var outsider = await Names(c.Outsider);   // no department
        Assert.Equal(4, outsider.Count);           // 3 built-ins + "For all"
        Assert.Contains("For all", outsider);

        // A template the caller may not use is not found either.
        var priv = (await c.Svc().ListAsync(c.Owner.Id, false, default)).Single(x => x.Name == "Mine private").Id;
        Assert.Equal("not_found", (await c.Svc().GetAsync(c.Colleague.Id, priv, default)).Error);
        Assert.Equal("not_found", (await c.Svc().CreateProjectAsync(c.Colleague.Id,
            new CreateProjectFromTemplateRequest(priv, "x", null, null, null, NewStart, null), default)).Error);
    }

    [Fact]
    public async Task Copied_third_party_assignee_becomes_a_member()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        var tpl = (await c.Svc().CreateAsync(c.Owner.Id,
            new CreateProjectTemplateRequest("Shared", null, pid, IncludeAssignees: true), default)).Value!;
        var result = await c.Svc().CreateProjectAsync(c.Owner.Id,
            new CreateProjectFromTemplateRequest(tpl.Id, "Copy", null, null, null, NewStart, null), default);
        Assert.True(result.Success, result.Error);
        using var db = c.T.Verify();
        Assert.True(await db.ProjectMembers.AnyAsync(m => m.ProjectId == result.Value!.Id && m.UserId == c.Colleague.Id));
    }

    [Fact]
    public async Task Department_visibility_needs_a_department_and_bad_values_are_rejected()
    {
        using var c = Setup();
        Assert.Equal("no_department", (await c.Svc().CreateAsync(c.Outsider.Id, new CreateProjectTemplateRequest("x", null, null, Visibility: "department"), default)).Error);
        Assert.Equal("invalid_visibility", (await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("x", null, null, Visibility: "public"), default)).Error);
        Assert.Equal("invalid_name", (await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("  ", null, null), default)).Error);
    }

    /// Source project: 2 sections, 3 tasks (2 dated, 1 assigned), 1 milestone linking 2 tasks.
    private static async Task<(Guid ProjectId, Guid[] Tasks)> SourceProjectAsync(Ctx c)
    {
        var ps = c.Projects();
        var p = (await ps.CreateProjectAsync(c.Owner.Id, new CreateProjectRequest("Audit ISO", null, 2026, c.Owner.Id, SourceStart, null, null, null), default)).Project!;
        await ps.AddMemberAsync(c.Owner.Id, p.Id, new AddProjectMemberRequest(c.Colleague.Id, "member"), default);
        using (var db = c.T.Verify())
        {
            var first = await db.ProjectGroups.SingleAsync(g => g.ProjectId == p.Id);
            first.Name = "Preparation"; first.Color = "#0A84FF";
            db.ProjectGroups.Add(new ProjectGroup { Id = Guid.NewGuid(), ProjectId = p.Id, Name = "Site visit", Color = "#FF9F0A", SortOrder = 100 });
            await db.SaveChangesAsync();
        }
        Guid groupB;
        using (var db = c.T.Verify()) groupB = (await db.ProjectGroups.SingleAsync(g => g.ProjectId == p.Id && g.Name == "Site visit")).Id;

        var t1 = (await ps.CreateTaskAsync(c.Owner.Id, new CreateTaskRequest(p.Id, null, "Collect documents", "SOPs", "done", "high", null, SourceStart.AddDays(3), null, null, null), default)).Task!.Id;
        var t2 = (await ps.CreateTaskAsync(c.Owner.Id, new CreateTaskRequest(p.Id, null, "Walk the line", null, "todo", null, null, SourceStart.AddDays(10), null, null, null), default)).Task!.Id;
        var t3 = (await ps.CreateTaskAsync(c.Owner.Id, new CreateTaskRequest(p.Id, null, "Write report", null, "todo", null, null, null, null, null, null), default)).Task!.Id;
        using (var db = c.T.Verify())
        {
            (await db.ProjectTasks.SingleAsync(t => t.Id == t2)).GroupId = groupB;
            db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = t2, UserId = c.Colleague.Id, AssignedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var ms = await new MilestoneService(c.T.Verify()).CreateAsync(c.Owner.Id, p.Id,
            new CreateMilestoneRequest("Audit day", SourceStart.AddDays(14), [t1, t2]), Today, default);
        Assert.True(ms.Success, ms.Error);
        return (p.Id, [t1, t2, t3]);
    }

    [Fact]
    public async Task Save_with_options_round_trips_sections_milestones_assignees_and_offsets()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);

        var r = await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("Audit site visit", "QA audit", pid,
            IncludeSections: true, IncludeMilestones: true, IncludeAssignees: true, Visibility: "department"), default);
        Assert.True(r.Success, r.Error);
        var dto = r.Value!;
        Assert.Equal((3, 2, 1, 0), (dto.TaskCount, dto.GroupCount, dto.MilestoneCount, dto.UsedCount));
        Assert.Equal("department", dto.Visibility);
        Assert.Equal("ICT", dto.DepartmentName);
        Assert.Equal(c.Owner.Name, dto.CreatedByName);
        Assert.False(dto.IsBuiltIn);

        var d = (await c.Svc().GetAsync(c.Owner.Id, dto.Id, default)).Value!;
        Assert.Equal(["Preparation", "Site visit"], d.Groups!.Select(g => g.Name));
        Assert.Equal("#FF9F0A", d.Groups![1].Color);
        var walk = d.Tasks.Single(t => t.Title == "Walk the line");
        Assert.Equal(10, walk.DueOffsetDays);
        Assert.Equal(d.Groups![1].Id, walk.GroupId);
        Assert.Equal([c.Colleague.Nik], walk.AssigneeNiks!);
        Assert.Equal(3, d.Tasks.Single(t => t.Title == "Collect documents").DueOffsetDays);
        Assert.Null(d.Tasks.Single(t => t.Title == "Write report").DueOffsetDays);
        var m = Assert.Single(d.Milestones!);
        Assert.Equal(("Audit day", 14), (m.Title, m.DueOffsetDays));
        Assert.Equal(2, m.TaskIds.Count);
    }

    [Fact]
    public async Task Save_without_options_keeps_only_tasks()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        var dto = (await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("Bare", null, pid,
            IncludeSections: false, IncludeMilestones: false, IncludeAssignees: false, Visibility: "private"), default)).Value!;
        Assert.Equal((3, 0, 0), (dto.TaskCount, dto.GroupCount, dto.MilestoneCount));
        var d = (await c.Svc().GetAsync(c.Owner.Id, dto.Id, default)).Value!;
        Assert.All(d.Tasks, t => { Assert.Null(t.GroupId); Assert.Empty(t.AssigneeNiks!); });
    }

    [Fact]
    public async Task Saving_from_a_project_requires_membership()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        Assert.Equal("forbidden", (await c.Svc().CreateAsync(c.Outsider.Id, new CreateProjectTemplateRequest("Steal", null, pid), default)).Error);
    }

    [Fact]
    public async Task From_template_recreates_sections_tasks_milestones_with_dates_from_start_and_counts_use()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        var tplId = (await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("Audit site visit", null, pid,
            IncludeAssignees: true, Visibility: "department"), default)).Value!.Id;

        // A colleague in the same department uses it.
        var r = await c.Svc().CreateProjectAsync(c.Colleague.Id,
            new CreateProjectFromTemplateRequest(tplId, "Audit ISO — Karawang", null, null, null, NewStart, null), default);
        Assert.True(r.Success, r.Error);
        var np = r.Value!.Id;

        using var v = c.T.Verify();
        var groups = await v.ProjectGroups.Where(g => g.ProjectId == np).OrderBy(g => g.SortOrder).ToListAsync();
        Assert.Equal(["Preparation", "Site visit"], groups.Select(g => g.Name));
        var tasks = await v.ProjectTasks.Where(t => t.ProjectId == np).Include(t => t.Assignees).ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.All(tasks, t => Assert.Equal("todo", t.Status));
        var walk = tasks.Single(t => t.Title == "Walk the line");
        Assert.Equal(NewStart.AddDays(10), walk.DueDate);
        Assert.Equal(groups[1].Id, walk.GroupId);
        Assert.Equal(c.Colleague.Id, Assert.Single(walk.Assignees).UserId);
        Assert.Equal(NewStart.AddDays(3), tasks.Single(t => t.Title == "Collect documents").DueDate);
        Assert.Equal(groups[0].Id, tasks.Single(t => t.Title == "Collect documents").GroupId);
        Assert.Null(tasks.Single(t => t.Title == "Write report").DueDate);

        var ms = await v.ProjectMilestones.Where(m => m.ProjectId == np).Include(m => m.LinkedTasks).SingleAsync();
        Assert.Equal(("Audit day", NewStart.AddDays(14)), (ms.Title, ms.DueDate));
        Assert.Equal(tasks.Where(t => t.Title is "Collect documents" or "Walk the line").Select(t => t.Id).OrderBy(x => x),
            ms.LinkedTasks.Select(l => l.TaskId).OrderBy(x => x));

        Assert.Equal(1, (await v.ProjectTemplates.SingleAsync(t => t.Id == tplId)).UsedCount);
        Assert.Equal(new DateTime(2026, 10, 12), (await v.Projects.SingleAsync(p => p.Id == np)).StartDate!.Value.Date);
    }

    [Fact]
    public async Task From_template_skips_assignees_who_no_longer_exist()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        var tplId = (await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("A", null, pid, IncludeAssignees: true), default)).Value!.Id;
        using (var db = c.T.Verify())
        {
            (await db.Users.SingleAsync(u => u.Id == c.Colleague.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }
        var np = (await c.Svc().CreateProjectAsync(c.Owner.Id, new CreateProjectFromTemplateRequest(tplId, "B", null, null, null, NewStart, null), default)).Value!.Id;
        using var v = c.T.Verify();
        Assert.False(await v.ProjectTaskAssignees.AnyAsync(a => a.Task.ProjectId == np));
    }

    [Fact]
    public async Task Built_in_seeding_is_idempotent_and_matches_the_mockup()
    {
        using var c = Setup();
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());

        var list = (await c.Svc().ListAsync(c.Owner.Id, true, default)).Where(t => t.IsBuiltIn).ToList();
        Assert.Equal(3, list.Count);
        var sprint = list.Single(t => t.Name == "Software sprint");
        Assert.Equal((12, 4, 2), (sprint.TaskCount, sprint.GroupCount, sprint.MilestoneCount));
        var proc = list.Single(t => t.Name == "IT procurement");
        Assert.Equal((9, 5, 1), (proc.TaskCount, proc.GroupCount, proc.MilestoneCount));
        var ev = list.Single(t => t.Name == "Event planning");
        Assert.Equal((14, 3, 1), (ev.TaskCount, ev.GroupCount, ev.MilestoneCount));
        Assert.All(list, t => { Assert.Equal("everyone", t.Visibility); Assert.False(t.CanDelete); });

        using var v = c.T.Verify();
        Assert.Equal(3, await v.ProjectTemplates.CountAsync());
        Assert.Equal(35, await v.ProjectTemplateTasks.CountAsync());

        // A used built-in keeps its count across restarts.
        var r = await c.Svc().CreateProjectAsync(c.Owner.Id, new CreateProjectFromTemplateRequest(BuiltInProjectTemplates.ItProcurementId, "Laptops", null, null, null, NewStart, null), default);
        Assert.True(r.Success, r.Error);
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        using var v2 = c.T.Verify();
        Assert.Equal(1, (await v2.ProjectTemplates.SingleAsync(t => t.Id == BuiltInProjectTemplates.ItProcurementId)).UsedCount);
        var installed = await v2.ProjectMilestones.Include(m => m.LinkedTasks).SingleAsync(m => m.ProjectId == r.Value!.Id);
        Assert.Equal(("Installed", NewStart.AddDays(32), 3), (installed.Title, installed.DueDate, installed.LinkedTasks.Count));
    }

    [Fact]
    public async Task Delete_rules_owner_or_admin_never_built_in()
    {
        using var c = Setup();
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        var id = await SaveAsync(c, c.Owner, "Shared", "everyone");

        Assert.False((await c.Svc().ListAsync(c.Colleague.Id, false, default)).Single(t => t.Id == id).CanDelete);
        Assert.True((await c.Svc().ListAsync(c.Owner.Id, false, default)).Single(t => t.Id == id).CanDelete);
        Assert.True((await c.Svc().ListAsync(c.Colleague.Id, true, default)).Single(t => t.Id == id).CanDelete);

        Assert.Equal("forbidden", (await c.Svc().DeleteAsync(c.Colleague.Id, id, false, default)).Error);
        Assert.Equal("built_in", (await c.Svc().DeleteAsync(c.Owner.Id, BuiltInProjectTemplates.SoftwareSprintId, true, default)).Error);
        Assert.True((await c.Svc().DeleteAsync(c.Owner.Id, id, false, default)).Success);
        Assert.DoesNotContain(await c.Svc().ListAsync(c.Owner.Id, false, default), t => t.Id == id);

        using var v = c.T.Verify();
        Assert.False((await v.ProjectTemplates.SingleAsync(t => t.Id == id)).IsActive);   // soft delete

        var other = await SaveAsync(c, c.Owner, "Another", "everyone");
        Assert.True((await c.Svc().DeleteAsync(c.Colleague.Id, other, true, default)).Success);   // Admin
        Assert.Equal("not_found", (await c.Svc().DeleteAsync(c.Owner.Id, other, false, default)).Error);
    }

    // ── Milestone owner ──

    [Fact]
    public async Task Milestone_owner_must_be_a_project_member()
    {
        using var c = Setup();
        var (pid, _) = await SourceProjectAsync(c);
        var ms = new MilestoneService(c.T.Verify());

        Assert.Equal("invalid_owner", (await ms.CreateAsync(c.Owner.Id, pid, new CreateMilestoneRequest("UAT", NewStart, OwnerId: c.Outsider.Id), Today, default)).Error);

        var created = await new MilestoneService(c.T.Verify()).CreateAsync(c.Owner.Id, pid, new CreateMilestoneRequest("UAT", NewStart, OwnerId: c.Colleague.Id), Today, default);
        Assert.True(created.Success, created.Error);
        Assert.Equal((c.Colleague.Id, c.Colleague.Name, c.Colleague.Nik), (created.Value!.OwnerId, created.Value.OwnerName, created.Value.OwnerNik));

        var mid = created.Value.Id;
        Assert.Equal("invalid_owner", (await new MilestoneService(c.T.Verify()).UpdateAsync(c.Owner.Id, pid, mid, new UpdateMilestoneRequest(OwnerId: c.Outsider.Id), Today, default)).Error);
        var moved = await new MilestoneService(c.T.Verify()).UpdateAsync(c.Owner.Id, pid, mid, new UpdateMilestoneRequest(OwnerId: c.Owner.Id), Today, default);
        Assert.Equal(c.Owner.Id, moved.Value!.OwnerId);
        // Leaving OwnerId out keeps it; ClearOwner removes it.
        Assert.Equal(c.Owner.Id, (await new MilestoneService(c.T.Verify()).UpdateAsync(c.Owner.Id, pid, mid, new UpdateMilestoneRequest(Title: "UAT ready"), Today, default)).Value!.OwnerId);
        var cleared = await new MilestoneService(c.T.Verify()).UpdateAsync(c.Owner.Id, pid, mid, new UpdateMilestoneRequest(ClearOwner: true), Today, default);
        Assert.Null(cleared.Value!.OwnerId);
        Assert.Null(cleared.Value.OwnerName);
    }
}
