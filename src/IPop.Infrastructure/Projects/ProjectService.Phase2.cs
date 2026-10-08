using System.Text.Json;
using System.Text.RegularExpressions;
using IPop.Application.Projects;
using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using static IPop.Application.UtcDates;

namespace IPop.Infrastructure.Projects;

/// Phase 2 of the work-management spec: dependencies (table-backed), recurring completion,
/// soft delete with undo, bulk actions, saved views and the calendar feed.
public sealed partial class ProjectService
{
    private const int MaxDependencies = 20;
    private const int MaxBulkTasks = 200;
    private const int MaxSavedViewsPerUser = 20;
    private const int MaxCalendarDays = 62;
    private const int MaxCalendarTasks = 500;
    private static readonly TimeSpan RestoreWindow = TimeSpan.FromHours(24);
    private static readonly string[] TaskStatuses = ["todo", "doing", "review", "done"];
    private static readonly string[] TaskPriorities = ["urgent", "high", "medium", "normal", "low"];
    private static readonly Regex EvidenceBlock = new(@"\[EVIDENCE_DATA\][\s\S]*?\[/EVIDENCE_DATA\]", RegexOptions.Compiled);

    // ──────────────────────────────────────────────────────────────────────────
    // Dependencies
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<TaskResultDto> SetTaskDependenciesAsync(Guid actorId, Guid taskId, SetTaskDependenciesRequest req, CancellationToken ct)
    {
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new(false, null, "task_not_found");
        if (!await CanMutateTasksAsync(actorId, task.ProjectId, ct))
            return new(false, null, "forbidden");

        var ids = (req.DependsOn ?? []).Distinct().ToList();
        if (ids.Count > MaxDependencies) return new(false, null, "too_many_dependencies");
        if (ids.Contains(taskId)) return new(false, null, "invalid_dependency");
        if (task.ParentTaskId is { } parentId && ids.Contains(parentId)) return new(false, null, "invalid_dependency");

        var targets = await db.ProjectTasks.AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.ProjectId == task.ProjectId)
            .Select(t => new { t.Id, t.Title }).ToListAsync(ct);
        if (targets.Count != ids.Count) return new(false, null, "invalid_dependency");

