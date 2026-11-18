using IPop.Application;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Project templates: save a project's structure (sections, top-level tasks, milestones with
/// dates relative to its start, optionally assignees by NIK) and create new projects from it.
/// Who may use a template: built-ins and "everyone" for all, "private" for its creator,
/// "department" for users of that department. Delete: creator or Admin, never a built-in.
public sealed class ProjectTemplateService(IPopDbContext db, IProjectService projects) : IProjectTemplateService
{
    public const int MaxName = 200;
    public const int MaxDescription = 1000;

    public async Task<IReadOnlyList<ProjectTemplateDto>> ListAsync(Guid actorId, bool isAdmin, CancellationToken ct)
    {
        var deptId = await DepartmentOfAsync(actorId, ct);
        var rows = await Usable(db.ProjectTemplates.AsNoTracking(), actorId, deptId)
            .Select(t => new
            {
                t.Id, t.Name, t.Description, t.SourceProjectId, t.IsBuiltIn, t.Visibility, t.UsedCount, t.CreatedAt,
                t.Icon, t.Color, t.CreatedById, t.Category, t.DurationWeeks,
                CreatedByName = t.CreatedBy != null ? t.CreatedBy.Name : null,
                DepartmentName = t.Department != null ? t.Department.Name : null,
                SourceName = t.SourceProject != null ? t.SourceProject.Name : null,
                Tasks = t.Tasks.Count, Groups = t.Groups.Count, Milestones = t.Milestones.Count,
            })
            .ToListAsync(ct);
        // Built-ins first in their seeded order, then the team's by popularity.
        return rows
            .OrderByDescending(t => t.IsBuiltIn)
            .ThenBy(t => t.IsBuiltIn ? t.CreatedAt : DateTime.MinValue)
            .ThenByDescending(t => t.UsedCount).ThenBy(t => t.Name)
            .Select(t => new ProjectTemplateDto(t.Id, t.Name, t.Description, t.Tasks, t.SourceProjectId,
                t.Groups, t.Milestones, t.UsedCount, t.IsBuiltIn, t.Visibility, t.CreatedByName, t.DepartmentName,
                t.Icon, t.Color, !t.IsBuiltIn && (isAdmin || (t.CreatedById is { } c && c == actorId)), t.SourceName,
                t.Category, t.DurationWeeks))
            .ToList();
    }

    public const string MyTemplates = "My templates";
    public const string SharedWithDepartment = "Shared with my department";

    public async Task<IReadOnlyList<ProjectTemplateCategoryDto>> CategoriesAsync(Guid actorId, CancellationToken ct)
    {
        var deptId = await DepartmentOfAsync(actorId, ct);
        var rows = await Usable(db.ProjectTemplates.AsNoTracking(), actorId, deptId)
            .Select(t => new { t.Category, t.CreatedById, t.Visibility, t.DepartmentId }).ToListAsync(ct);
        var result = rows.Where(r => r.Category is not null).GroupBy(r => r.Category!)
            .OrderBy(g => g.Key).Select(g => new ProjectTemplateCategoryDto(g.Key, g.Count())).ToList();
        var mine = rows.Count(r => r.CreatedById == actorId);
        var shared = deptId is null ? 0 : rows.Count(r => r.Visibility == ProjectTemplateVisibility.Department
            && r.DepartmentId == deptId && r.CreatedById != actorId);
        result.Add(new(MyTemplates, mine));
        result.Add(new(SharedWithDepartment, shared));
        return result;
    }

    public async Task<TemplateResult<ProjectTemplateDetailDto>> GetAsync(Guid actorId, Guid templateId, CancellationToken ct)
    {
        var tpl = await LoadUsableAsync(actorId, templateId, ct);
        if (tpl is null) return Fail<ProjectTemplateDetailDto>("not_found");
        return new(Detail(tpl), null);
    }

