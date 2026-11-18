using System.Text.Json;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IPop.Tests;

/// Projects v2 stage 2: Template Center metadata, categories, statuses/tabs/roles/goal on create,
/// working-day dates, recurring tasks, and the integrity of every built-in definition.
public sealed class ProjectTemplateV2Tests
{
    private static readonly DateTime Monday = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Ctx : IDisposable
    {
        public required TestDb T;
        public required User Owner, Eng, Key;
        public void Dispose() => T.Dispose();
        public ProjectTemplateService Svc() { var db = T.Verify(); return new ProjectTemplateService(db, new ProjectService(db)); }
    }

    private static async Task<Ctx> SetupAsync()
    {
        var t = new TestDb();
        var c = new Ctx { T = t, Owner = t.AddUser("V2_O"), Eng = t.AddUser("V2_E"), Key = t.AddUser("V2_K") };
        await BuiltInProjectTemplates.EnsureSeededAsync(t.Verify());
        return c;
    }

    [Theory]
    [InlineData("2026-11-02", 0, "2026-11-02")]
    [InlineData("2026-11-02", 4, "2026-11-06")]
    [InlineData("2026-11-02", 5, "2026-11-09")]
    [InlineData("2026-11-06", 1, "2026-11-09")]
    [InlineData("2026-11-07", 0, "2026-11-09")]
    [InlineData("2026-11-02", 10, "2026-11-16")]
    public void AddWorkingDays_skips_weekends(string start, int n, string expected) =>
        Assert.Equal(DateTime.Parse(expected), WorkingDays.AddWorkingDays(DateTime.Parse(start), n));

    [Fact]
    public void AddWorkingDays_honours_a_holiday_hook() =>
        Assert.Equal(new DateTime(2026, 11, 4), WorkingDays.AddWorkingDays(Monday, 1, d => d == new DateTime(2026, 11, 3)));

    [Fact]
    public void Every_built_in_definition_is_valid() => Assert.Empty(BuiltInProjectTemplates.Validate());

    [Fact]
    public async Task Seeding_is_idempotent_and_adds_the_v2_templates()
    {
        using var c = await SetupAsync();
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        var list = (await c.Svc().ListAsync(c.Owner.Id, false, default)).Where(t => t.IsBuiltIn).ToList();
        Assert.True(list.Count >= 20, list.Count.ToString());
        Assert.All(list, t => { Assert.NotNull(t.Category); Assert.True(t.DurationWeeks > 0); });
        var ict = list.Single(t => t.Name == "ICT implementation");
        Assert.Equal(("ICT", 10, 4, 22), (ict.Category, ict.DurationWeeks!.Value, ict.GroupCount, ict.TaskCount));
        Assert.Equal("Finance & purchasing", list.Single(t => t.Id == BuiltInProjectTemplates.ItProcurementId).Category);
        Assert.Single(list, t => t.Name == "IT procurement");
    }

    [Fact]
    public async Task Backfill_fills_old_built_in_rows_without_touching_edits()
    {
        using var c = await SetupAsync();
        using (var db = c.T.Verify())
        {
            var row = await db.ProjectTemplates.SingleAsync(t => t.Id == BuiltInProjectTemplates.EventPlanningId);
            row.Category = null; row.StatusesJson = null; row.TabsJson = null; row.RolesJson = null; row.DurationWeeks = null;
            row.Name = "Our office party";
            await db.SaveChangesAsync();
        }
        await BuiltInProjectTemplates.EnsureSeededAsync(c.T.Verify());
        using var v = c.T.Verify();
        var after = await v.ProjectTemplates.SingleAsync(t => t.Id == BuiltInProjectTemplates.EventPlanningId);
        Assert.Equal(("Events", "Our office party"), (after.Category, after.Name));
        Assert.NotNull(after.TabsJson); Assert.NotNull(after.RolesJson);
    }

