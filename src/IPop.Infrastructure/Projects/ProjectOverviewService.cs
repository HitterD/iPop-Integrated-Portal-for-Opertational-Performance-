using System.Globalization;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static IPop.Infrastructure.Projects.ProjectAccess;

namespace IPop.Infrastructure.Projects;

/// Overview tab data and the hours-based weekly workload (person x 8 weeks).
/// Hours of a task = EstimatedHours spread evenly over its working days (Mon-Fri minus the project's holidays/off days)
/// between StartDate (or DueDate when only that is set) and DueDate, split equally between its assignees. Tasks without an
/// estimate count 0 hours but still count as tasks. Done and soft-deleted tasks are ignored.
public sealed class ProjectOverviewService(IPopDbContext db, INotificationGatewayService? gateway = null) : IProjectOverviewService
{
    public const int CapacityHours = 40;
    public const int WorkloadWeeks = 8;
    public const int MaxSuggestions = 3;
    private const int MaxBurnWeeks = 26;

    // ───────────────────────────── Overview ─────────────────────────────

    public async Task<ProjectOpResult<ProjectOverviewDto>> GetOverviewAsync(Guid projectId, Guid userId, DateTime localToday, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId && p.IsActive)
            .Select(p => new { p.Goal, p.StartDate, p.Deadline, p.CreatedAt, OwnerName = p.Owner.Name }).FirstOrDefaultAsync(ct);
        if (project is null) return Fail<ProjectOverviewDto>("project_not_found");
        if (!await CanViewAsync(db, projectId, userId, ct)) return Fail<ProjectOverviewDto>("forbidden");
        var today = DateTime.SpecifyKind(localToday.Date, DateTimeKind.Utc);

