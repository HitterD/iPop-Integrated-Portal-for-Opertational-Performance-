using IPop.Application.Projects;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// <summary>Read-only, scope-checked portfolio queries; no project/task entities are loaded.</summary>
public sealed class PortfolioService(IPopDbContext db) : IPortfolioService
{
    private static DateTime Day(DateTime? today) => DateTime.SpecifyKind((today ?? DateTime.UtcNow).Date, DateTimeKind.Utc);

    private IQueryable<IPop.Core.Entities.Project> VisibleProjects(Guid userId, bool all) =>
        db.Projects.AsNoTracking().Where(p => p.IsActive &&
            (p.Status == "active" || p.Status == "planning" || p.Status == "on_hold") &&
            (all || p.Members.Any(m => m.UserId == userId)));

    private async Task<(bool All, bool SuperAdmin)> RolesAsync(Guid userId, CancellationToken ct)
    {
        var roles = await db.UserRoles.AsNoTracking().Where(r => r.UserId == userId)
            .Select(r => r.Role.Name).ToListAsync(ct);
        return (roles.Any(r => r is "Admin" or "HR" or "Super Admin" or "SuperAdmin"),
            roles.Any(r => r is "Super Admin" or "SuperAdmin"));
    }

    public async Task<PortfolioDto> GetPortfolioAsync(Guid userId, CancellationToken ct, DateTime? today = null,
        int page = 1, int pageSize = 20)
    {
        var day = Day(today);
        var nextWeek = day.AddDays(7);
        var (all, superAdmin) = await RolesAsync(userId, ct);
        var visible = VisibleProjects(userId, all);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var totalProjects = await visible.CountAsync(ct);
        // The dashboard and cards use SQL aggregates; only the requested card page is materialized.
        var atRiskProjects = await visible.CountAsync(p => p.Status == "on_hold" ||
            p.Deadline < day || p.Tasks.Any(t => t.ParentTaskId == null && t.Status != "done" && t.DueDate < day), ct);
        var averageProgress = totalProjects == 0 ? 0 : (int)Math.Round(await visible.AverageAsync(p => (double)p.Progress, ct));
        var projects = await visible.OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new {
                p.Id, p.Name, p.Status, p.Progress, p.Deadline,
                TaskCount = p.Tasks.Count(t => t.ParentTaskId == null),
                DoneCount = p.Tasks.Count(t => t.ParentTaskId == null && t.Status == "done"),
                OverdueCount = p.Tasks.Count(t => t.ParentTaskId == null && t.Status != "done" && t.DueDate < day)
            }).ToListAsync(ct);
        var rows = projects.Select(p => new PortfolioProjectDto(p.Id, p.Name, p.Status, p.Progress, p.Deadline,
            p.TaskCount, p.DoneCount, p.OverdueCount, Health(p.Status, p.Deadline, p.OverdueCount, day))).ToList();
        var tasks = db.ProjectTasks.AsNoTracking().Where(t => t.ParentTaskId == null && visible.Any(p => p.Id == t.ProjectId));

        // Count distinct tasks assigned to the caller, including assignments on multiple projects.
        var myDueWeek = await tasks.CountAsync(t => t.Status != "done" &&
            t.DueDate >= day && t.DueDate < nextWeek && t.Assignees.Any(a => a.UserId == userId), ct);
        var overdueTasks = await tasks.CountAsync(t => t.Status != "done" && t.DueDate < day, ct);

        var personCounts = await db.ProjectTaskAssignees.AsNoTracking()
            .Where(a => visible.Any(p => p.Id == a.Task.ProjectId) && a.Task.ParentTaskId == null && a.Task.Status != "done" && a.User.IsActive)
            .GroupBy(a => new { a.UserId, a.User.Name })
            .Select(g => new {
                g.Key.UserId, g.Key.Name,
                Overdue = g.Count(a => a.Task.DueDate < day),
                Open = g.Count(a => a.Task.DueDate >= day && a.Task.DueDate < nextWeek),
                Hours = g.Where(a => a.Task.DueDate >= day && a.Task.DueDate < nextWeek)
                    .Sum(a => a.Task.EstimatedHours ?? 0m)
            }).ToListAsync(ct);
        // Eight open tasks is the per-person weekly workload capacity. Hours remain informative.
        var people = personCounts.Select(p => new PortfolioPersonDto(p.UserId, p.Name, p.Overdue,
            p.Open, p.Hours, 8, p.Open > 8)).ToList();
        var overduePeople = people.Where(p => p.OverdueCount > 0)
            .OrderByDescending(p => p.OverdueCount).ThenBy(p => p.Name).Take(20).ToList();
        var workload = people.Where(p => p.OpenTaskCount > 0)
            .OrderByDescending(p => p.OpenTaskCount).ThenBy(p => p.Name).Take(20).ToList();
        var nearest = await visible.Where(p => p.Deadline.HasValue)
            .OrderBy(p => p.Deadline).ThenBy(p => p.Id).Take(20)
            .Select(p => new { p.Id, p.Name, p.Deadline, p.Progress, p.Status,
                Overdue = p.Tasks.Count(t => t.ParentTaskId == null && t.Status != "done" && t.DueDate < day) })
            .ToListAsync(ct);
        var deadlines = nearest.Select(p => new PortfolioDeadlineDto(p.Id, p.Name, p.Deadline!.Value,
            p.Progress, Health(p.Status, p.Deadline, p.Overdue, day))).ToList();
        return new PortfolioDto(all ? "all" : "member", superAdmin, totalProjects,
            atRiskProjects, averageProgress, myDueWeek, overdueTasks,
            new PagedResult<PortfolioProjectDto>(rows, totalProjects, page, pageSize), overduePeople, workload, deadlines);
    }

    private static string Health(string status, DateTime? deadline, int overdue, DateTime day)
    {
        if (overdue > 0 || deadline < day || status == "on_hold") return "at_risk";
        if (deadline < day.AddDays(7)) return "due_soon";
        return "on_track";
    }

    public async Task<PagedResult<PortfolioTaskDto>> GetTasksAsync(Guid userId, string filter,
        Guid? projectId, Guid? assigneeId, int page, int pageSize, CancellationToken ct, DateTime? today = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var day = Day(today);
        var (all, _) = await RolesAsync(userId, ct);
        var visible = VisibleProjects(userId, all);
        var q = db.ProjectTasks.AsNoTracking().Where(t => t.ParentTaskId == null && visible.Any(p => p.Id == t.ProjectId) && t.Status != "done");
        if (projectId.HasValue) q = q.Where(t => t.ProjectId == projectId.Value);
        if (assigneeId.HasValue) q = q.Where(t => t.Assignees.Any(a => a.UserId == assigneeId.Value));
        q = filter switch {
            "overdue" => q.Where(t => t.DueDate < day),
            "due_week" => q.Where(t => t.DueDate >= day && t.DueDate < day.AddDays(7) && t.Assignees.Any(a => a.UserId == userId)),
            "workload" => q.Where(t => t.DueDate >= day && t.DueDate < day.AddDays(7)),
            _ => q.Where(t => false)
        };
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(t => t.DueDate).ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(t => new PortfolioTaskDto(t.Id, t.ProjectId, t.Project.Name, t.Title,
                t.Status, t.DueDate, t.EstimatedHours,
                t.Assignees.Select(a => new PortfolioAssigneeDto(a.UserId, a.User.Name)).ToList()))
            .ToListAsync(ct);
        return new PagedResult<PortfolioTaskDto>(items, total, page, pageSize);
    }
}
