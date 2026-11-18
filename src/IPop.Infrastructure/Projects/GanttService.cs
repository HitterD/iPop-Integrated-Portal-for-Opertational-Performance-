using IPop.Application;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Stage 3 Gantt engine: v2 read model, baselines, drag-to-reschedule (preview / apply / undo) and
/// MS Project XML. Scheduling maths lives in <see cref="GanttScheduler"/>.
public sealed partial class GanttService(IPopDbContext db) : IGanttService
{
    public const int MaxBaselines = 5;
    public const int MaxRevertChanges = 500;

    private static GanttResult<T> Fail<T>(string code) => new(default, code);
    private static GanttResult<T> Ok<T>(T value) => new(value, null);

    // ── Access ──
    private async Task<bool> CanViewAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct)
        || await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

    private Task<bool> CanMutateAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member"), ct);

    private Task<bool> CanManageAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
            && (m.Role == "owner" || m.Role == "manager"), ct);

    private async Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        await db.Projects.AnyAsync(p => p.Id == projectId, ct);

    private async Task<List<DateTime>> HolidaysAsync(Guid projectId, CancellationToken ct) =>
        await db.WorkCalendarEntries.AsNoTracking()
            .Where(e => e.ProjectId == projectId && (e.Kind == "holiday" || e.Kind == "offday"))
            .Select(e => e.Date).ToListAsync(ct);

    private async Task<List<SchedLink>> LinksAsync(Guid projectId, CancellationToken ct) =>
        (await db.ProjectTaskDependencies.AsNoTracking().Where(d => d.Task.ProjectId == projectId)
            .Select(d => new { d.TaskId, d.DependsOnTaskId, d.LinkType, d.LagDays }).ToListAsync(ct))
        .Select(l => new SchedLink(l.DependsOnTaskId, l.TaskId, l.LinkType, l.LagDays)).ToList();

    // ──────────────────────────────────────────────────────────────────────────
    // Baselines
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<GanttResult<BaselineDto>> CreateBaselineAsync(Guid actorId, Guid projectId, CreateBaselineRequest req, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<BaselineDto>("project_not_found");
        if (!await CanManageAsync(projectId, actorId, ct)) return Fail<BaselineDto>("forbidden");
        var existing = await db.ProjectBaselines.Where(b => b.ProjectId == projectId).ToListAsync(ct);
        if (existing.Count >= MaxBaselines) return Fail<BaselineDto>("too_many_baselines");

        string name;
        if (string.IsNullOrWhiteSpace(req?.Name))
        {
            var n = existing.Count + 1;
            while (existing.Any(b => b.Name == $"Plan {n}")) n++;
            name = $"Plan {n}";
        }
        else
        {
            name = req.Name.Trim();
            if (name.Length > 60) return Fail<BaselineDto>("invalid_name");
        }

        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Id, t.StartDate, t.DueDate }).ToListAsync(ct);
        foreach (var b in existing) b.IsActive = false;
        var baseline = new ProjectBaseline
        {
            Id = Guid.NewGuid(), ProjectId = projectId, Name = name, CreatedAt = DateTime.UtcNow,
            CreatedById = actorId, IsActive = true
        };
        db.ProjectBaselines.Add(baseline);
        db.ProjectBaselineTasks.AddRange(tasks.Select(t => new ProjectBaselineTask
            { BaselineId = baseline.Id, TaskId = t.Id, Start = t.StartDate, Due = t.DueDate }));
        await db.SaveChangesAsync(ct);
        var by = await db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";
        return Ok(new BaselineDto(baseline.Id, name, baseline.CreatedAt, by, tasks.Count, true));
    }

    public async Task<GanttResult<IReadOnlyList<BaselineDto>>> ListBaselinesAsync(Guid actorId, Guid projectId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, actorId, ct))
            return Fail<IReadOnlyList<BaselineDto>>(await ProjectExistsAsync(projectId, ct) ? "forbidden" : "project_not_found");
        return Ok(await BaselineListAsync(projectId, ct));
    }

    private async Task<IReadOnlyList<BaselineDto>> BaselineListAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.ProjectBaselines.AsNoTracking().Where(b => b.ProjectId == projectId)
            .OrderBy(b => b.CreatedAt)
            .Select(b => new { b.Id, b.Name, b.CreatedAt, By = b.CreatedBy.Name, N = b.Tasks.Count, b.IsActive }).ToListAsync(ct);
        return rows.Select(r => new BaselineDto(r.Id, r.Name, r.CreatedAt, r.By, r.N, r.IsActive)).ToList();
    }

    public async Task<GanttResult<BaselineDto>> ActivateBaselineAsync(Guid actorId, Guid projectId, Guid baselineId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<BaselineDto>("project_not_found");
        if (!await CanManageAsync(projectId, actorId, ct)) return Fail<BaselineDto>("forbidden");
        var all = await db.ProjectBaselines.Where(b => b.ProjectId == projectId).ToListAsync(ct);
        if (all.All(b => b.Id != baselineId)) return Fail<BaselineDto>("baseline_not_found");
        foreach (var b in all) b.IsActive = b.Id == baselineId;
        await db.SaveChangesAsync(ct);
        return Ok((await BaselineListAsync(projectId, ct)).First(b => b.Id == baselineId));
    }

    public async Task<GanttResult<bool>> DeleteBaselineAsync(Guid actorId, Guid projectId, Guid baselineId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<bool>("project_not_found");
        if (!await CanManageAsync(projectId, actorId, ct)) return Fail<bool>("forbidden");
        var b = await db.ProjectBaselines.FirstOrDefaultAsync(x => x.Id == baselineId && x.ProjectId == projectId, ct);
        if (b is null) return Fail<bool>("baseline_not_found");
        db.ProjectBaselineTasks.RemoveRange(db.ProjectBaselineTasks.Where(x => x.BaselineId == baselineId));
        db.ProjectBaselines.Remove(b);
        await db.SaveChangesAsync(ct);
        if (b.IsActive)
        {
            // Keep one baseline showing: promote the newest remaining one.
            var next = await db.ProjectBaselines.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (next is not null) { next.IsActive = true; await db.SaveChangesAsync(ct); }
        }
        return Ok(true);
    }

    /// The requested baseline, else the active one; its per-task dates.
    private async Task<(Guid? Id, Dictionary<Guid, (DateTime? Start, DateTime? Due)> Dates)> BaselineDatesAsync(
        Guid projectId, Guid? baselineId, CancellationToken ct)
    {
        var q = db.ProjectBaselines.AsNoTracking().Where(b => b.ProjectId == projectId);
        var id = baselineId.HasValue
            ? await q.Where(b => b.Id == baselineId).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct)
            : await q.Where(b => b.IsActive).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        if (id is null) return (null, []);
        var rows = await db.ProjectBaselineTasks.AsNoTracking().Where(x => x.BaselineId == id)
            .Select(x => new { x.TaskId, x.Start, x.Due }).ToListAsync(ct);
        return (id, rows.ToDictionary(r => r.TaskId, r => (r.Start, r.Due)));
    }

    private static int? Variance(WorkCalendar cal, DateTime? due, DateTime? start, (DateTime? Start, DateTime? Due) b)
    {
        var finish = due ?? start;
        var bFinish = b.Due ?? b.Start;
        if (finish is null || bFinish is null) return null;
        return cal.FinishOrdinal(finish.Value) - cal.FinishOrdinal(bFinish.Value);
    }

    public async Task<ProjectGanttDto> WithBaselineAsync(ProjectGanttDto gantt, Guid? baselineId, CancellationToken ct)
    {
        var (id, dates) = await BaselineDatesAsync(gantt.ProjectId, baselineId, ct);
        if (id is null) return gantt;
        var holidays = await HolidaysAsync(gantt.ProjectId, ct);
        var all = gantt.Tasks.SelectMany(t => new[] { t.StartDate, t.DueDate }).Where(d => d.HasValue).Select(d => d!.Value)
            .Concat(dates.Values.SelectMany(v => new[] { v.Start, v.Due }).Where(d => d.HasValue).Select(d => d!.Value)).ToList();
        var cal = WorkCalendar.Create(all, holidays);
        var tasks = gantt.Tasks.Select(t => dates.TryGetValue(t.Id, out var b)
            ? t with { BaselineStart = b.Start, BaselineDue = b.Due, VarianceDays = Variance(cal, t.DueDate, t.StartDate, b) }
            : t).ToList();
        return gantt with { Tasks = tasks };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Gantt v2
    // ──────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> StageLabels = new()
    {
        ["todo"] = "To do", ["doing"] = "In progress", ["review"] = "In review", ["done"] = "Done"
    };

    public async Task<GanttResult<GanttV2Dto>> GetGanttV2Async(Guid actorId, Guid projectId, Guid? baselineId, Guid? groupId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null || !await CanViewAsync(projectId, actorId, ct)) return Fail<GanttV2Dto>("project_not_found");

        var groups = await db.ProjectGroups.AsNoTracking().Where(g => g.ProjectId == projectId)
            .OrderBy(g => g.SortOrder).ToListAsync(ct);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var optionLabels = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId)
            .ToDictionaryAsync(o => o.Id, o => o.Label, ct);
        var links = await LinksAsync(projectId, ct);
        var holidayEntries = await db.WorkCalendarEntries.AsNoTracking()
            .Where(e => e.ProjectId == projectId && (e.Kind == "holiday" || e.Kind == "offday")).OrderBy(e => e.Date).ToListAsync(ct);
        var holidays = holidayEntries.Select(e => e.Date).ToList();
        var (usedBaseline, baselineDates) = await BaselineDatesAsync(projectId, baselineId, ct);
        var baselines = await BaselineListAsync(projectId, ct);
        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == projectId)
            .Include(m => m.LinkedTasks).OrderBy(m => m.DueDate).ToListAsync(ct);

        var analysis = GanttScheduler.Analyze(tasks.Select(ProjectService.ToSched), links, holidays);
        var dependsOn = links.GroupBy(l => l.SuccessorId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<GanttLinkDto>)g.Select(l => new GanttLinkDto(l.PredecessorId, l.Type, l.LagDays)).ToList());

        var allDates = tasks.SelectMany(t => new[] { t.StartDate, t.DueDate }).Where(d => d.HasValue).Select(d => d!.Value)
            .Concat(baselineDates.Values.SelectMany(v => new[] { v.Start, v.Due }).Where(d => d.HasValue).Select(d => d!.Value))
            .Concat(milestones.Select(m => m.DueDate)).ToList();
        if (project.StartDate.HasValue) allDates.Add(project.StartDate.Value);
        if (project.Deadline.HasValue) allDates.Add(project.Deadline.Value);
        var cal = WorkCalendar.Create(allDates, holidays);

        // WBS: groups numbered by order, top-level tasks within the group, subtasks under them.
        var wbs = new Dictionary<Guid, string>();
        var ordered = new List<ProjectTask>();
        var byParent = tasks.Where(t => t.ParentTaskId.HasValue).ToLookup(t => t.ParentTaskId!.Value);
        var taskIds = tasks.Select(t => t.Id).ToHashSet();
        void Walk(ProjectTask t, string path)
        {
            wbs[t.Id] = path; ordered.Add(t);
            var i = 1;
            foreach (var c in byParent[t.Id]) Walk(c, $"{path}.{i++}");
        }
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var top = tasks.Where(t => t.GroupId == groups[gi].Id && (t.ParentTaskId is null || !taskIds.Contains(t.ParentTaskId.Value))).ToList();
            for (var ti = 0; ti < top.Count; ti++) Walk(top[ti], $"{gi + 1}.{ti + 1}");
        }

        var rows = new List<GanttRowDto>();
        foreach (var t in ordered)
        {
            if (groupId.HasValue && t.GroupId != groupId.Value) continue;
            var subs = byParent[t.Id].ToList();
            var percent = t.Status == "done" ? 100 : subs.Count > 0 ? (int)Math.Round(subs.Count(s => s.Status == "done") * 100.0 / subs.Count) : 0;
            analysis.Floats.TryGetValue(t.Id, out var fl);
            var scheduled = analysis.Floats.ContainsKey(t.Id);
            baselineDates.TryGetValue(t.Id, out var b);
            var hasBaseline = baselineDates.ContainsKey(t.Id);
            var label = t.StatusOptionId is { } oid && optionLabels.TryGetValue(oid, out var l) ? l : StageLabels.GetValueOrDefault(t.Status, t.Status);
            rows.Add(new GanttRowDto(
                t.Id, t.GroupId, t.ParentTaskId, wbs[t.Id], t.Title, label, t.StatusOptionId, t.Status, t.Priority,
                t.StartDate, t.DueDate, scheduled ? fl!.DurationDays : 0, percent,
                t.Assignees.Select(a => new GanttAssigneeDto(a.UserId, a.User.Name, a.User.Nik)).ToList(),
                scheduled && fl!.IsCritical, scheduled ? fl!.SlackDays : null,
                t.DeadlineDate, t.ConstraintType, t.ConstraintDate,
                hasBaseline ? b.Start : null, hasBaseline ? b.Due : null,
                hasBaseline ? Variance(cal, t.DueDate, t.StartDate, b) : null,
                dependsOn.GetValueOrDefault(t.Id) ?? []));
        }

        var critical = analysis.CriticalPath.ToHashSet();
        var ms = milestones.Select(m => new GanttMilestoneDto(m.Id, m.Title, m.DueDate, m.AchievedAt,
            m.LinkedTasks.Select(x => x.TaskId).ToList(), m.LinkedTasks.Any(x => critical.Contains(x.TaskId)))).ToList();

        var starts = tasks.Where(t => t.StartDate.HasValue).Select(t => t.StartDate!.Value)
            .Concat(tasks.Where(t => t.StartDate is null && t.DueDate.HasValue).Select(t => t.DueDate!.Value))
            .Concat(baselineDates.Values.Select(v => v.Start ?? v.Due).Where(d => d.HasValue).Select(d => d!.Value))
            .Concat(milestones.Select(m => m.DueDate)).ToList();
        var ends = tasks.Where(t => t.DueDate.HasValue || t.StartDate.HasValue).Select(t => (t.DueDate ?? t.StartDate)!.Value)
            .Concat(baselineDates.Values.Select(v => v.Due ?? v.Start).Where(d => d.HasValue).Select(d => d!.Value))
            .Concat(milestones.Select(m => m.DueDate)).ToList();
        if (project.StartDate.HasValue) starts.Add(project.StartDate.Value);
        if (project.Deadline.HasValue) ends.Add(project.Deadline.Value);
        var today = DateTime.UtcNow.Date;
        var wStart = starts.Count > 0 ? starts.Min().Date : today;
        var wEnd = ends.Count > 0 ? ends.Max().Date : wStart.AddDays(30);
        if (wEnd < wStart) wEnd = wStart;

        var dto = new GanttV2Dto(
            new GanttV2ProjectDto(project.Id, project.Name, project.StartDate, project.Deadline),
            new GanttWindowDto(UtcDates.AsUtcDate(wStart), UtcDates.AsUtcDate(wEnd)),
            groups.Where(g => !groupId.HasValue || g.Id == groupId.Value).Select(g => new GanttGroupDto(g.Id, g.Name, g.Color, g.SortOrder)).ToList(),
            rows, ms,
            holidayEntries.Select(e => new GanttHolidayDto(e.Date, e.Title)).ToList(),
            analysis.Conflicts.Select(c => new GanttConflictDto(c.FromTaskId, c.ToTaskId)).ToList(),
            analysis.CriticalPath, analysis.ProjectFinish, baselines, usedBaseline, analysis.ConstraintViolations);
        return Ok(dto);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reschedule
    // ──────────────────────────────────────────────────────────────────────────

    private sealed record Plan(ReschedulePreviewDto Preview, List<ProjectTask> Tracked);

    private async Task<GanttResult<(ReschedulePreviewDto Preview, Dictionary<Guid, ProjectTask> Tasks)>> PlanAsync(
        Guid actorId, Guid projectId, RescheduleRequest req, bool track, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<(ReschedulePreviewDto, Dictionary<Guid, ProjectTask>)>("project_not_found");
        if (!await CanMutateAsync(projectId, actorId, ct)) return Fail<(ReschedulePreviewDto, Dictionary<Guid, ProjectTask>)>("forbidden");
        var q = track ? db.ProjectTasks.Where(t => t.ProjectId == projectId) : db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId);
        var tasks = await q.ToListAsync(ct);
        var byId = tasks.ToDictionary(t => t.Id);
        if (!byId.ContainsKey(req.TaskId)) return Fail<(ReschedulePreviewDto, Dictionary<Guid, ProjectTask>)>("task_not_found");

        var outcome = GanttScheduler.PreviewReschedule(tasks.Select(ProjectService.ToSched), await LinksAsync(projectId, ct),
            await HolidaysAsync(projectId, ct), req.TaskId, UtcDates.AsUtcDate(req.NewStart).Date, req.Mode,
            req.NewDue is { } d ? UtcDates.AsUtcDate(d).Date : null);
        if (outcome.Error is not null) return Fail<(ReschedulePreviewDto, Dictionary<Guid, ProjectTask>)>(outcome.Error);

        var preview = new ReschedulePreviewDto(
            outcome.Changes.Select(c => new RescheduleChangeDto(c.TaskId, byId[c.TaskId].Title, c.OldStart, c.OldDue, c.NewStart, c.NewDue)).ToList(),
            outcome.Conflicts.Select(c => new GanttConflictDto(c.FromTaskId, c.ToTaskId)).ToList(),
            outcome.NewFinish, outcome.OldFinish, outcome.ConstraintViolations);
        return Ok((preview, byId));
    }

    public async Task<GanttResult<ReschedulePreviewDto>> PreviewRescheduleAsync(Guid actorId, Guid projectId, RescheduleRequest req, CancellationToken ct)
    {
        var r = await PlanAsync(actorId, projectId, req, track: false, ct);
        return r.Success ? Ok(r.Value.Preview) : Fail<ReschedulePreviewDto>(r.Error!);
    }

    public async Task<GanttResult<ReschedulePreviewDto>> ApplyRescheduleAsync(Guid actorId, Guid projectId, RescheduleRequest req, CancellationToken ct)
    {
        var r = await PlanAsync(actorId, projectId, req, track: true, ct);
        if (!r.Success) return Fail<ReschedulePreviewDto>(r.Error!);
        var (preview, byId) = r.Value;
        if (preview.Changes.Count == 0) return Ok(preview);

        var now = DateTime.UtcNow;
        foreach (var c in preview.Changes)
        {
            var t = byId[c.TaskId];
            t.StartDate = UtcDates.AsUtcDate(c.NewStart);
            t.DueDate = UtcDates.AsUtcDate(c.NewDue);
            t.UpdatedAt = now;
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = t.Id, ActorId = actorId, Action = "rescheduled",
                OldValue = Fmt(c.OldStart, c.OldDue), NewValue = Fmt(c.NewStart, c.NewDue), CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        return Ok(preview);

        static string Fmt(DateTime? s, DateTime? d) => $"{s:yyyy-MM-dd} → {d:yyyy-MM-dd}";
    }

    public async Task<GanttResult<RescheduleRevertResultDto>> RevertRescheduleAsync(Guid actorId, Guid projectId, RescheduleRevertRequest req, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return Fail<RescheduleRevertResultDto>("project_not_found");
        if (!await CanMutateAsync(projectId, actorId, ct)) return Fail<RescheduleRevertResultDto>("forbidden");
        var changes = (req?.Changes ?? []).GroupBy(c => c.TaskId).Select(g => g.Last()).ToList();
        if (changes.Count == 0) return Ok(new RescheduleRevertResultDto(0));
        if (changes.Count > MaxRevertChanges) return Fail<RescheduleRevertResultDto>("too_many_changes");
        var ids = changes.Select(c => c.TaskId).ToList();
        var tasks = await db.ProjectTasks.Where(t => t.ProjectId == projectId && ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        if (tasks.Count != ids.Count) return Fail<RescheduleRevertResultDto>("task_not_found");

        var now = DateTime.UtcNow;
        foreach (var c in changes)
        {
            var t = tasks[c.TaskId];
            db.ProjectTaskActivities.Add(new ProjectTaskActivity
            {
                Id = Guid.NewGuid(), TaskId = t.Id, ActorId = actorId, Action = "rescheduled",
                OldValue = $"{t.StartDate:yyyy-MM-dd} → {t.DueDate:yyyy-MM-dd}", NewValue = $"{c.Start:yyyy-MM-dd} → {c.Due:yyyy-MM-dd} (undo)", CreatedAt = now
            });
            t.StartDate = UtcDates.AsUtcDate(c.Start);
            t.DueDate = UtcDates.AsUtcDate(c.Due);
            t.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return Ok(new RescheduleRevertResultDto(changes.Count));
    }
}
