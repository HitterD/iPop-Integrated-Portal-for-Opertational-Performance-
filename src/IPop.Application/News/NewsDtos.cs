namespace IPop.Application.News;

// ── Create / Update payloads ─────────────────────────────────────────────

public record CreateNewsRequest(
    string Type,                  // "news" | "announcement" | "memo" | "condolence"
    string Title,
    string Body,
    DateTime? PublishAt = null,   // scheduled publish; null = immediately on approve
    bool RequiresAcknowledgement = false,
    bool IsPinned = false,
    IReadOnlyList<NewsTargetInput>? Targets = null,
    DateTime? AckDueAt = null,
    string? Mode = null,          // "draft" (default) | "publish" | "schedule" (uses PublishAt)
    IReadOnlyList<Guid>? AttachmentIds = null);

public record UpdateNewsRequest(
    string? Title = null,
    string? Body = null,
    string? Type = null,
    DateTime? PublishAt = null,
    bool? RequiresAcknowledgement = null,
    bool? IsPinned = null,
    IReadOnlyList<NewsTargetInput>? Targets = null,
    DateTime? AckDueAt = null,
    bool? ClearAckDueAt = null,
    bool? ClearPublishAt = null,
    IReadOnlyList<Guid>? AttachmentIds = null);   // full replace when non-null

public record NewsTargetInput(string Kind, string? Value); // kind: all|department|role|user|project; value: code/name/nik/project GUID

// ── Read DTOs ────────────────────────────────────────────────────────────

public record NewsSummaryDto(
    Guid Id,
    string Type,
    string Title,
    string Status,
    string AuthorName,
    string AuthorNik,
    DateTime? PublishAt,
    DateTime? PublishedAt,
    bool IsPinned,
    DateTime? PinnedAt,
    bool RequiresAcknowledgement,
    IReadOnlyList<NewsTargetDto> Targets,
    bool IsAcknowledged,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Summary = "",
    bool IsRead = false,
    bool IsSaved = false,
    int ViewCount = 0,
    int AttachmentCount = 0,
    DateTime? AckDueAt = null,
    DateTime? EditedAt = null,
    NewsVideoBriefDto? Video = null);

public record NewsDetailDto(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string Status,
    Guid AuthorId,
    string AuthorName,
    string AuthorNik,
    Guid? ApproverId,
    string? ApproverName,
    string? RejectionReason,
    DateTime? PublishAt,
    DateTime? PublishedAt,
    bool IsPinned,
    DateTime? PinnedAt,
    bool RequiresAcknowledgement,
    IReadOnlyList<NewsTargetDto> Targets,
    IReadOnlyList<NewsAuditDto> Audits,
    bool IsAcknowledged,
    int AckCount,
    int AckTotal,                 // 0 when not mandatory; else targeted user count
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? AckDueAt = null,
    DateTime? AcknowledgedAt = null,
    DateTime? EditedAt = null,
    bool IsSaved = false,
    int ViewCount = 0,
    IReadOnlyList<NewsAttachmentDto>? Attachments = null,
    bool CanManage = false,       // HR/Admin: ack report, audits, remind
    bool CanEdit = false,         // author of the post, or HR/Admin
    bool CanDelete = false,       // author of the post, or HR/Admin
    bool CanPublish = false,      // publish / unpublish / pin: HR/Admin only
    NewsVideoBriefDto? Video = null);

public record NewsAttachmentDto(Guid Id, string FileName, string ContentType, long Size);

/// One targeted reader in the acknowledgement report (HR/Admin only).
public record NewsAckEntryDto(Guid UserId, string Nik, string Name, string? Department, string? Position, DateTime? AcknowledgedAt);
public record NewsAckReportDto(int Total, int Acknowledged, IReadOnlyList<NewsAckEntryDto> Entries);

/// Counts for the left navigation; HR/Admin also see drafts and scheduled.
public record NewsCountsDto(int All, int Unread, int NeedsAck, int Saved, int Pinned, int Scheduled, int Drafts, bool CanManage);

public record NewsTargetDto(string Kind, string? Value);
public record NewsAuditDto(Guid Id, Guid ActorId, string ActorName, string Action, string? Detail, DateTime CreatedAt);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

// ── Result wrappers ──────────────────────────────────────────────────────

public record NewsResultDto(bool Success, NewsDetailDto? News, string? Error);