    [Fact]
    public async Task Categories_count_visible_templates_and_pseudo_categories()
    {
        using var c = await SetupAsync();
        await c.Svc().CreateAsync(c.Owner.Id, new CreateProjectTemplateRequest("Mine", null, null, Visibility: "private"), default);
        var cats = await c.Svc().CategoriesAsync(c.Owner.Id, default);
        Assert.Contains(cats, x => x.Name == "ICT" && x.Count == 3);
        Assert.Contains(cats, x => x.Name == "HC" && x.Count == 4);
        Assert.Equal(1, cats.Single(x => x.Name == "My templates").Count);
        Assert.Equal(0, cats.Single(x => x.Name == "Shared with my department").Count);
        Assert.Equal(0, (await c.Svc().CategoriesAsync(c.Eng.Id, default)).Single(x => x.Name == "My templates").Count);
    }

    [Fact]
    public async Task Detail_carries_statuses_tabs_roles_automations_and_group_weeks()
    {
        using var c = await SetupAsync();
        var d = (await c.Svc().GetAsync(c.Owner.Id, BuiltInProjectTemplates.IctImplementationId, default)).Value!;
        Assert.Contains(d.Statuses!, s => s.Label == "Waiting vendor" && s.Stage == "doing");
        Assert.Equal(["overview", "table", "board", "gantt", "workload", "files"], d.Tabs);
        Assert.Equal(["Project lead", "IT engineer", "Key user"], d.Roles!.Select(r => r.Label));
        Assert.Equal(22, d.Roles!.Sum(r => r.TaskCount));
        Assert.InRange(d.Automations!.Count, 1, 3);
        Assert.Equal(4, d.GroupSummaries!.Count);
        Assert.Equal(1, d.GroupSummaries![0].WeekFrom);
        Assert.True(d.GroupSummaries[^1].WeekTo >= 10);
        Assert.Equal(22, d.GroupSummaries.Sum(g => g.TaskCount));
    }

    [Fact]
    public async Task Create_applies_statuses_tabs_goal_roles_and_working_day_dates()
    {
        using var c = await SetupAsync();
        var r = await c.Svc().CreateProjectAsync(c.Owner.Id, new CreateProjectFromTemplateRequest(
            BuiltInProjectTemplates.IctImplementationId, "ERP rollout", null, null, null, Monday, null,
            Goal: "Replace Excel with ERP", RoleAssignments: new Dictionary<string, IReadOnlyList<Guid>>
            {
                ["eng"] = [c.Eng.Id], ["user"] = [c.Key.Id, c.Eng.Id], ["bogus"] = [c.Key.Id],
            }), default);
        Assert.True(r.Success, r.Error);
        Assert.Equal("Replace Excel with ERP", r.Value!.Goal);
        Assert.Equal(["overview", "table", "board", "gantt", "workload", "files"], r.Value.Tabs);
        Assert.Equal(WorkingDays.AddWorkingDays(Monday, 50), r.Value.Deadline);

        using var v = c.T.Verify();
        var opts = await v.ProjectStatusOptions.Where(o => o.ProjectId == r.Value.Id).OrderBy(o => o.SortOrder).ToListAsync();
        Assert.Equal(6, opts.Count);
        Assert.Contains(opts, o => o.Label == "Waiting vendor");
        var tasks = await v.ProjectTasks.Include(t => t.Assignees).Where(t => t.ProjectId == r.Value.Id).ToListAsync();
        Assert.Equal(22, tasks.Count);
        var firstTodo = opts.First(o => o.Stage == "todo").Id;
        Assert.All(tasks, t => { Assert.Equal("todo", t.Status); Assert.Equal(firstTodo, t.StatusOptionId); });

        // "Install and configure the system": due offset 15, duration 5 -> start offset 11.
        var install = tasks.Single(t => t.Title == "Install and configure the system");
        Assert.Equal(WorkingDays.AddWorkingDays(Monday, 11), install.StartDate);
        Assert.Equal(WorkingDays.AddWorkingDays(Monday, 15), install.DueDate);
        Assert.Equal(new DateTime(2026, 11, 23), install.DueDate);   // 15 working days after Mon 2 Nov
        Assert.All(tasks, t => Assert.True(WorkingDays.IsWorkingDay(t.DueDate!.Value)));
        Assert.Null(tasks.Single(t => t.Title == "Kick-off meeting with stakeholders").StartDate);

        Assert.Equal(new[] { c.Eng.Id }, install.Assignees.Select(a => a.UserId));
        var uat = tasks.Single(t => t.Title == "User acceptance testing (UAT)");
        Assert.Equal(new[] { c.Eng.Id, c.Key.Id }.Order(), uat.Assignees.Select(a => a.UserId).Order());
        Assert.Empty(tasks.Single(t => t.Title == "Kick-off meeting with stakeholders").Assignees);
        var members = await v.ProjectMembers.Where(m => m.ProjectId == r.Value.Id).ToListAsync();
        Assert.Contains(members, m => m.UserId == c.Key.Id && m.Role == "member");
        Assert.Contains(members, m => m.UserId == c.Eng.Id);

        var ms = await v.ProjectMilestones.SingleAsync(m => m.ProjectId == r.Value.Id && m.Title == "Go-live");
        Assert.Equal(WorkingDays.AddWorkingDays(Monday, 45), ms.DueDate);
    }

