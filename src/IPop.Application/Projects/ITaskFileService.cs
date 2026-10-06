namespace IPop.Application.Projects;

/// A task file stored on the NAS through the shared attachment store (area "projects").
/// Id is the link id (used for delete); AttachmentId feeds GET /api/attachments/{id}.
/// ThumbUrl is set for images only (same url — the browser scales it).
public record TaskFileDto(Guid Id, Guid AttachmentId, string Kind, string FileName, string ContentType, long Size,
    string UploadedByName, Guid UploadedById, DateTime CreatedAt, string Url, string? ThumbUrl);

/// Project-wide "recent evidence" row: the file plus the task it belongs to.
public record ProjectRecentFileDto(Guid Id, Guid AttachmentId, string Kind, string FileName, string ContentType, long Size,
    string UploadedByName, Guid UploadedById, DateTime CreatedAt, string Url, string? ThumbUrl,
    Guid TaskId, string TaskTitle);

/// Error codes: "task_not_found", "project_not_found", "file_not_found", "forbidden", "invalid_kind".
public record TaskFileResult<T>(T? Value, string? Error)
{
    public bool Success => Error is null;
}

public interface ITaskFileService
{
    /// Stores the bytes (may throw AttachmentRejectedException / SynoException), links them to the
    /// task and writes a "file_added" activity row. Members of the task's project only.
    Task<TaskFileResult<TaskFileDto>> UploadAsync(Guid actorId, Guid taskId, string kind, string fileName,
        string contentType, Stream content, CancellationToken ct);
    /// All kinds, not deleted, newest first.
    Task<TaskFileResult<IReadOnlyList<TaskFileDto>>> ListAsync(Guid actorId, Guid taskId, CancellationToken ct);
    /// Soft delete: the uploader or a project owner; writes a "file_removed" activity row.
    Task<TaskFileResult<bool>> DeleteAsync(Guid actorId, Guid taskId, Guid fileId, CancellationToken ct);
    /// Newest field photos and deliverables across the project (take clamped to 1..50).
    Task<TaskFileResult<IReadOnlyList<ProjectRecentFileDto>>> RecentAsync(Guid actorId, Guid projectId, int take, CancellationToken ct);
}
