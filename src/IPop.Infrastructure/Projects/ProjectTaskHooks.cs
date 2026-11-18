using System.Text.Json;
using IPop.Application.Notifications;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Side effects of a task changing status, run after the change is saved: progress notices for
/// request-form submitters and the status-driven automation rules. Never throws (the change is committed).
/// Automation actions are logged on the task as ProjectTaskActivity with Action "automation:*" and
/// ActorId = the project owner (there is no bot user).
internal static class ProjectTaskHooks
{
    public const string AutomationPrefix = "automation:";

    public static async Task AfterStatusChangeAsync(IPopDbContext db, INotificationGatewayService? gateway, ProjectTask task,
        string oldStage, Guid? oldOptionId, Guid actorId, CancellationToken ct)
    {
        var stageChanged = oldStage != task.Status;
        var optionChanged = oldOptionId != task.StatusOptionId;
        if (!stageChanged && !optionChanged) return;
        try
        {
            if (stageChanged) await NotifySubmitterAsync(db, gateway, task, oldStage, actorId, ct);
            var rules = await db.ProjectAutomations.Where(a => a.ProjectId == task.ProjectId && a.IsEnabled).ToListAsync(ct);
            if (rules.Count == 0) return;
            if (optionChanged && rules.FirstOrDefault(r => r.Kind == ProjectAutomationCatalogue.StuckNotifyLead) is { } stuck)
                await StuckAsync(db, gateway, task, stuck, actorId, ct);
            if (stageChanged && task.Status == "done" && task.ParentTaskId is not null
                && rules.FirstOrDefault(r => r.Kind == ProjectAutomationCatalogue.SubtasksDoneReview) is { } review)
                await SubtasksDoneAsync(db, task, review, ct);
        }
        catch (Exception) { /* advisory */ }
    }

    private static async Task NotifySubmitterAsync(IPopDbContext db, INotificationGatewayService? gateway, ProjectTask task,
        string oldStage, Guid actorId, CancellationToken ct)
    {
        if (gateway is null) return;
        string? what = task.Status switch
        {
            "doing" when oldStage == "todo" => "has been started",
            "review" => "is ready to be checked",
            "done" => "is done",
            _ => null
        };
        if (what is null) return;
        var submitter = await db.ProjectFormSubmissions.AsNoTracking().Where(s => s.TaskId == task.Id)
            .Select(s => (Guid?)s.SubmitterId).FirstOrDefaultAsync(ct);
        if (submitter is null || submitter == actorId) return;
        var projectName = await db.Projects.AsNoTracking().Where(p => p.Id == task.ProjectId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "Project";
        await gateway.PublishInternalAsync("projects", "project.form_request_update",
            $"Your request {what}", $"\"{task.Title}\" in {projectName} {what}.",
            new NotificationTarget(UserId: submitter), "normal", $"/projects/{task.ProjectId}?task={task.Id}", ct);
    }

    private static async Task StuckAsync(IPopDbContext db, INotificationGatewayService? gateway, ProjectTask task,
        ProjectAutomation rule, Guid actorId, CancellationToken ct)
    {
        var label = ConfigString(rule.ConfigJson, "statusLabel") ?? "Stuck";
        var optionLabel = task.StatusOptionId is { } oid
            ? await db.ProjectStatusOptions.AsNoTracking().Where(o => o.Id == oid).Select(o => o.Label).FirstOrDefaultAsync(ct) : null;
        if (optionLabel is null || !string.Equals(optionLabel, label, StringComparison.OrdinalIgnoreCase)) return;
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == task.ProjectId).Select(p => new { p.OwnerId, p.Name }).FirstOrDefaultAsync(ct);
        if (project is null || project.OwnerId == actorId) return;
        var since = DateTime.UtcNow.AddHours(-24);
        if (await db.ProjectTaskActivities.AnyAsync(a => a.TaskId == task.Id && a.Action == AutomationPrefix + "stuck_notified" && a.CreatedAt > since, ct)) return;
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ActorId = project.OwnerId, Action = AutomationPrefix + "stuck_notified",
            NewValue = optionLabel, CreatedAt = DateTime.UtcNow
        });
        rule.RunCount++;
        rule.LastRunAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (gateway is not null)
            await gateway.PublishInternalAsync("projects", "project.task_stuck", $"Stuck: {task.Title}",
                $"\"{task.Title}\" in {project.Name} is marked {optionLabel}.",
                new NotificationTarget(UserId: project.OwnerId), "high", $"/projects/{task.ProjectId}?task={task.Id}", ct);
    }

    private static async Task SubtasksDoneAsync(IPopDbContext db, ProjectTask subtask, ProjectAutomation rule, CancellationToken ct)
    {
        var parent = await db.ProjectTasks.FirstOrDefaultAsync(t => t.Id == subtask.ParentTaskId, ct);
        if (parent is null || parent.Status is "review" or "done") return;
        if (await db.ProjectTasks.AnyAsync(t => t.ParentTaskId == parent.Id && t.Status != "done", ct)) return;
        var option = await db.ProjectStatusOptions.AsNoTracking().Where(o => o.ProjectId == parent.ProjectId && o.Stage == "review")
            .OrderBy(o => o.SortOrder).ThenBy(o => o.CreatedAt).Select(o => new { o.Id }).FirstOrDefaultAsync(ct);
        if (option is null) return;
        var now = DateTime.UtcNow;
        var ownerId = await db.Projects.AsNoTracking().Where(p => p.Id == parent.ProjectId).Select(p => p.OwnerId).FirstAsync(ct);
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = parent.Id, ActorId = ownerId, Action = AutomationPrefix + "subtasks_done_review",
            OldValue = parent.Status, NewValue = "review", CreatedAt = now
        });
        ProjectService.SetStatus(parent, "review", now);
        parent.StatusOptionId = option.Id;
        parent.UpdatedAt = now;
        rule.RunCount++;
        rule.LastRunAt = now;
        await db.SaveChangesAsync(ct);
    }

    internal static string? ConfigString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
        }
        catch (JsonException) { return null; }
    }
}