    public async Task<ProjectTemplateScopeDto> ScopeAsync(Guid actorId, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking().Where(u => u.Id == actorId)
            .Select(u => new { u.DepartmentId, Name = u.Department != null ? u.Department.Name : null })
            .FirstOrDefaultAsync(ct);
        return new ProjectTemplateScopeDto(row?.DepartmentId, row?.Name);
    }

    public async Task<TemplateResult<ProjectTemplateDto>> CreateAsync(Guid actorId, CreateProjectTemplateRequest req, CancellationToken ct)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxName) return Fail<ProjectTemplateDto>("invalid_name");
        var visibility = string.IsNullOrWhiteSpace(req.Visibility) ? ProjectTemplateVisibility.Everyone : req.Visibility.Trim().ToLowerInvariant();
        if (!ProjectTemplateVisibility.All.Contains(visibility)) return Fail<ProjectTemplateDto>("invalid_visibility");
        Guid? deptId = null;
        if (visibility == ProjectTemplateVisibility.Department)
        {
            deptId = await DepartmentOfAsync(actorId, ct);
            if (deptId is null) return Fail<ProjectTemplateDto>("no_department");
        }

        Project? source = null;
        if (req.SourceProjectId is { } sid)
        {
            // Copying a project into a template exposes its task list: only its members may.
            if (!await db.ProjectMembers.AnyAsync(pm => pm.ProjectId == sid && pm.UserId == actorId, ct))
                return Fail<ProjectTemplateDto>("forbidden");
            source = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sid, ct);
            if (source is null) return Fail<ProjectTemplateDto>("not_found");
        }

        var description = req.Description?.Trim();
        if (string.IsNullOrEmpty(description)) description = null;
        else if (description.Length > MaxDescription) description = description[..MaxDescription];

        var tpl = new ProjectTemplate
        {
            Id = Guid.NewGuid(), Name = name, Description = description, SourceProjectId = source?.Id,
            IsActive = true, CreatedAt = DateTime.UtcNow, CreatedById = actorId,
            Visibility = visibility, DepartmentId = deptId,
            Icon = Trim(req.Icon, 40), Color = Trim(req.Color, 16),
        };
        db.ProjectTemplates.Add(tpl);

        if (source is not null)
        {
            // Offsets are counted from the source project's start (or creation) date.
            var baseDate = (source.StartDate ?? source.CreatedAt).Date;

            var groupMap = new Dictionary<Guid, Guid>();
            if (req.IncludeSections)
            {
                var groups = await db.ProjectGroups.AsNoTracking().Where(g => g.ProjectId == source.Id)
                    .OrderBy(g => g.SortOrder).ToListAsync(ct);
                var gi = 0;
                foreach (var g in groups)
                {
                    var tg = new ProjectTemplateGroup { Id = Guid.NewGuid(), TemplateId = tpl.Id, Name = g.Name, Color = g.Color, SortOrder = gi++ * 100 };
                    groupMap[g.Id] = tg.Id;
                    db.ProjectTemplateGroups.Add(tg);
                }
            }

            var srcTasks = await db.ProjectTasks.AsNoTracking()
                .Where(t => t.ProjectId == source.Id && t.ParentTaskId == null)
                .OrderBy(t => t.Group.SortOrder).ThenBy(t => t.SortOrder)
                .Select(t => new
                {
                    t.Id, t.Title, t.Description, t.Priority, t.DueDate, t.GroupId,
                    Niks = t.Assignees.Select(a => a.User.Nik).ToList(),
                })
                .ToListAsync(ct);
            var taskMap = new Dictionary<Guid, Guid>();
            var i = 0;
            foreach (var t in srcTasks)
            {
                var tt = new ProjectTemplateTask
                {
                    Id = Guid.NewGuid(), TemplateId = tpl.Id, Title = t.Title, Description = t.Description,
                    Priority = t.Priority, SortOrder = i++ * 100,
                    DueOffsetDays = t.DueDate is { } due ? (due.Date - baseDate).Days : null,
                    GroupId = groupMap.TryGetValue(t.GroupId, out var gid) ? gid : null,
                    AssigneeNiks = req.IncludeAssignees && t.Niks.Count > 0 ? string.Join(",", t.Niks.Distinct()) : null,
                };
                taskMap[t.Id] = tt.Id;
                db.ProjectTemplateTasks.Add(tt);
            }

            if (req.IncludeMilestones)
            {
                var ms = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == source.Id)
                    .OrderBy(m => m.DueDate).ThenBy(m => m.CreatedAt)
                    .Select(m => new { m.Title, m.DueDate, Links = m.LinkedTasks.Select(l => l.TaskId).ToList() })
                    .ToListAsync(ct);
                var mi = 0;
                foreach (var m in ms)
                {
                    var linked = m.Links.Where(taskMap.ContainsKey).Select(id => taskMap[id]).ToList();
                    db.ProjectTemplateMilestones.Add(new ProjectTemplateMilestone
                    {
                        Id = Guid.NewGuid(), TemplateId = tpl.Id, Title = m.Title, SortOrder = mi++ * 100,
                        DueOffsetDays = (m.DueDate.Date - baseDate).Days,
                        TaskIds = linked.Count > 0 ? string.Join(",", linked) : null,
                    });
                }
            }
        }
        await db.SaveChangesAsync(ct);

        var list = await ListAsync(actorId, false, ct);
        return new(list.First(x => x.Id == tpl.Id), null);
    }

    public async Task<TemplateResult<ProjectDto>> CreateProjectAsync(Guid actorId, CreateProjectFromTemplateRequest req, CancellationToken ct)
    {
        var tpl = await LoadUsableAsync(actorId, req.TemplateId, ct);
        if (tpl is null) return Fail<ProjectDto>("not_found");

        var startDate = UtcDates.AsUtcDate(req.StartDate ?? DateTime.UtcNow.Date);
        var goal = Trim(req.Goal, 1000);
        var wd = tpl.OffsetsInWorkingDays;
        DateTime At(int offset) => wd ? WorkingDays.AddWorkingDays(startDate, offset) : startDate.AddDays(offset);
        var deadline = req.Deadline ?? req.FinishBy;
        if (deadline is null && wd && tpl.Tasks.Any(t => t.DueOffsetDays != null))
            deadline = At(Math.Max(tpl.Tasks.Max(t => t.DueOffsetDays ?? 0), tpl.Milestones.Count == 0 ? 0 : tpl.Milestones.Max(m => m.DueOffsetDays)));
        if (deadline is not null) deadline = UtcDates.AsUtcDate(deadline.Value);
        var statuses = ParseStatuses(tpl.StatusesJson);
        var createReq = new CreateProjectRequest(string.IsNullOrWhiteSpace(req.Name) ? tpl.Name : req.Name, req.Description ?? tpl.Description,
            req.Year ?? startDate.Year, req.OwnerId ?? actorId, startDate, deadline, null, null,
            string.IsNullOrWhiteSpace(req.OwnerNik) ? null : req.OwnerNik, req.Color);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var created = await projects.CreateProjectAsync(actorId, createReq, ct);
        if (!created.Success || created.Project is null) return Fail<ProjectDto>(created.Error ?? "create_failed");
        var projectId = created.Project.Id;
        var now = DateTime.UtcNow;

        // Sections: the project's default section becomes the first template section.
        var groups = await db.ProjectGroups.Where(g => g.ProjectId == projectId).OrderBy(g => g.SortOrder).ToListAsync(ct);
        var firstGroup = groups[0];
        var groupMap = new Dictionary<Guid, Guid>();
        var tplGroups = tpl.Groups.OrderBy(g => g.SortOrder).ToList();
        for (var gi = 0; gi < tplGroups.Count; gi++)
        {
            var tg = tplGroups[gi];
            if (gi == 0)
            {
                firstGroup.Name = tg.Name; firstGroup.Color = tg.Color;
                groupMap[tg.Id] = firstGroup.Id;
                continue;
            }
            var g = new ProjectGroup { Id = Guid.NewGuid(), ProjectId = projectId, Name = tg.Name, Color = tg.Color, SortOrder = gi * 100 };
            db.ProjectGroups.Add(g);
            groupMap[tg.Id] = g.Id;
        }

        // Statuses: the template's own set, else the project defaults; tasks point at the first todo option.
        if (statuses is not null)
        {
            db.ProjectStatusOptions.RemoveRange(await db.ProjectStatusOptions.Where(o => o.ProjectId == projectId).ToListAsync(ct));
            for (var i = 0; i < statuses.Count; i++)
                db.ProjectStatusOptions.Add(new ProjectStatusOption
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, Label = statuses[i].Label.Trim(), Color = statuses[i].Color, Stage = statuses[i].Stage,
                    SortOrder = (i + 1) * 100, CreatedAt = now,
                });
            await db.SaveChangesAsync(ct);
        }
        else await ProjectStatusService.EnsureStatusesAsync(db, projectId, ct);
        var todoOption = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId && o.Stage == "todo")
            .OrderBy(o => o.SortOrder).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);

        var project = await db.Projects.FirstAsync(p => p.Id == projectId, ct);
        project.Goal = goal;
        var tabs = ParseStrings(tpl.TabsJson).Where(ProjectStatusDefaults.TabKeys.Contains).Distinct().ToList();
        if (tabs.Count > 0) project.TabsJson = System.Text.Json.JsonSerializer.Serialize(tabs);

        // Role assignments: roleKey -> people. Only active users count; they join as members.
        var roleKeys = ParseRoles(tpl.RolesJson).Select(r => r.Key).ToHashSet();
        var roleUsers = new Dictionary<string, List<Guid>>();
        if (req.RoleAssignments is { Count: > 0 })
        {
            var wanted = req.RoleAssignments.Where(kv => roleKeys.Contains(kv.Key)).SelectMany(kv => kv.Value).Distinct().ToList();
            var active = (await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id) && u.IsActive).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
            foreach (var kv in req.RoleAssignments.Where(kv => roleKeys.Contains(kv.Key)))
                roleUsers[kv.Key] = kv.Value.Where(active.Contains).Distinct().ToList();
        }

        // Assignees: only NIKs that still name an active user; they join the project as members.
        var allNiks = tpl.Tasks.SelectMany(t => SplitNiks(t.AssigneeNiks)).Distinct().ToList();
        var users = allNiks.Count == 0 ? new Dictionary<string, Guid>()
            : await db.Users.AsNoTracking().Where(u => allNiks.Contains(u.Nik) && u.IsActive)
                .ToDictionaryAsync(u => u.Nik, u => u.Id, ct);

        var taskMap = new Dictionary<Guid, Guid>();
        foreach (var tt in tpl.Tasks.OrderBy(t => t.SortOrder))
        {
            var task = new ProjectTask
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Title = tt.Title, Description = tt.Description,
                Status = "todo", StatusOptionId = todoOption, Priority = tt.Priority,
                GroupId = tt.GroupId is { } tg && groupMap.TryGetValue(tg, out var g) ? g : firstGroup.Id,
                DueDate = tt.DueOffsetDays is { } d ? At(d) : null,
                StartDate = tt is { DueOffsetDays: { } d2, DurationDays: { } dur and > 1 }
                    ? (wd ? At(Math.Max(0, d2 - (dur - 1))) : startDate.AddDays(d2 - (dur - 1))) : null,
                IsRecurring = !string.IsNullOrWhiteSpace(tt.RecurrenceRule),
                RecurrenceRule = string.IsNullOrWhiteSpace(tt.RecurrenceRule) ? null : tt.RecurrenceRule,
                SortOrder = tt.SortOrder, CreatedById = actorId, CreatedAt = now, UpdatedAt = now,
            };
            db.ProjectTasks.Add(task);
            taskMap[tt.Id] = task.Id;
            var assigned = new HashSet<Guid>();
            foreach (var nik in SplitNiks(tt.AssigneeNiks).Distinct())
                if (users.TryGetValue(nik, out var uid) && assigned.Add(uid))
                    db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = uid, AssignedAt = now });
            if (tt.RoleKey is { } rk && roleUsers.TryGetValue(rk, out var ru))
                foreach (var uid in ru)
                    if (assigned.Add(uid))
                        db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = uid, AssignedAt = now });
        }

        foreach (var tm in tpl.Milestones.OrderBy(m => m.SortOrder))
        {
            var linked = SplitGuids(tm.TaskIds).Where(taskMap.ContainsKey).Select(id => taskMap[id]).Distinct().ToList();
            var m = new ProjectMilestone
            {
                Id = Guid.NewGuid(), ProjectId = projectId, Title = tm.Title, DueDate = At(tm.DueOffsetDays),
                TaskId = linked.Count > 0 ? linked[0] : null, CreatedAt = now,
            };
            db.ProjectMilestones.Add(m);
            foreach (var tid in linked) db.ProjectMilestoneTasks.Add(new ProjectMilestoneTask { MilestoneId = m.Id, TaskId = tid });
        }
        // This creation flow owns the new project: membership and assignments are saved together.
        // Do not ignore a separate AddMemberAsync result after tasks have already been persisted.
        var existingMembers = await db.ProjectMembers.Where(m => m.ProjectId == projectId)
            .Select(m => m.UserId).ToListAsync(ct);
        foreach (var uid in users.Values.Concat(roleUsers.Values.SelectMany(x => x)).Distinct().Except(existingMembers))
            db.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = uid, Role = "member", JoinedAt = now });
        await db.SaveChangesAsync(ct);

        await db.ProjectTemplates.Where(t => t.Id == tpl.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedCount, t => t.UsedCount + 1), ct);

        var result = await projects.GetProjectAsync(projectId, actorId, ct) ?? created.Project;
        await transaction.CommitAsync(ct);
        return new(result, null);
    }

    public async Task<TemplateResult<bool>> DeleteAsync(Guid actorId, Guid templateId, bool isAdmin, CancellationToken ct)
    {
        var tpl = await db.ProjectTemplates.FirstOrDefaultAsync(t => t.Id == templateId && t.IsActive, ct);
        if (tpl is null) return Fail<bool>("not_found");
        if (tpl.IsBuiltIn) return Fail<bool>("built_in");
        if (!isAdmin && tpl.CreatedById != actorId) return Fail<bool>("forbidden");
        tpl.IsActive = false;
        await db.SaveChangesAsync(ct);
        return new(true, null);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IQueryable<ProjectTemplate> Usable(IQueryable<ProjectTemplate> q, Guid actorId, Guid? deptId) =>
        q.Where(t => t.IsActive && (t.IsBuiltIn
            || t.Visibility == ProjectTemplateVisibility.Everyone
            || t.CreatedById == actorId
            || (t.Visibility == ProjectTemplateVisibility.Department && deptId != null && t.DepartmentId == deptId)));

    private async Task<ProjectTemplate?> LoadUsableAsync(Guid actorId, Guid templateId, CancellationToken ct)
    {
        var deptId = await DepartmentOfAsync(actorId, ct);
        return await Usable(db.ProjectTemplates.AsNoTracking(), actorId, deptId)
            .Include(t => t.Tasks).Include(t => t.Groups).Include(t => t.Milestones)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);
    }

    private Task<Guid?> DepartmentOfAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);

    private static ProjectTemplateDetailDto Detail(ProjectTemplate tpl)
    {
        var roles = ParseRoles(tpl.RolesJson)
            .Select(r => new ProjectTemplateRoleDto(r.Key, r.Label, r.Hint, tpl.Tasks.Count(t => t.RoleKey == r.Key))).ToList();
        var perWeek = tpl.OffsetsInWorkingDays ? 5 : 7;
        var summaries = tpl.Groups.OrderBy(g => g.SortOrder).Select(g =>
        {
            var ts = tpl.Tasks.Where(t => t.GroupId == g.Id && t.DueOffsetDays != null).ToList();
            var ends = ts.Select(t => t.DueOffsetDays!.Value).ToList();
            var starts = ts.Select(t => Math.Max(0, t.DueOffsetDays!.Value - ((t.DurationDays ?? 1) - 1))).ToList();
            return new ProjectTemplateGroupSummaryDto(g.Name, g.Color, tpl.Tasks.Count(t => t.GroupId == g.Id),
                starts.Count == 0 ? 1 : starts.Min() / perWeek + 1, ends.Count == 0 ? 1 : ends.Max() / perWeek + 1);
        }).ToList();
        return new(
            tpl.Id, tpl.Name, tpl.Description,
            tpl.Tasks.OrderBy(t => t.SortOrder).Select(t => new ProjectTemplateTaskDto(t.Id, t.Title, t.Description, t.Priority,
                t.DueOffsetDays, t.SortOrder, t.GroupId, SplitNiks(t.AssigneeNiks).ToList(), t.DurationDays, t.RoleKey, t.RecurrenceRule)).ToList(),
            tpl.Groups.OrderBy(g => g.SortOrder).Select(g => new ProjectTemplateGroupDto(g.Id, g.Name, g.Color, g.SortOrder)).ToList(),
            tpl.Milestones.OrderBy(m => m.SortOrder).Select(m => new ProjectTemplateMilestoneDto(m.Id, m.Title, m.DueOffsetDays,
                SplitGuids(m.TaskIds).ToList())).ToList(),
            ParseStatuses(tpl.StatusesJson) ?? ProjectStatusDefaults.Options.Select(o => new ProjectTemplateStatusDto(o.Label, o.Color, o.Stage)).ToList(),
            ParseStrings(tpl.TabsJson).Where(ProjectStatusDefaults.TabKeys.Contains).ToList(),
            roles, ParseStrings(tpl.AutomationsJson), summaries, tpl.Category, tpl.DurationWeeks);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static List<string> ParseStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    private static List<ProjectTemplateRoleDto> ParseRoles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return (System.Text.Json.JsonSerializer.Deserialize<List<ProjectTemplateRoleDto>>(json, JsonOpts) ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Key)).ToList(); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// Null when the template has no (valid) custom status set, so the project defaults apply.
    private static List<ProjectTemplateStatusDto>? ParseStatuses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<ProjectTemplateStatusDto>>(json, JsonOpts);
            if (list is null || list.Count is 0 or > ProjectStatusDefaults.MaxOptions) return null;
            if (list.Any(o => string.IsNullOrWhiteSpace(o.Label) || o.Label.Length > ProjectStatusDefaults.MaxLabelLength
                    || !ProjectStatusDefaults.Stages.Contains(o.Stage) || string.IsNullOrWhiteSpace(o.Color))) return null;
            if (!list.Any(o => o.Stage == "todo") || !list.Any(o => o.Stage == "done")) return null;
            return list;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal static IEnumerable<string> SplitNiks(string? s) =>
        string.IsNullOrWhiteSpace(s) ? [] : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static IEnumerable<Guid> SplitGuids(string? s) =>
        SplitNiks(s).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty);

    private static string? Trim(string? s, int max)
    {
        var v = s?.Trim();
        return string.IsNullOrEmpty(v) ? null : v.Length > max ? v[..max] : v;
    }

    private static TemplateResult<T> Fail<T>(string code) => new(default, code);
}