        // Would any new prerequisite (transitively) wait on this task? Walk "depends on" edges from each.
        var edges = await ProjectDependencyEdgesAsync(task.ProjectId, ct);
        edges.Remove(taskId);
        var seen = new HashSet<Guid>();
        var queue = new Queue<Guid>(ids);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            if (n == taskId) return new(false, null, "dependency_cycle");
            if (!seen.Add(n) || !edges.TryGetValue(n, out var next)) continue;
            foreach (var x in next) queue.Enqueue(x);
        }

        var existing = await db.ProjectTaskDependencies.Where(d => d.TaskId == taskId).ToListAsync(ct);
        var before = existing.Select(d => d.DependsOnTaskId).ToHashSet();
        if (before.SetEquals(ids)) return new(true, await MapToTaskDtoAsync(taskId, ct), null);

        db.ProjectTaskDependencies.RemoveRange(existing.Where(d => !ids.Contains(d.DependsOnTaskId)));
        db.ProjectTaskDependencies.AddRange(ids.Where(id => !before.Contains(id))
            .Select(id => new ProjectTaskDependency { TaskId = taskId, DependsOnTaskId = id }));
        var titles = string.Join(", ", targets.Select(t => t.Title));
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId, Action = "dependencies_changed",
            OldValue = before.Count.ToString(), NewValue = titles.Length > 500 ? titles[..500] : titles,
            CreatedAt = DateTime.UtcNow
        });
        var tracked = await db.ProjectTasks.FirstAsync(t => t.Id == taskId, ct);
        tracked.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new(true, await MapToTaskDtoAsync(taskId, ct), null);
    }

    /// taskId → the ids it waits on, for live (not deleted) tasks of one project.
    private async Task<Dictionary<Guid, List<Guid>>> ProjectDependencyEdgesAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.ProjectTaskDependencies.AsNoTracking()
            .Where(d => d.Task.ProjectId == projectId)
            .Join(db.ProjectTasks, d => d.DependsOnTaskId, t => t.Id, (d, _) => new { d.TaskId, d.DependsOnTaskId })
            .Join(db.ProjectTasks, d => d.TaskId, t => t.Id, (d, _) => d)
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.Select(r => r.DependsOnTaskId).ToList());
    }

    /// Unfinished prerequisites of each task (deleted prerequisites no longer block).
    private async Task<Dictionary<Guid, List<Guid>>> UnmetDependenciesAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];
        var rows = await db.ProjectTaskDependencies.AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Join(db.ProjectTasks.Where(t => t.Status != "done"), d => d.DependsOnTaskId, t => t.Id, (d, _) => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TaskId).ToDictionary(g => g.Key, g => g.Select(r => r.DependsOnTaskId).ToList());
    }

    private async Task<Dictionary<Guid, List<DependencyRefDto>>> DependencyRefsAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct)
    {
        if (taskIds.Count == 0) return [];
        var rows = await db.ProjectTaskDependencies.AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Join(db.ProjectTasks, d => d.DependsOnTaskId, t => t.Id, (d, t) => new { d.TaskId, t.Id, t.Title, t.Status })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.TaskId).ToDictionary(g => g.Key,
            g => g.OrderBy(r => r.Title).Select(r => new DependencyRefDto(r.Id, r.Title, r.Status)).ToList());
    }

    /// Adds DependsOn/IsBlocked to already-mapped task DTOs with one query per page.
    private async Task<List<ProjectTaskDto>> WithDependenciesAsync(IReadOnlyList<ProjectTaskDto> dtos, CancellationToken ct)
    {
        var refs = await DependencyRefsAsync(dtos.Select(d => d.Id).ToList(), ct);
        return dtos.Select(d => refs.TryGetValue(d.Id, out var deps)
            ? d with { DependsOn = deps, IsBlocked = d.Status != "done" && deps.Any(x => x.Status != "done") }
            : d with { DependsOn = [] }).ToList();
    }

    /// Create-time dependencies still arrive as the legacy comma-separated string; keep the
    /// ids that name live tasks in the same project and store them as rows.
    private async Task AddCreateTimeDependenciesAsync(ProjectTask task, string? dependencies, CancellationToken ct)
    {
        var ids = ParseDependencyIds(dependencies).Where(id => id != task.Id).Distinct().Take(MaxDependencies).ToList();
        if (ids.Count == 0) return;
        var valid = await db.ProjectTasks.Where(t => ids.Contains(t.Id) && t.ProjectId == task.ProjectId)
            .Select(t => t.Id).ToListAsync(ct);
        db.ProjectTaskDependencies.AddRange(valid.Select(id => new ProjectTaskDependency { TaskId = task.Id, DependsOnTaskId = id }));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Recurring
    // ──────────────────────────────────────────────────────────────────────────

    /// Called when a task first becomes done. Queues the next instance on the context (saved
    /// with the caller's SaveChanges) and clears the rule on the finished task so the chain
    /// continues from the newest instance only and re-completing never spawns twice.
    private async Task<Guid?> QueueNextRecurrenceAsync(ProjectTask task, Guid actorId, CancellationToken ct)
    {
        if (!task.IsRecurring || TaskRecurrence.Parse(task.RecurrenceRule) is not { } rule) return null;

        var now = DateTime.UtcNow;
        var baseDate = task.DueDate ?? task.StartDate ?? DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        DateTime? due = null, start = null;
        if (task.DueDate is { } oldDue)
        {
            due = AsUtcDate(rule.Next(oldDue));
            if (task.StartDate is { } oldStart) start = due.Value - (oldDue.Date - oldStart.Date);
        }
        else if (task.StartDate is { } onlyStart) start = AsUtcDate(rule.Next(onlyStart));
        else due = AsUtcDate(rule.Next(baseDate));

        var maxSort = await db.ProjectTasks
            .Where(t => t.ProjectId == task.ProjectId && t.Status == "todo" && t.ParentTaskId == task.ParentTaskId)
            .Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? 0;
        var description = task.Description is null ? null : EvidenceBlock.Replace(task.Description, "").Trim();
        var next = new ProjectTask
        {
            Id = Guid.NewGuid(), ProjectId = task.ProjectId, GroupId = task.GroupId, ParentTaskId = task.ParentTaskId,
            Title = task.Title, Description = string.IsNullOrEmpty(description) ? null : description,
            Status = "todo", Priority = task.Priority, StartDate = start, DueDate = due,
            EstimatedHours = task.EstimatedHours, SortOrder = maxSort + 100, Tags = task.Tags,
            IsRecurring = true, RecurrenceRule = rule.ToString(),
            CreatedById = actorId, CreatedAt = now, UpdatedAt = now
        };
        db.ProjectTasks.Add(next);

        var assignees = await db.ProjectTaskAssignees.AsNoTracking().Where(a => a.TaskId == task.Id).ToListAsync(ct);
        db.ProjectTaskAssignees.AddRange(assignees.Select(a => new ProjectTaskAssignee
            { TaskId = next.Id, UserId = a.UserId, Role = a.Role, AssignedAt = now }));
        var fields = await db.ProjectTaskFieldValues.AsNoTracking().Where(v => v.ProjectTaskId == task.Id).ToListAsync(ct);
        db.ProjectTaskFieldValues.AddRange(fields.Select(v => new ProjectTaskFieldValue
            { Id = Guid.NewGuid(), ProjectTaskId = next.Id, ProjectFieldId = v.ProjectFieldId, Value = v.Value, UpdatedAt = now }));
        var deps = await db.ProjectTaskDependencies.AsNoTracking().Where(d => d.TaskId == task.Id)
            .Join(db.ProjectTasks, d => d.DependsOnTaskId, t => t.Id, (d, _) => d.DependsOnTaskId).ToListAsync(ct);
        db.ProjectTaskDependencies.AddRange(deps.Select(id => new ProjectTaskDependency { TaskId = next.Id, DependsOnTaskId = id }));
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ActorId = actorId, Action = "recurrence_spawned",
            NewValue = (due ?? start)?.ToString("yyyy-MM-dd"), CreatedAt = now
        });

        task.RecurrenceRule = null;
        return next.Id;
    }

    /// Validates the drawer's "Ulangi" fields. Returns the error code, or null when applied.
    private static string? ApplyRecurrence(ProjectTask task, bool? isRecurring, string? rule)
    {
        if (isRecurring == false)
        {
            task.IsRecurring = false;
            task.RecurrenceRule = null;
            return null;
        }
        if (rule is not null)
        {
            if (rule.Trim().Length == 0)
            {
                task.IsRecurring = false;
                task.RecurrenceRule = null;
                return null;
            }
            if (TaskRecurrence.Parse(rule) is not { } parsed) return "invalid_recurrence";
            task.RecurrenceRule = parsed.ToString();
            task.IsRecurring = true;
            return null;
        }
        if (isRecurring == true)
        {
            if (!TaskRecurrence.IsValid(task.RecurrenceRule)) return "invalid_recurrence";
            task.IsRecurring = true;
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Soft delete + undo
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<DeleteTaskResultDto> DeleteTaskAsync(Guid actorId, Guid taskId, CancellationToken ct)
    {
        var task = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return new(false, null, [], "task_not_found");
        if (!await CanMutateTasksAsync(actorId, task.ProjectId, ct))
            return new(false, null, [], "forbidden");

        var batchId = Guid.NewGuid();
        var ids = await MarkDeletedAsync(task.ProjectId, [taskId], batchId, ct);
        await db.SaveChangesAsync(ct);
        await RecalculateProgressAsync(task.ProjectId, ct);
        return new(true, batchId, ids, null);
    }

    /// Flags the roots and every live descendant with one batch id (not saved yet).
    private async Task<List<Guid>> MarkDeletedAsync(Guid projectId, IReadOnlyCollection<Guid> rootIds, Guid batchId, CancellationToken ct)
    {
        var children = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.ParentTaskId != null)
            .Select(t => new { t.Id, Parent = t.ParentTaskId!.Value }).ToListAsync(ct);
        var byParent = children.ToLookup(c => c.Parent, c => c.Id);
        var all = new HashSet<Guid>();
        var queue = new Queue<Guid>(rootIds);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!all.Add(id)) continue;
            foreach (var c in byParent[id]) queue.Enqueue(c);
        }
        var ids = all.ToList();
        var now = DateTime.UtcNow;
        foreach (var t in await db.ProjectTasks.Where(t => ids.Contains(t.Id)).ToListAsync(ct))
        {
            t.DeletedAt = now;
            t.DeleteBatchId = batchId;
            t.UpdatedAt = now;
        }
        return ids;
    }

    public async Task<MemberResultDto> RestoreTasksAsync(Guid actorId, Guid projectId, Guid batchId, CancellationToken ct)
    {
        if (!await CanMutateTasksAsync(actorId, projectId, ct))
            return new(false, "forbidden");
        var since = DateTime.UtcNow - RestoreWindow;
        var tasks = await db.ProjectTasks.IgnoreQueryFilters()
            .Where(t => t.ProjectId == projectId && t.DeleteBatchId == batchId && t.DeletedAt != null && t.DeletedAt >= since)
            .ToListAsync(ct);
        if (tasks.Count == 0) return new(false, "restore_expired");

        var inBatch = tasks.Select(t => t.Id).ToHashSet();
        var parentIds = tasks.Where(t => t.ParentTaskId is { } p && !inBatch.Contains(p)).Select(t => t.ParentTaskId!.Value).Distinct().ToList();
        // Subtasks come back only under a live parent, and into that parent's current group.
        var liveParents = await db.ProjectTasks.AsNoTracking().Where(t => parentIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.GroupId, ct);
        var now = DateTime.UtcNow;
        foreach (var t in tasks)
        {
            if (t.ParentTaskId is { } p && !inBatch.Contains(p))
            {
                if (!liveParents.TryGetValue(p, out var group)) continue;
                t.GroupId = group;
            }
            t.DeletedAt = null;
            t.DeleteBatchId = null;
            t.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await RecalculateProgressAsync(projectId, ct);
        return new(true, null);
    }

    /// Hard-deletes tasks that have sat in the bin past <paramref name="olderThanUtc"/>, leaves first
    /// (the parent FK is Restrict). Dependency, assignee and discussion rows cascade in the database.
    public async Task<int> PurgeDeletedTasksAsync(DateTime olderThanUtc, CancellationToken ct)
    {
        var total = 0;
        for (var pass = 0; pass < 10; pass++)
        {
            var leaves = await db.ProjectTasks.IgnoreQueryFilters()
                .Where(t => t.DeletedAt != null && t.DeletedAt < olderThanUtc
                    && !db.ProjectTasks.IgnoreQueryFilters().Any(c => c.ParentTaskId == t.Id))
                .Select(t => t.Id).Take(1000).ToListAsync(ct);
            if (leaves.Count == 0) break;
            await db.ProjectTaskDependencies.Where(d => leaves.Contains(d.TaskId) || leaves.Contains(d.DependsOnTaskId)).ExecuteDeleteAsync(ct);
            total += await db.ProjectTasks.IgnoreQueryFilters().Where(t => leaves.Contains(t.Id)).ExecuteDeleteAsync(ct);
        }
        return total;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bulk actions
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<BulkTaskResultDto> BulkUpdateTasksAsync(Guid actorId, Guid projectId, BulkTaskRequest req, CancellationToken ct)
    {
        BulkTaskResultDto Fail(string code) => new(false, 0, [], null, code);
        if (!await CanMutateTasksAsync(actorId, projectId, ct)) return Fail("forbidden");
        var ids = (req.TaskIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxBulkTasks) return Fail("invalid_tasks");
        var tasks = await db.ProjectTasks.Where(t => ids.Contains(t.Id) && t.ProjectId == projectId).ToListAsync(ct);
        if (tasks.Count != ids.Count) return Fail("invalid_tasks");

        var action = (req.Action ?? "").Trim().ToLowerInvariant();
        var value = req.Value?.Trim() ?? "";
        var skipped = new List<BulkSkippedDto>();
        var now = DateTime.UtcNow;
        var updated = 0;
        Guid? batchId = null;
        void Track(Guid taskId, string act, string? oldVal, string? newVal) => db.ProjectTaskActivities.Add(new ProjectTaskActivity
            { Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId, Action = act, OldValue = oldVal, NewValue = newVal, CreatedAt = now });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        switch (action)
        {
            case "status":
            {
                if (!TaskStatuses.Contains(value)) return Fail("invalid_value");
                var evidence = string.IsNullOrWhiteSpace(req.Evidence) || value == "todo" ? null : req.Evidence.Trim();
                if (evidence is not null && !EvidenceBlock.IsMatch(evidence)) return Fail("invalid_evidence");
                var unmet = value == "done" ? await UnmetDependenciesAsync(ids, ct) : [];
                var nextSort = (await db.ProjectTasks.Where(t => t.ProjectId == projectId && t.Status == value)
                    .Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? 0) + 100;
                foreach (var t in tasks)
                {
                    if (t.Status == value) { skipped.Add(new(t.Id, "unchanged")); continue; }
                    if (unmet.ContainsKey(t.Id)) { skipped.Add(new(t.Id, "unmet_dependencies")); continue; }
                    if (evidence is not null)
                    {
                        var clean = t.Description is null ? "" : EvidenceBlock.Replace(t.Description, "").Trim();
                        var description = clean.Length == 0 ? evidence : clean + "\n\n" + evidence;
                        if (description.Length > 4000) { skipped.Add(new(t.Id, "description_too_long")); continue; }
                        t.Description = description;
                    }
                    Track(t.Id, "status_changed", t.Status, value);
                    SetStatus(t, value, now);
                    t.SortOrder = nextSort;
                    nextSort += 100;
                    t.UpdatedAt = now;
                    if (value == "done") await QueueNextRecurrenceAsync(t, actorId, ct);
                    updated++;
                }
                break;
            }
            case "assign" or "unassign":
            {
                if (!Guid.TryParse(value, out var userId)) return Fail("invalid_value");
                var userName = await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == userId
                    && (action == "unassign" || ((m.Role == "owner" || m.Role == "manager" || m.Role == "member") && m.User.IsActive)))
                    .Select(m => m.User.Name).FirstOrDefaultAsync(ct);
                if (userName is null) return Fail("invalid_value");
                var links = await db.ProjectTaskAssignees.Where(a => ids.Contains(a.TaskId) && a.UserId == userId).ToListAsync(ct);
                var linked = links.Select(a => a.TaskId).ToHashSet();
                foreach (var t in tasks)
                {
                    if (action == "assign" == linked.Contains(t.Id)) { skipped.Add(new(t.Id, "unchanged")); continue; }
                    if (action == "assign")
                    {
                        db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = t.Id, UserId = userId, AssignedAt = now });
                        Track(t.Id, "assignee_added", null, userName);
                    }
                    else
                    {
                        db.ProjectTaskAssignees.Remove(links.First(a => a.TaskId == t.Id));
                        Track(t.Id, "assignee_removed", userName, null);
                    }
                    t.UpdatedAt = now;
                    updated++;
                }
                break;
            }
            case "due":
            {
                DateTime? due = null;
                if (value.Length > 0)
                {
                    if (!DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var parsed)) return Fail("invalid_value");
                    due = AsUtcDate(parsed);
                }
                foreach (var t in tasks)
                {
                    if (t.DueDate == due) { skipped.Add(new(t.Id, "unchanged")); continue; }
                    Track(t.Id, "due_changed", t.DueDate?.ToString("yyyy-MM-dd"), due?.ToString("yyyy-MM-dd"));
                    t.DueDate = due;
                    if (due is { } d && t.StartDate > d) t.StartDate = d;
                    t.UpdatedAt = now;
                    updated++;
                }
                break;
            }
            case "priority":
            {
                var priority = value.ToLowerInvariant();
                if (priority.Length > 0 && !TaskPriorities.Contains(priority)) return Fail("invalid_value");
                foreach (var t in tasks)
                {
                    var next = priority.Length == 0 ? null : priority;
                    if (t.Priority == next) { skipped.Add(new(t.Id, "unchanged")); continue; }
                    Track(t.Id, "priority_changed", t.Priority, next);
                    t.Priority = next;
                    t.UpdatedAt = now;
                    updated++;
                }
                break;
            }
            case "group":
            {
                if (!Guid.TryParse(value, out var groupId)
                    || !await db.ProjectGroups.AnyAsync(g => g.Id == groupId && g.ProjectId == projectId, ct))
                    return Fail("invalid_value");
                var selected = ids.ToHashSet();
                var moved = new List<Guid>();
                foreach (var t in tasks)
                {
                    if (t.ParentTaskId is { } p)
                    {
                        // A subtask travels with its parent; alone it cannot leave the parent's group.
                        if (!selected.Contains(p)) skipped.Add(new(t.Id, "move_parent_instead"));
                        continue;
                    }
                    if (t.GroupId == groupId) { skipped.Add(new(t.Id, "unchanged")); continue; }
                    t.GroupId = groupId;
                    t.UpdatedAt = now;
                    moved.Add(t.Id);
                    updated++;
                }
                await db.SaveChangesAsync(ct);
                if (moved.Count > 0)
                    await db.ProjectTasks.IgnoreQueryFilters().Where(t => t.ParentTaskId != null && moved.Contains(t.ParentTaskId.Value))
                        .ExecuteUpdateAsync(set => set.SetProperty(t => t.GroupId, groupId), ct);
                break;
            }
            case "delete":
            {
                batchId = Guid.NewGuid();
                await MarkDeletedAsync(projectId, ids, batchId.Value, ct);
                updated = tasks.Count;
                break;
            }
            default:
                return Fail("invalid_action");
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await RecalculateProgressAsync(projectId, ct);
        return new(true, updated, skipped, batchId, null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Saved views
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProjectSavedViewDto>?> GetSavedViewsAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;
        return await db.ProjectSavedViews.AsNoTracking()
            .Where(v => v.ProjectId == projectId && (v.OwnerId == userId || v.IsShared))
            .OrderBy(v => v.OwnerId == userId ? 0 : 1).ThenBy(v => v.SortOrder).ThenBy(v => v.CreatedAt)
            .Select(v => new ProjectSavedViewDto(v.Id, v.ProjectId, v.OwnerId, v.Owner.Name, v.Name, v.IsShared,
                v.StateJson, v.SortOrder, v.OwnerId == userId))
            .ToListAsync(ct);
    }

    public async Task<SavedViewResultDto> CreateSavedViewAsync(Guid actorId, Guid projectId, SaveProjectViewRequest req, CancellationToken ct)
    {
        if (!await CanMutateTasksAsync(actorId, projectId, ct)) return new(false, null, "forbidden");
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 40) return new(false, null, "invalid_name");
        if (SanitizeViewState(req.StateJson) is not { } state) return new(false, null, "invalid_state");
        if (req.IsShared && !await CanManageGroupsAsync(actorId, projectId, ct)) return new(false, null, "forbidden");
        var mine = await db.ProjectSavedViews.Where(v => v.ProjectId == projectId && v.OwnerId == actorId).CountAsync(ct);
        if (mine >= MaxSavedViewsPerUser) return new(false, null, "too_many_views");
        var maxSort = await db.ProjectSavedViews.Where(v => v.ProjectId == projectId && v.OwnerId == actorId)
            .Select(v => (int?)v.SortOrder).MaxAsync(ct) ?? 0;
        var view = new ProjectSavedView
        {
            Id = Guid.NewGuid(), ProjectId = projectId, OwnerId = actorId, Name = name, IsShared = req.IsShared,
            StateJson = state, SortOrder = maxSort + 100, CreatedAt = DateTime.UtcNow
        };
        db.ProjectSavedViews.Add(view);
        await db.SaveChangesAsync(ct);
        return new(true, await MapSavedViewAsync(view.Id, actorId, ct), null);
    }

    public async Task<SavedViewResultDto> UpdateSavedViewAsync(Guid actorId, Guid viewId, SaveProjectViewRequest req, CancellationToken ct)
    {
        var view = await db.ProjectSavedViews.FirstOrDefaultAsync(v => v.Id == viewId, ct);
        if (view is null) return new(false, null, "view_not_found");
        if (!await CanMutateTasksAsync(actorId, view.ProjectId, ct)) return new(false, null, "forbidden");
        if (view.OwnerId != actorId) return new(false, null, "forbidden");
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 40) return new(false, null, "invalid_name");
        if (SanitizeViewState(req.StateJson) is not { } state) return new(false, null, "invalid_state");
        if (req.IsShared != view.IsShared && !await CanManageGroupsAsync(actorId, view.ProjectId, ct))
            return new(false, null, "forbidden");
        view.Name = name;
        view.StateJson = state;
        view.IsShared = req.IsShared;
        await db.SaveChangesAsync(ct);
        return new(true, await MapSavedViewAsync(view.Id, actorId, ct), null);
    }

    public async Task<MemberResultDto> DeleteSavedViewAsync(Guid actorId, Guid viewId, CancellationToken ct)
    {
        var view = await db.ProjectSavedViews.FirstOrDefaultAsync(v => v.Id == viewId, ct);
        if (view is null) return new(false, "view_not_found");
        if (!await CanMutateTasksAsync(actorId, view.ProjectId, ct)) return new(false, "forbidden");
        if (view.OwnerId != actorId && !(view.IsShared && await CanManageGroupsAsync(actorId, view.ProjectId, ct)))
            return new(false, "forbidden");
        db.ProjectSavedViews.Remove(view);
        await db.SaveChangesAsync(ct);
        return new(true, null);
    }

    public async Task<Guid?> SavedViewProjectIdAsync(Guid viewId, CancellationToken ct) =>
        await db.ProjectSavedViews.Where(v => v.Id == viewId).Select(v => (Guid?)v.ProjectId).FirstOrDefaultAsync(ct);

    private Task<ProjectSavedViewDto> MapSavedViewAsync(Guid viewId, Guid userId, CancellationToken ct) =>
        db.ProjectSavedViews.AsNoTracking().Where(v => v.Id == viewId)
            .Select(v => new ProjectSavedViewDto(v.Id, v.ProjectId, v.OwnerId, v.Owner.Name, v.Name, v.IsShared,
                v.StateJson, v.SortOrder, v.OwnerId == userId))
            .FirstAsync(ct);

    /// Keeps only the filter keys the Projects page understands, with sane types and lengths.
    /// Returns null when the input is not a JSON object.
    public static string? SanitizeViewState(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 2000) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var clean = new Dictionary<string, object?>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "tab" or "priority" when p.Value.ValueKind == JsonValueKind.String:
                        var s = p.Value.GetString()!.Trim();
                        if (s.Length is > 0 and <= 20 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) clean[p.Name] = s;
                        break;
                    case "search" when p.Value.ValueKind == JsonValueKind.String:
                        var q = p.Value.GetString()!.Trim();
                        if (q.Length > 0) clean[p.Name] = q.Length > 100 ? q[..100] : q;
                        break;
                    case "myTasks" or "overdue" when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                        clean[p.Name] = p.Value.GetBoolean();
                        break;
                    case "groupId" or "assigneeId" when p.Value.ValueKind == JsonValueKind.String && Guid.TryParse(p.Value.GetString(), out var g):
                        clean[p.Name] = g.ToString();
                        break;
                }
            }
            return JsonSerializer.Serialize(clean);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Calendar
    // ──────────────────────────────────────────────────────────────────────────

    public async Task<ProjectCalendarDto?> GetProjectCalendarAsync(Guid projectId, Guid userId, DateTime from, DateTime to, Guid? groupId, CancellationToken ct)
    {
        if (!await CanViewAsync(projectId, userId, ct)) return null;
        from = AsUtcDate(from.Date);
        to = AsUtcDate(to.Date);
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > MaxCalendarDays) to = from.AddDays(MaxCalendarDays);
        var end = to.AddDays(1);

        var rows = await db.ProjectTasks.AsNoTracking()
            .Where(t => t.ProjectId == projectId && (!groupId.HasValue || t.GroupId == groupId.Value)
                && (t.StartDate != null || t.DueDate != null)
                && (t.StartDate ?? t.DueDate) < end && (t.DueDate ?? t.StartDate) >= from)
            .OrderBy(t => t.StartDate ?? t.DueDate).ThenBy(t => t.SortOrder)
            .Take(MaxCalendarTasks + 1)
            .Select(t => new
            {
                t.Id, t.ParentTaskId, t.Title, t.Status, t.Priority, t.StartDate, t.DueDate, t.GroupId, t.IsRecurring,
                Assignees = t.Assignees.Select(a => a.UserId).ToList()
            })
            .ToListAsync(ct);
        var truncated = rows.Count > MaxCalendarTasks;
        if (truncated) rows.RemoveAt(rows.Count - 1);
        var unmet = await UnmetDependenciesAsync(rows.Select(r => r.Id).ToList(), ct);
        var tasks = rows.Select(r => new CalendarTaskDto(r.Id, r.ParentTaskId, r.Title, r.Status, r.Priority,
            r.StartDate, r.DueDate, r.GroupId, r.Assignees, r.Status != "done" && unmet.ContainsKey(r.Id), r.IsRecurring)).ToList();
        return new ProjectCalendarDto(projectId, from, to, tasks, truncated);
    }
}
