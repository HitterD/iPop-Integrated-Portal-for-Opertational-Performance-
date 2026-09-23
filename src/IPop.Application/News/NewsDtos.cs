namespace IPop.Application.News;

// ── Create / Update payloads ─────────────────────────────────────────────

public record CreateNewsRequest(
    string Type,                  // "news" | "announcement" | "memo" | "condolence"
    string Title,
    string Body,
    DateTime? PublishAt = null,   // scheduled publish; null = immediately on approve
    bool RequiresAcknowledgement = false,
    bool IsPinned = false,
    IReadOnlyList<NewsTargetInput>? Targets = null);

public record UpdateNewsRequest(
    string? Title = null,
    string? Body = null,
    string? Type = null,
    DateTime? PublishAt = null,
    bool? RequiresAcknowledgement = null,
    bool? IsPinned = null,
    IReadOnlyList<NewsTargetInput>? Targets = null);

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
    DateTime UpdatedAt);

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
    DateTime UpdatedAt);

public record NewsTargetDto(string Kind, string? Value);
public record NewsAuditDto(Guid Id, Guid ActorId, string ActorName, string Action, string? Detail, DateTime CreatedAt);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

// ── Result wrappers ──────────────────────────────────────────────────────

public record NewsResultDto(bool Success, NewsDetailDto? News, string? Error);