    [Fact]
    public async Task Recurring_template_tasks_become_recurring_project_tasks()
    {
        using var c = await SetupAsync();
        var id = (await c.Svc().ListAsync(c.Owner.Id, false, default)).Single(t => t.Name == "Machine maintenance").Id;
        var r = await c.Svc().CreateProjectAsync(c.Owner.Id, new CreateProjectFromTemplateRequest(id, null, null, null, null, Monday, null), default);
        Assert.True(r.Success, r.Error);
        using var v = c.T.Verify();
        var tasks = await v.ProjectTasks.Where(t => t.ProjectId == r.Value!.Id).ToListAsync();
        var weekly = tasks.Single(t => t.Title == "Lubricate bearings and moving parts");
        Assert.True(weekly.IsRecurring);
        Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=FR", weekly.RecurrenceRule);
        Assert.False(tasks.Single(t => t.Title == "Monthly maintenance report").IsRecurring);
        Assert.Equal(ProjectStatusDefaults.Options.Count, await v.ProjectStatusOptions.CountAsync(o => o.ProjectId == r.Value!.Id));
    }

    [Fact]
    public async Task Old_request_shape_and_calendar_day_templates_behave_as_before()
    {
        using var c = await SetupAsync();
        var r = await c.Svc().CreateProjectAsync(c.Owner.Id,
            new CreateProjectFromTemplateRequest(BuiltInProjectTemplates.ItProcurementId, "Laptops", null, null, null, Monday, null), default);
        Assert.True(r.Success, r.Error);
        using var v = c.T.Verify();
        var m = await v.ProjectMilestones.SingleAsync(x => x.ProjectId == r.Value!.Id);
        Assert.Equal(Monday.AddDays(32), m.DueDate);
        Assert.Null(r.Value!.Deadline);
        Assert.Null(r.Value.Goal);
    }

    [Fact]
    public async Task Invalid_statuses_json_falls_back_to_defaults()
    {
        using var c = await SetupAsync();
        using (var db = c.T.Verify())
        {
            (await db.ProjectTemplates.SingleAsync(t => t.Id == BuiltInProjectTemplates.IctImplementationId)).StatusesJson =
                JsonSerializer.Serialize(new[] { new { label = "Only", color = "#fff", stage = "nope" } });
            await db.SaveChangesAsync();
        }
        var r = await c.Svc().CreateProjectAsync(c.Owner.Id,
            new CreateProjectFromTemplateRequest(BuiltInProjectTemplates.IctImplementationId, null, null, null, null, Monday, null), default);
        Assert.True(r.Success, r.Error);
        using var v = c.T.Verify();
        Assert.Equal(ProjectStatusDefaults.Options.Count, await v.ProjectStatusOptions.CountAsync(o => o.ProjectId == r.Value!.Id));
    }
}