        await ProjectStatusService.EnsureStatusesAsync(db, projectId, ct);
        var options = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == projectId).OrderBy(o => o.SortOrder).ToListAsync(ct);
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId && t.ParentTaskId == null)
            .Select(t => new
            {
                t.Id, t.Title, t.Status, t.StatusOptionId, t.StartDate, t.DueDate, t.CompletedAt,
                Owner = t.Assignees.OrderBy(a => a.AssignedAt).Select(a => new { a.User.Name, a.User.Nik }).FirstOrDefault()
            }).ToListAsync(ct);
        var memberCount = await db.ProjectMembers.CountAsync(m => m.ProjectId == projectId, ct);
        var hasBaseline = await db.ProjectBaselines.AnyAsync(b => b.ProjectId == projectId, ct);
        var milestones = await db.ProjectMilestones.AsNoTracking().Where(m => m.ProjectId == projectId).OrderBy(m => m.DueDate)
            .Select(m => new { m.Id, m.Title, m.DueDate, m.AchievedAt, Linked = m.LinkedTasks.Select(l => l.TaskId).ToList(), m.TaskId }).ToListAsync(ct);

        var checklist = new List<OverviewChecklistItemDto>
        {
            new("goal", "Add the goal", !string.IsNullOrWhiteSpace(project.Goal)),
            new("people", "Invite key users", memberCount >= 2),
            new("dates", "Check Gantt dates", tasks.Any(t => t.DueDate.HasValue || t.StartDate.HasValue)),
            new("baseline", "Save a baseline", hasBaseline),
        };

        var counts = tasks.Where(t => t.StatusOptionId != null).GroupBy(t => t.StatusOptionId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var statusCounts = options.Select(o => new OverviewStatusCountDto(o.Label, o.Color, o.Stage, counts.GetValueOrDefault(o.Id))).ToList();
        var optionById = options.ToDictionary(o => o.Id);
        (string Label, string Color) Look(Guid? id, string stage) => id is { } i && optionById.TryGetValue(i, out var o) ? (o.Label, o.Color)
            : ProjectStatusDefaults.Options.First(d => d.Stage == stage) is var d ? (d.Label, d.Color) : ("", "");

        var finish = new[] { tasks.Where(t => t.DueDate.HasValue).Select(t => t.DueDate!.Value).DefaultIfEmpty().Max(),
            milestones.Select(m => m.DueDate).DefaultIfEmpty().Max() }.Max();

        // Burn-up by week (cumulative counts).
        var first = new[] { project.StartDate ?? project.CreatedAt, tasks.Where(t => t.DueDate.HasValue).Select(t => t.DueDate!.Value).DefaultIfEmpty(DateTime.MaxValue).Min() }.Min();
        var startWeek = WeekStart(first);
        var thisWeek = WeekStart(today);
        var lastWeek = WeekStart(finish == default ? today : (finish > today ? finish : today));
        if (lastWeek < thisWeek) lastWeek = thisWeek;
        var weekCount = Math.Clamp(((lastWeek - startWeek).Days / 7) + 1, 1, MaxBurnWeeks);
        if (weekCount == MaxBurnWeeks) startWeek = lastWeek.AddDays(-7 * (MaxBurnWeeks - 1));
        var labels = new List<string>(); var plan = new List<int>(); var done = new List<int?>();
        for (var i = 0; i < weekCount; i++)
        {
            var ws = startWeek.AddDays(7 * i);
            var we = ws.AddDays(7);
            labels.Add(ws.ToString("d MMM", CultureInfo.InvariantCulture));
            plan.Add(tasks.Count(t => t.DueDate.HasValue && t.DueDate.Value < we));
            done.Add(ws > thisWeek ? null : tasks.Count(t => t.Status == "done" && (t.CompletedAt ?? t.DueDate ?? DateTime.MinValue) < we));
        }

        var msDtos = new List<OverviewMilestoneDto>();
        foreach (var m in milestones)
        {
            var linked = m.Linked.Count > 0 ? m.Linked : (m.TaskId is { } tid ? [tid] : []);
            var linkedTasks = tasks.Where(t => linked.Contains(t.Id)).ToList();
            var open = linkedTasks.Count(t => t.Status != "done");
            var lateLinked = linkedTasks.Count(t => t.Status != "done" && t.DueDate.HasValue && t.DueDate.Value.Date > m.DueDate.Date);
            string state; string? note = null;
            if (m.AchievedAt is not null) state = "achieved";
            else if (m.DueDate.Date < today || (m.DueDate.Date <= today.AddDays(14) && open > 0) || lateLinked > 0)
            {
                state = "at_risk";
                note = m.DueDate.Date < today ? "Past its date" : lateLinked > 0 ? $"{lateLinked} linked task{(lateLinked == 1 ? "" : "s")} planned after it" : $"{open} linked task{(open == 1 ? "" : "s")} still open";
            }
            else state = "upcoming";
            msDtos.Add(new OverviewMilestoneDto(m.Id, m.Title, m.DueDate, state, note));
        }

        // Needs attention: late, stuck, overloaded people.
        var attention = new List<OverviewAttentionDto>();
        foreach (var t in tasks.Where(t => t.Status != "done" && t.DueDate.HasValue && t.DueDate.Value.Date < today).OrderBy(t => t.DueDate).Take(10))
        {
            var (l, c) = Look(t.StatusOptionId, t.Status);
            attention.Add(new(t.Id, t.Title, l, c, "late", t.Owner?.Name, t.Owner?.Nik));
        }
        foreach (var t in tasks.Where(t => t.Status != "done" && t.StatusOptionId is { } o && optionById.TryGetValue(o, out var op) && op.Label.Equals("Stuck", StringComparison.OrdinalIgnoreCase)
            && !attention.Any(a => a.TaskId == t.Id)).Take(10))
        {
            var (l, c) = Look(t.StatusOptionId, t.Status);
            attention.Add(new(t.Id, t.Title, l, c, "stuck", t.Owner?.Name, t.Owner?.Nik));
        }
        var load = await BuildWorkloadAsync(projectId, thisWeek, 1, ct);
        foreach (var p in load.People.Where(p => p.Weeks[0].Hours > CapacityHours))
        {
            var biggest = load.Entries.Where(e => e.UserId == p.UserId && e.WeekHours[0] > 0).OrderByDescending(e => e.WeekHours[0]).FirstOrDefault();
            if (biggest is null) continue;
            var t = tasks.FirstOrDefault(x => x.Id == biggest.TaskId);
            if (t is null) continue;
            var (l, c) = Look(t.StatusOptionId, t.Status);
            attention.Add(new(t.Id, t.Title, l, c, "overloaded", p.Name, p.Nik));
        }

        var activities = await db.ProjectTaskActivities.AsNoTracking().Where(a => a.Task.ProjectId == projectId)
            .OrderByDescending(a => a.CreatedAt).Take(8)
            .Select(a => new { a.Action, a.OldValue, a.NewValue, a.CreatedAt, a.Task.Title, Actor = a.Actor.Name, ActorNik = a.Actor.Nik }).ToListAsync(ct);
        var updates = activities.Select(a => new OverviewUpdateDto(a.Actor, a.ActorNik, Humanise(a.Action, a.Title, a.OldValue, a.NewValue), a.CreatedAt)).ToList();

        return Ok(new ProjectOverviewDto(project.Goal, project.StartDate, project.Deadline, finish == default ? null : finish, project.OwnerName,
            null, checklist, statusCounts, new OverviewBurnUpDto(labels, plan, done), msDtos, attention, updates));
    }

    private static string Humanise(string action, string title, string? oldValue, string? newValue)
    {
        if (action.StartsWith(ProjectTaskHooks.AutomationPrefix, StringComparison.Ordinal))
            return action[ProjectTaskHooks.AutomationPrefix.Length..] switch
            {
                "stuck_notified" => $"Automation told the lead that \"{title}\" is stuck",
                "due_reminded" => $"Automation reminded the team about \"{title}\"",
                "subtasks_done_review" => $"Automation sent \"{title}\" to review",
                "weekly_task" => $"Automation created \"{title}\"",
                var other => $"Automation ran on \"{title}\" ({other.Replace('_', ' ')})"
            };
        return action switch
        {
            "status_changed" => $"moved \"{title}\" to {newValue}",
            "file_added" => $"added a file to \"{title}\"",
            "file_removed" or "attachment_removed" => $"removed a file from \"{title}\"",
            "attachment_added" => $"attached a file to \"{title}\"",
            "comment_added" => $"commented on \"{title}\"",
            "comment_replied" => $"replied on \"{title}\"",
            "rescheduled" => $"rescheduled \"{title}\"",
            "due_changed" => $"changed the due date of \"{title}\" to {newValue ?? "none"}",
            "form_request" => $"sent a request: \"{title}\"",
            "assignee_added" => $"assigned {newValue} to \"{title}\"",
            "assignee_removed" => $"unassigned {oldValue} from \"{title}\"",
            "health_changed" => $"set project health to {newValue}",
            _ => $"{action.Replace('_', ' ')} on \"{title}\""
        };
    }

    // ───────────────────────────── Weekly workload ─────────────────────────────

    private sealed record Entry(Guid UserId, Guid TaskId, string Title, string Stage, DateTime? Start, DateTime? Due, decimal TotalHours, decimal[] WeekHours, bool[] OnWeek);
    private sealed record Load(IReadOnlyList<WorkloadPersonDto> People, IReadOnlyList<Entry> Entries, bool[] Off);

    private async Task<Load> BuildWorkloadAsync(Guid projectId, DateTime from, int weekCount, CancellationToken ct)
    {
        var holidays = (await db.WorkCalendarEntries.AsNoTracking().Where(e => e.ProjectId == projectId && (e.Kind == "holiday" || e.Kind == "offday"))
            .Select(e => e.Date).ToListAsync(ct)).Select(d => d.Date).ToHashSet();
        bool Working(DateTime d) => WorkingDays.IsWorkingDay(d, holidays.Contains);

        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId && m.Role != "viewer" && m.User.IsActive)
            .Select(m => new { m.UserId, m.User.Name, m.User.Nik, m.Role }).ToListAsync(ct);
        var rangeEnd = from.AddDays(7 * weekCount);
        var rows = await db.ProjectTaskAssignees.AsNoTracking()
            .Where(a => a.Task.ProjectId == projectId && a.Task.Status != "done" && a.Task.ParentTaskId == null
                && (a.Task.StartDate != null || a.Task.DueDate != null))
            .Select(a => new { a.UserId, a.TaskId, a.Task.Title, a.Task.Status, a.Task.StartDate, a.Task.DueDate, a.Task.EstimatedHours,
                Count = a.Task.Assignees.Count }).ToListAsync(ct);

        var off = new bool[weekCount];
        for (var w = 0; w < weekCount; w++) off[w] = !Enumerable.Range(0, 7).Any(i => Working(from.AddDays(7 * w + i)));

        var entries = new List<Entry>();
        foreach (var r in rows)
        {
            var start = (r.StartDate ?? r.DueDate)!.Value.Date;
            var due = (r.DueDate ?? r.StartDate)!.Value.Date;
            if (due < start) (start, due) = (due, start);
            if (due - start > TimeSpan.FromDays(730)) due = start.AddDays(730);
            var days = new List<DateTime>();
            for (var d = start; d <= due; d = d.AddDays(1)) if (Working(d)) days.Add(d);
            var share = (r.EstimatedHours ?? 0m) / r.Count;
            var perDay = days.Count == 0 ? 0m : share / days.Count;
            var hours = new decimal[weekCount]; var on = new bool[weekCount];
            for (var w = 0; w < weekCount; w++)
            {
                var ws = from.AddDays(7 * w); var we = ws.AddDays(7);
                on[w] = start < we && due >= ws;
                hours[w] = perDay * days.Count(d => d >= ws && d < we);
                // A task dated only on days off still weighs on the week it falls in.
                if (days.Count == 0 && due >= ws && due < we) hours[w] = share;
            }
            if (on.Any(x => x)) entries.Add(new Entry(r.UserId, r.TaskId, r.Title, r.Status, r.StartDate, r.DueDate, share, hours, on));
        }

        var people = members.Select(m =>
        {
            var mine = entries.Where(e => e.UserId == m.UserId).ToList();
            var cells = Enumerable.Range(0, weekCount).Select(w =>
            {
                var h = Math.Round(mine.Sum(e => e.WeekHours[w]), 1);
                return new WorkloadCellDto(h, mine.Count(e => e.OnWeek[w]), (int)Math.Round(h * 100 / CapacityHours), off[w]);
            }).ToList();
            return new WorkloadPersonDto(m.UserId, m.Name, m.Nik, m.Role, cells);
        }).OrderBy(p => p.Name).ToList();
        return new Load(people, entries, off);
    }

    public async Task<ProjectOpResult<WeeklyWorkloadDto>> GetWeeklyWorkloadAsync(Guid projectId, Guid userId, DateTime? from, DateTime localToday, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<WeeklyWorkloadDto>("project_not_found");
        if (!await CanViewAsync(db, projectId, userId, ct)) return Fail<WeeklyWorkloadDto>("forbidden");
        var start = WeekStart(from ?? localToday);
        var load = await BuildWorkloadAsync(projectId, start, WorkloadWeeks, ct);
        var weeks = Enumerable.Range(0, WorkloadWeeks).Select(i => new WorkloadWeekDto(start.AddDays(7 * i), start.AddDays(7 * i).ToString("d MMM", CultureInfo.InvariantCulture))).ToList();
        return Ok(new WeeklyWorkloadDto(weeks, load.People, CapacityHours));
    }

    public async Task<ProjectOpResult<WorkloadSuggestionsDto>> SuggestAsync(Guid projectId, Guid userId, WorkloadSuggestRequest request, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<WorkloadSuggestionsDto>("project_not_found");
        if (!await CanViewAsync(db, projectId, userId, ct)) return Fail<WorkloadSuggestionsDto>("forbidden");
        if (request is null) return Fail<WorkloadSuggestionsDto>("invalid_request");
        var week = WeekStart(request.WeekStart);
        var load = await BuildWorkloadAsync(projectId, week, 1, ct);
        var person = load.People.FirstOrDefault(p => p.UserId == request.UserId);
        if (person is null) return Fail<WorkloadSuggestionsDto>("invalid_user");

        var over = person.Weeks[0].Hours - CapacityHours;
        var free = load.People.Where(p => p.UserId != request.UserId).ToDictionary(p => p.UserId, p => (Name: p.Name, Free: CapacityHours - p.Weeks[0].Hours));
        var suggestions = new List<WorkloadSuggestionDto>();
        if (over > 0)
        {
            foreach (var e in load.Entries.Where(e => e.UserId == request.UserId && e.Stage == "todo" && e.WeekHours[0] > 0).OrderBy(e => e.WeekHours[0]).ThenBy(e => e.Title))
            {
                if (over <= 0 || suggestions.Count >= MaxSuggestions) break;
                var alreadyOn = load.Entries.Where(x => x.TaskId == e.TaskId).Select(x => x.UserId).ToHashSet();
                var target = free.Where(f => f.Value.Free >= e.WeekHours[0] && !alreadyOn.Contains(f.Key))
                    .OrderByDescending(f => f.Value.Free).ThenBy(f => f.Value.Name).Select(f => (Guid?)f.Key).FirstOrDefault();
                if (target is not { } to) continue;
                var info = free[to];
                suggestions.Add(new WorkloadSuggestionDto(e.TaskId, e.Title, Math.Round(e.WeekHours[0], 1), to, info.Name, Math.Round(info.Free, 1)));
                free[to] = (info.Name, info.Free - e.WeekHours[0]);
                over -= e.WeekHours[0];
            }
        }
        return Ok(new WorkloadSuggestionsDto(suggestions));
    }

    public async Task<ProjectOpResult<bool>> ReassignAsync(Guid projectId, Guid userId, WorkloadReassignRequest request, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<bool>("project_not_found");
        if (!await CanMutateAsync(db, projectId, userId, ct)) return Fail<bool>("forbidden");
        if (request is null) return Fail<bool>("invalid_request");
        var task = await db.ProjectTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TaskId && t.ProjectId == projectId, ct);
        if (task is null) return Fail<bool>("task_not_found");
        var link = await db.ProjectTaskAssignees.FirstOrDefaultAsync(a => a.TaskId == task.Id && a.UserId == request.FromUserId, ct);
        if (link is null) return Fail<bool>("invalid_from_user");
        if (request.ToUserId == request.FromUserId) return Fail<bool>("invalid_assignee");
        var to = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == projectId && m.UserId == request.ToUserId
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member") && m.User.IsActive).Select(m => m.User.Name).FirstOrDefaultAsync(ct);
        if (to is null) return Fail<bool>("invalid_assignee");
        var fromName = await db.Users.AsNoTracking().Where(u => u.Id == request.FromUserId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "";

        var now = DateTime.UtcNow;
        db.ProjectTaskAssignees.Remove(link);
        db.ProjectTaskActivities.Add(new ProjectTaskActivity { Id = Guid.NewGuid(), TaskId = task.Id, ActorId = userId, Action = "assignee_removed", OldValue = fromName, CreatedAt = now });
        if (!await db.ProjectTaskAssignees.AnyAsync(a => a.TaskId == task.Id && a.UserId == request.ToUserId, ct))
        {
            db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = request.ToUserId, AssignedAt = now });
            db.ProjectTaskActivities.Add(new ProjectTaskActivity { Id = Guid.NewGuid(), TaskId = task.Id, ActorId = userId, Action = "assignee_added", NewValue = to, CreatedAt = now });
        }
        await db.SaveChangesAsync(ct);

        if (gateway is not null)
        {
            var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "Project";
            var url = $"/projects/{projectId}?task={task.Id}";
            try
            {
                if (request.ToUserId != userId)
                    await gateway.PublishInternalAsync("projects", "project.task_assigned", $"Task Assignment: {task.Title}",
                        $"\"{task.Title}\" in {projectName} was moved to you.", new NotificationTarget(UserId: request.ToUserId), userId, null, "normal", url, ct);
                if (request.FromUserId != userId)
                    await gateway.PublishInternalAsync("projects", "project.task_unassigned", $"Task moved: {task.Title}",
                        $"\"{task.Title}\" in {projectName} was moved to {to}.", new NotificationTarget(UserId: request.FromUserId), userId, null, "normal", url, ct);
            }
            catch (Exception) { /* the change is saved; the notices are advisory */ }
        }
        return Ok(true);
    }
}
