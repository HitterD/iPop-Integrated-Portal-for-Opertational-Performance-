using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static IPop.Infrastructure.Projects.ProjectAccess;

namespace IPop.Infrastructure.Projects;

/// Wrap-up of a project: a summary of how it went, close (Status "completed", ClosedAt, lessons) and reopen.
/// Completed and cancelled projects are what the lists and portfolio already treat as archived.
public sealed class ProjectCloseService(IPopDbContext db, IProjectTemplateService? templates = null) : IProjectCloseService
{
    public const int MaxLessons = 2000;

    public async Task<ProjectOpResult<CloseSummaryDto>> GetSummaryAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(db, projectId, ct)) return Fail<CloseSummaryDto>("project_not_found");
        if (!await CanViewAsync(db, projectId, userId, ct)) return Fail<CloseSummaryDto>("forbidden");
        return Ok(await BuildSummaryAsync(projectId, ct));
    }

    internal async Task<CloseSummaryDto> BuildSummaryAsync(Guid projectId, CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var tasks = await db.ProjectTasks.AsNoTracking().Where(t => t.ProjectId == projectId && t.ParentTaskId == null)
            .Select(t => new { t.Id, t.Title, t.Status, t.DueDate, t.CompletedAt }).ToListAsync(ct);
        var deadline = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.Deadline).FirstAsync(ct);

        var done = tasks.Where(t => t.Status == "done").ToList();
        var doneWithDue = done.Where(t => t.DueDate.HasValue).ToList();
        int? onTime = doneWithDue.Count == 0 ? null
            : (int)Math.Round(doneWithDue.Count(t => (t.CompletedAt ?? t.DueDate!.Value).Date <= t.DueDate!.Value.Date) * 100.0 / doneWithDue.Count);

        var delays = new List<(Guid Id, string Title, int Days)>();
        foreach (var t in doneWithDue)
        {
            var days = ((t.CompletedAt ?? t.DueDate!.Value).Date - t.DueDate!.Value.Date).Days;
            if (days > 0) delays.Add((t.Id, t.Title, days));
        }
        foreach (var t in tasks.Where(t => t.Status != "done" && t.DueDate.HasValue && t.DueDate.Value.Date < today))
            delays.Add((t.Id, t.Title, (today - t.DueDate!.Value.Date).Days));
        var longest = delays.OrderByDescending(d => d.Days).ThenBy(d => d.Title).Select(d => new LongestDelayDto(d.Id, d.Title, d.Days)).FirstOrDefault();

        int? vsPlan = null;
        var lastDone = done.Where(t => t.CompletedAt.HasValue).Select(t => t.CompletedAt!.Value).DefaultIfEmpty().Max();
        var plan = deadline ?? tasks.Where(t => t.DueDate.HasValue).Select(t => t.DueDate!.Value).DefaultIfEmpty().Max();
        if (lastDone != default && plan != default) vsPlan = (lastDone.Date - plan.Date).Days;

        var openIds = tasks.Where(t => t.Status != "done").Select(t => t.Id).ToList();
        var openWithComments = await db.ProjectTaskComments.AsNoTracking()
            .Where(c => !c.IsDeleted && c.Task.ProjectId == projectId && c.Task.Status != "done" && c.Task.DeletedAt == null)
            .Select(c => c.TaskId).Distinct().CountAsync(ct);
        var onNas = await db.ProjectTaskFiles.AsNoTracking().AnyAsync(f => f.DeletedAt == null && f.Task.ProjectId == projectId, ct)
            || await db.ProjectTaskAttachments.AsNoTracking().AnyAsync(a => !a.IsDeleted && a.Task.ProjectId == projectId, ct);

        var late = doneWithDue.Count(t => (t.CompletedAt ?? t.DueDate!.Value).Date > t.DueDate!.Value.Date)
            + tasks.Count(t => t.Status != "done" && t.DueDate.HasValue && t.DueDate.Value.Date < today);
        return new CloseSummaryDto(tasks.Count, done.Count, onTime, late, vsPlan, longest, openWithComments, openIds.Count, onNas);
    }

    public async Task<ProjectOpResult<CloseProjectResultDto>> CloseAsync(Guid projectId, Guid userId, CloseProjectRequest request, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct);
        if (project is null) return Fail<CloseProjectResultDto>("project_not_found");
        if (!await CanManageAsync(db, projectId, userId, ct)) return Fail<CloseProjectResultDto>("forbidden");
        var lessons = request?.Lessons?.Trim();
        if (lessons is { Length: > MaxLessons }) return Fail<CloseProjectResultDto>("invalid_lessons");
        var templateName = request?.TemplateName?.Trim();
        if (request?.SaveAsTemplate == true && (string.IsNullOrEmpty(templateName) ? project.Name : templateName).Length > 200)
            return Fail<CloseProjectResultDto>("invalid_name");

        var summary = await BuildSummaryAsync(projectId, ct);
        var now = DateTime.UtcNow;
        project.Status = "completed";
        project.ClosedAt = now;
        project.Lessons = string.IsNullOrEmpty(lessons) ? null : lessons;
        project.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        Guid? templateId = null;
        if (request?.SaveAsTemplate == true && templates is not null)
        {
            var description = string.IsNullOrEmpty(lessons) ? null : "Lessons learned: " + lessons;
            if (description is { Length: > ProjectTemplateService.MaxDescription }) description = description[..ProjectTemplateService.MaxDescription];
            var created = await templates.CreateAsync(userId, new CreateProjectTemplateRequest(
                string.IsNullOrEmpty(templateName) ? project.Name : templateName, description, projectId,
                IncludeSections: true, IncludeMilestones: true, IncludeAssignees: false, Visibility: "private"), ct);
            if (created.Success) templateId = created.Value!.Id;
        }

        var posted = false;
        if (request?.PostSummaryInChat == true)
        {
            var roomId = await db.ProjectRooms.AsNoTracking().Where(r => r.ProjectId == projectId).Select(r => (Guid?)r.RoomId).FirstOrDefaultAsync(ct);
            if (roomId is { } rid)
            {
                var text = $"Project closed: {project.Name}. {summary.Done} of {summary.Total} tasks done"
                    + (summary.OnTimePercent is { } p ? $", {p}% on time" : "") + (summary.OpenTasks > 0 ? $", {summary.OpenTasks} left open" : "") + ".";
                db.ChatMessages.Add(new ChatMessage
                {
                    Id = Guid.NewGuid(), RoomId = rid, SenderId = userId, Type = "system", Content = text,
                    IdempotencyKey = Guid.NewGuid(), CreatedAt = now
                });
                var room = await db.ChatRooms.FirstOrDefaultAsync(r => r.Id == rid, ct);
                if (room is not null) room.LastMessageAt = now;
                await db.SaveChangesAsync(ct);
                posted = true;
            }
        }
        return Ok(new CloseProjectResultDto(summary, summary.OpenTasks > 0, templateId, posted));
    }

    public async Task<ProjectOpResult<bool>> ReopenAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId && p.IsActive, ct);
        if (project is null) return Fail<bool>("project_not_found");
        if (!await CanManageAsync(db, projectId, userId, ct)) return Fail<bool>("forbidden");
        if (project.Status is not ("completed" or "cancelled")) return Ok(true);
        project.Status = "active";
        project.ClosedAt = null;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Ok(true);
    }
}
