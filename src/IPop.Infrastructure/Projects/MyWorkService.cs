using IPop.Application.Projects;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// "My work": the caller's open tasks across every active project they belong to, bucketed by the
/// caller's local week (Mon-Sun), plus what is waiting on them.
public sealed class MyWorkService(IPopDbContext db) : IMyWorkService
{
    public const int MaxItems = 300;
    public const int RecentDays = 14;

    public async Task<MyWorkDto> GetAsync(Guid userId, DateTime localToday, CancellationToken ct)
    {
        var today = DateTime.SpecifyKind(localToday.Date, DateTimeKind.Utc);
        var weekStart = ProjectAccess.WeekStart(today);
        var weekEnd = weekStart.AddDays(6); // Sunday
        var recent = DateTime.UtcNow.AddDays(-RecentDays);

        var rows = await db.ProjectTaskAssignees.AsNoTracking()
            .Where(a => a.UserId == userId && a.Task.Status != "done"
                && a.Task.Project.IsActive && a.Task.Project.Status != "completed" && a.Task.Project.Status != "cancelled"
                && db.ProjectMembers.Any(m => m.ProjectId == a.Task.ProjectId && m.UserId == userId))
            .Select(a => new
            {
                a.Task.Id, a.Task.ProjectId, ProjectName = a.Task.Project.Name, ProjectColor = a.Task.Project.Color,
                a.Task.Title, a.Task.Status, a.Task.StatusOptionId, a.Task.DueDate, GroupName = a.Task.Group.Name
            }).ToListAsync(ct);

        var doneThisWeek = await db.ProjectTaskAssignees.AsNoTracking()
            .CountAsync(a => a.UserId == userId && a.Task.Status == "done" && a.Task.CompletedAt >= weekStart
                && a.Task.CompletedAt < weekStart.AddDays(7)
                && a.Task.Project.IsActive && a.Task.Project.Status != "completed" && a.Task.Project.Status != "cancelled", ct);

        var optionIds = rows.Where(r => r.StatusOptionId != null).Select(r => r.StatusOptionId!.Value).Distinct().ToList();
        var options = await db.ProjectStatusOptions.AsNoTracking().Where(o => optionIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => (o.Label, o.Color), ct);

        static int Bucket(DateTime? due, DateTime today, DateTime weekEnd) =>
            due is null ? 3 : due.Value.Date < today ? 0 : due.Value.Date == today ? 1 : due.Value.Date <= weekEnd ? 2 : 3;

        var items = rows.Select(r =>
        {
            var def = ProjectStatusDefaults.Options.First(o => o.Stage == r.Status);
            var (label, color) = r.StatusOptionId is { } oid && options.TryGetValue(oid, out var o) ? o : (def.Label, def.Color);
            return (Bucket: Bucket(r.DueDate, today, weekEnd), Item: new MyWorkItemDto(r.Id, r.ProjectId, r.ProjectName, r.ProjectColor,
                r.Title, label, color, r.Status, r.DueDate, r.DueDate is { } d && d.Date < today, r.GroupName));
        }).ToList();

        var counts = new MyWorkCountsDto(items.Count(i => i.Bucket == 0), items.Count(i => i.Bucket == 1),
            items.Count(i => i.Bucket == 2), items.Count(i => i.Bucket == 3), doneThisWeek);
        var sorted = items.OrderBy(i => i.Bucket).ThenBy(i => i.Item.DueDate ?? DateTime.MaxValue).ThenBy(i => i.Item.Title)
            .Take(MaxItems).Select(i => i.Item).ToList();

        return new MyWorkDto(counts, sorted, await WaitingAsync(userId, rows.Select(r => r.Id).ToList(), recent, ct));
    }

    private async Task<IReadOnlyList<MyWorkWaitingDto>> WaitingAsync(Guid userId, List<Guid> myOpenTaskIds, DateTime recent, CancellationToken ct)
    {
        var waiting = new List<MyWorkWaitingDto>();

        // Blocked: someone else's open task waits for one of my unfinished tasks.
        var blocked = await db.ProjectTaskDependencies.AsNoTracking()
            .Where(d => myOpenTaskIds.Contains(d.DependsOnTaskId) && d.Task.Status != "done"
                && !d.Task.Assignees.Any(a => a.UserId == userId))
            .Select(d => new
            {
                Mine = d.DependsOnTaskId, MineTitle = d.DependsOn.Title, d.DependsOn.ProjectId, ProjectName = d.DependsOn.Project.Name,
                d.Task.UpdatedAt,
                Who = d.Task.Assignees.OrderBy(a => a.AssignedAt).Select(a => new { a.User.Name, a.User.Nik }).FirstOrDefault()
            }).ToListAsync(ct);
        foreach (var g in blocked.GroupBy(b => b.Mine))
        {
            var first = g.First();
            var n = g.Count();
            var who = g.Select(x => x.Who).FirstOrDefault(w => w is not null);
            waiting.Add(new MyWorkWaitingDto("blocked", $"{n} task{(n == 1 ? "" : "s")} wait{(n == 1 ? "s" : "")} for {first.MineTitle}",
                g.Key, first.ProjectId, first.ProjectName, who?.Name ?? "", who?.Nik ?? "", g.Max(x => x.UpdatedAt)));
        }

        // Request: new intake tasks made through a request form and assigned to me.
        var requests = await db.ProjectFormSubmissions.AsNoTracking()
            .Where(s => s.CreatedAt >= recent && s.Task.Status != "done" && s.Task.Assignees.Any(a => a.UserId == userId)
                && s.Task.Project.IsActive && s.Task.Project.Status != "completed" && s.Task.Project.Status != "cancelled")
            .Select(s => new
            {
                s.TaskId, s.ProjectId, ProjectName = s.Task.Project.Name, s.Task.Title, s.CreatedAt,
                Name = db.Users.Where(u => u.Id == s.SubmitterId).Select(u => u.Name).FirstOrDefault(),
                Nik = db.Users.Where(u => u.Id == s.SubmitterId).Select(u => u.Nik).FirstOrDefault()
            }).ToListAsync(ct);
        waiting.AddRange(requests.Select(r => new MyWorkWaitingDto("request", $"New request: {r.Title}", r.TaskId, r.ProjectId,
            r.ProjectName, r.Name ?? "", r.Nik ?? "", r.CreatedAt)));

        return waiting.OrderByDescending(w => w.At).Take(50).ToList();
    }
}
