using IPop.Application.Attachments;
using IPop.Application.Projects;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Projects;

/// Task files (field photos, completion deliverables, plain attachments) kept on the NAS by the
/// attachment store, so every member of the project reads the same bytes instead of the
/// uploader's browser cache. Membership is the project member list, as for task discussion.
public sealed class TaskFileService(IPopDbContext db, IAttachmentStore store) : ITaskFileService
{
    public const int MaxRecent = 50;

    public async Task<TaskFileResult<TaskFileDto>> UploadAsync(Guid actorId, Guid taskId, string kind, string fileName,
        string contentType, Stream content, CancellationToken ct)
    {
        var normalizedKind = (kind ?? "").Trim().ToLowerInvariant();
        if (!ProjectTaskFile.Kinds.Contains(normalizedKind)) return Fail<TaskFileDto>("invalid_kind");
        var projectId = await TaskProjectIdAsync(taskId, ct);
        if (projectId is null) return Fail<TaskFileDto>("task_not_found");
        if (!await IsWriterAsync(projectId.Value, actorId, ct)) return Fail<TaskFileDto>("forbidden");

        var stored = await store.PutAsync(AttachmentAreas.Projects, fileName,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, content, actorId, ct);

        var now = DateTime.UtcNow;
        var link = new ProjectTaskFile
        {
            Id = Guid.NewGuid(), TaskId = taskId, AttachmentId = stored.Id, Kind = normalizedKind,
            UploadedById = actorId, CreatedAt = now
        };
        db.ProjectTaskFiles.Add(link);
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId,
            Action = "file_added", NewValue = Truncate(stored.FileName), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);

        var name = await db.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.Name).FirstOrDefaultAsync(ct);
        return new(ToDto(link.Id, stored.Id, normalizedKind, stored.FileName, stored.ContentType, stored.Size,
            name ?? "Unknown", actorId, now), null);
    }

    public async Task<TaskFileResult<IReadOnlyList<TaskFileDto>>> ListAsync(Guid actorId, Guid taskId, CancellationToken ct)
    {
        var projectId = await TaskProjectIdAsync(taskId, ct);
        if (projectId is null) return Fail<IReadOnlyList<TaskFileDto>>("task_not_found");
        if (!await IsMemberAsync(projectId.Value, actorId, ct)) return Fail<IReadOnlyList<TaskFileDto>>("forbidden");

        var rows = await db.ProjectTaskFiles.AsNoTracking()
            .Where(f => f.TaskId == taskId && f.DeletedAt == null && f.Attachment.DeletedAt == null)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new
            {
                f.Id, f.AttachmentId, f.Kind, f.Attachment.FileName, f.Attachment.ContentType, f.Attachment.Size,
                UploaderName = f.UploadedBy.Name, f.UploadedById, f.CreatedAt
            })
            .ToListAsync(ct);
        return new(rows.Select(r => ToDto(r.Id, r.AttachmentId, r.Kind, r.FileName, r.ContentType, r.Size,
            r.UploaderName, r.UploadedById, r.CreatedAt)).ToList(), null);
    }

    public async Task<TaskFileResult<bool>> DeleteAsync(Guid actorId, Guid taskId, Guid fileId, CancellationToken ct)
    {
        var link = await db.ProjectTaskFiles.Include(f => f.Attachment)
            .FirstOrDefaultAsync(f => f.Id == fileId && f.TaskId == taskId && f.DeletedAt == null, ct);
        if (link is null) return Fail<bool>("file_not_found");
        var projectId = await TaskProjectIdAsync(taskId, ct);
        if (projectId is null) return Fail<bool>("task_not_found");
        if (!await IsWriterAsync(projectId.Value, actorId, ct)) return Fail<bool>("forbidden");
        if (link.UploadedById != actorId
            && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == actorId && m.Role == "owner", ct))
            return Fail<bool>("forbidden");

        var now = DateTime.UtcNow;
        link.DeletedAt = now;
        db.ProjectTaskActivities.Add(new ProjectTaskActivity
        {
            Id = Guid.NewGuid(), TaskId = taskId, ActorId = actorId,
            Action = "file_removed", OldValue = Truncate(link.Attachment.FileName), CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return new(true, null);
    }

    public async Task<TaskFileResult<IReadOnlyList<ProjectRecentFileDto>>> RecentAsync(Guid actorId, Guid projectId, int take, CancellationToken ct)
    {
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct))
            return Fail<IReadOnlyList<ProjectRecentFileDto>>("project_not_found");
        if (!await IsMemberAsync(projectId, actorId, ct)) return Fail<IReadOnlyList<ProjectRecentFileDto>>("forbidden");

        var n = Math.Clamp(take, 1, MaxRecent);
        var rows = await db.ProjectTaskFiles.AsNoTracking()
            .Where(f => f.Task.ProjectId == projectId && f.DeletedAt == null && f.Attachment.DeletedAt == null
                && (f.Kind == ProjectTaskFile.KindField || f.Kind == ProjectTaskFile.KindDeliverable))
            .OrderByDescending(f => f.CreatedAt)
            .Take(n)
            .Select(f => new
            {
                f.Id, f.AttachmentId, f.Kind, f.Attachment.FileName, f.Attachment.ContentType, f.Attachment.Size,
                UploaderName = f.UploadedBy.Name, f.UploadedById, f.CreatedAt, f.TaskId, TaskTitle = f.Task.Title
            })
            .ToListAsync(ct);
        return new(rows.Select(r =>
        {
            var d = ToDto(r.Id, r.AttachmentId, r.Kind, r.FileName, r.ContentType, r.Size, r.UploaderName, r.UploadedById, r.CreatedAt);
            return new ProjectRecentFileDto(d.Id, d.AttachmentId, d.Kind, d.FileName, d.ContentType, d.Size,
                d.UploadedByName, d.UploadedById, d.CreatedAt, d.Url, d.ThumbUrl, r.TaskId, r.TaskTitle);
        }).ToList(), null);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static TaskFileDto ToDto(Guid id, Guid attachmentId, string kind, string fileName, string contentType, long size,
        string uploaderName, Guid uploaderId, DateTime createdAt)
    {
        var url = $"/api/attachments/{attachmentId}";
        var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        return new TaskFileDto(id, attachmentId, kind, fileName, contentType, size, uploaderName, uploaderId,
            DateTime.SpecifyKind(createdAt, DateTimeKind.Utc), url, isImage ? url : null);
    }

    private Task<Guid?> TaskProjectIdAsync(Guid taskId, CancellationToken ct) =>
        db.ProjectTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.ProjectId).FirstOrDefaultAsync(ct);

    private Task<bool> IsMemberAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);

    private Task<bool> IsWriterAsync(Guid projectId, Guid userId, CancellationToken ct) =>
        db.ProjectMembers.AsNoTracking().AnyAsync(m => m.ProjectId == projectId && m.UserId == userId
            && (m.Role == "owner" || m.Role == "manager" || m.Role == "member"), ct);

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    private static TaskFileResult<T> Fail<T>(string code) => new(default, code);
}
