using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Projects;

/// Time-driven automation rules, checked every minute against the server's local time:
/// weekly_recurring_task from 07:30 on the chosen weekday, due_tomorrow_remind from 08:00.
/// Both are safe to re-run: a task is created once per week (LastRunAt) and a reminder is sent once per task per day.
public sealed class ProjectAutomationWorker(IServiceScopeFactory scopeFactory, ILogger<ProjectAutomationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IPopDbContext>();
                await RunOnceAsync(db, scope.ServiceProvider.GetService<INotificationGatewayService>(), DateTime.Now, DateTime.UtcNow, log, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "ProjectAutomationWorker tick failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <param name="localNow">Server wall-clock time (decides which rules are due).</param>
    public static async Task RunOnceAsync(IPopDbContext db, INotificationGatewayService? gateway, DateTime localNow, DateTime utcNow, ILogger log, CancellationToken ct)
    {
        var time = localNow.TimeOfDay;
        if (time >= new TimeSpan(7, 30, 0)) await WeeklyTasksAsync(db, localNow, utcNow, log, ct);
        if (time >= new TimeSpan(8, 0, 0) && gateway is not null) await DueTomorrowAsync(db, gateway, localNow, utcNow, log, ct);
    }

    private static async Task WeeklyTasksAsync(IPopDbContext db, DateTime localNow, DateTime utcNow, ILogger log, CancellationToken ct)
    {
        var weekday = localNow.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)localNow.DayOfWeek;
        var weekStart = ProjectAccess.WeekStart(localNow);
        var rules = await db.ProjectAutomations.Where(a => a.IsEnabled && a.Kind == ProjectAutomationCatalogue.WeeklyRecurringTask
            && a.Project.IsActive && a.Project.Status != "completed" && a.Project.Status != "cancelled").ToListAsync(ct);
        foreach (var rule in rules)
        {
            try
            {
                if (rule.LastRunAt is { } last && ProjectAccess.WeekStart(last + (localNow - utcNow)) == weekStart) continue;
                using var doc = JsonDocument.Parse(rule.ConfigJson);
                var root = doc.RootElement;
                var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() ?? "" : "";
                var day = root.TryGetProperty("weekday", out var w) && w.TryGetInt32(out var wi) ? wi : 0;
                if (title.Length is 0 or > 200 || day != weekday) continue;

                Guid? assignee = root.TryGetProperty("assigneeId", out var a) && Guid.TryParse(a.GetString(), out var ag) ? ag : null;
                Guid? groupId = root.TryGetProperty("groupId", out var g) && Guid.TryParse(g.GetString(), out var gg) ? gg : null;
                var project = await db.Projects.AsNoTracking().Where(p => p.Id == rule.ProjectId).Select(p => new { p.OwnerId }).FirstAsync(ct);
                if (groupId is null || !await db.ProjectGroups.AnyAsync(x => x.Id == groupId && x.ProjectId == rule.ProjectId, ct))
                    groupId = await db.ProjectGroups.Where(x => x.ProjectId == rule.ProjectId).OrderBy(x => x.SortOrder).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
                if (groupId is null) continue;

                await ProjectStatusService.EnsureStatusesAsync(db, rule.ProjectId, ct);
                var option = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == rule.ProjectId && o.Stage == "todo")
                    .OrderBy(o => o.SortOrder).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
                var task = new ProjectTask
                {
                    Id = Guid.NewGuid(), ProjectId = rule.ProjectId, GroupId = groupId.Value, Title = title, Status = "todo", StatusOptionId = option,
                    DueDate = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Utc), CreatedById = project.OwnerId, CreatedAt = utcNow, UpdatedAt = utcNow,
                    SortOrder = ((await db.ProjectTasks.Where(x => x.ProjectId == rule.ProjectId && x.Status == "todo").Select(x => (int?)x.SortOrder).MaxAsync(ct)) ?? 0) + 100
                };
                db.ProjectTasks.Add(task);
                var who = assignee ?? project.OwnerId;
                if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == rule.ProjectId && m.UserId == who, ct))
                    db.ProjectTaskAssignees.Add(new ProjectTaskAssignee { TaskId = task.Id, UserId = who, AssignedAt = utcNow });
                db.ProjectTaskActivities.Add(new ProjectTaskActivity
                    { Id = Guid.NewGuid(), TaskId = task.Id, ActorId = project.OwnerId, Action = ProjectTaskHooks.AutomationPrefix + "weekly_task", NewValue = title, CreatedAt = utcNow });
                rule.RunCount++;
                rule.LastRunAt = utcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) { log.LogWarning(ex, "Weekly task rule {Rule} failed", rule.Id); }
        }
    }

    private static async Task DueTomorrowAsync(IPopDbContext db, INotificationGatewayService gateway, DateTime localNow, DateTime utcNow, ILogger log, CancellationToken ct)
    {
        var tomorrow = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Utc);
        var dayStartUtc = utcNow.AddHours(-24);
        var rules = await db.ProjectAutomations.Where(a => a.IsEnabled && a.Kind == ProjectAutomationCatalogue.DueTomorrowRemind
            && a.Project.IsActive && a.Project.Status != "completed" && a.Project.Status != "cancelled").ToListAsync(ct);
        foreach (var rule in rules)
        {
            try
            {
                var tasks = await db.ProjectTasks.AsNoTracking()
                    .Where(t => t.ProjectId == rule.ProjectId && t.Status != "done" && t.DueDate == tomorrow && t.Assignees.Any())
                    .Select(t => new { t.Id, t.Title, Assignees = t.Assignees.Select(a => a.UserId).ToList() }).ToListAsync(ct);
                if (tasks.Count == 0) continue;
                var projectInfo = await db.Projects.AsNoTracking().Where(p => p.Id == rule.ProjectId).Select(p => new { p.Name, p.OwnerId }).FirstAsync(ct);
                var sent = 0;
                foreach (var t in tasks)
                {
                    if (await db.ProjectTaskActivities.AnyAsync(x => x.TaskId == t.Id && x.Action == ProjectTaskHooks.AutomationPrefix + "due_reminded" && x.CreatedAt > dayStartUtc, ct)) continue;
                    db.ProjectTaskActivities.Add(new ProjectTaskActivity
                        { Id = Guid.NewGuid(), TaskId = t.Id, ActorId = projectInfo.OwnerId, Action = ProjectTaskHooks.AutomationPrefix + "due_reminded", NewValue = tomorrow.ToString("yyyy-MM-dd"), CreatedAt = utcNow });
                    await db.SaveChangesAsync(ct);
                    foreach (var uid in t.Assignees)
                        await gateway.PublishInternalAsync("projects", "project.task_due_tomorrow", $"Due tomorrow: {t.Title}",
                            $"\"{t.Title}\" in {projectInfo.Name} is due tomorrow.", new NotificationTarget(UserId: uid), "normal",
                            $"/projects/{rule.ProjectId}?task={t.Id}", ct);
                    sent++;
                }
                if (sent > 0) { rule.RunCount += sent; rule.LastRunAt = utcNow; await db.SaveChangesAsync(ct); }
            }
            catch (Exception ex) { log.LogWarning(ex, "Due tomorrow rule {Rule} failed", rule.Id); }
        }
    }
}
