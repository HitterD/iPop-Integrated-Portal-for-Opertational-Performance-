namespace IPop.Web.Models;
using IPop.Web.Components;

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
    NewsVideoBriefDto? Video = null,
    string BodyFormat = "plain",
    int ContentVersion = 0,
    Guid? CoverMediaId = null,
    string CoverMode = "auto"
);

public record NewsTargetDto(string Kind, string? Value);
public record NewsCommentDto(Guid Id, string AuthorName, string Body, DateTime CreatedAt);
public record NewsDiscussionDto(int LikeCount, bool IsLiked, IReadOnlyList<NewsCommentDto> Comments);

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
    int AckTotal,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? AckDueAt = null,
    DateTime? AcknowledgedAt = null,
    DateTime? EditedAt = null,
    bool IsSaved = false,
    int ViewCount = 0,
    IReadOnlyList<NewsAttachmentDto>? Attachments = null,
    bool CanManage = false,
    bool CanEdit = false,
    bool CanDelete = false,
    bool CanPublish = false,
    NewsVideoBriefDto? Video = null,
    string BodyFormat = "plain",
    int ContentVersion = 0,
    Guid? CoverMediaId = null,
    string CoverMode = "auto"
);

public record NewsAttachmentDto(Guid Id, string FileName, string ContentType, long Size);
public record NewsAckEntryDto(Guid UserId, string Nik, string Name, string? Department, string? Position, DateTime? AcknowledgedAt);
public record NewsAckReportDto(int Total, int Acknowledged, IReadOnlyList<NewsAckEntryDto> Entries);
public record NewsCountsDto(int All, int Unread, int NeedsAck, int Saved, int Pinned, int Scheduled, int Drafts, bool CanManage);

public record NewsAuditDto(
    Guid Id,
    Guid ActorId,
    string ActorName,
    string Action,
    string? Detail,
    DateTime CreatedAt
);

public record NewsTargetInput(string Kind, string? Value);

public record NewsSaveRequest(
    string Type,
    string Title,
    string Body,
    DateTime? PublishAt,
    bool RequiresAcknowledgement,
    bool IsPinned,
    IReadOnlyList<NewsTargetInput> Targets,
    DateTime? AckDueAt = null,
    IReadOnlyList<Guid>? AttachmentIds = null,
    /// "draft" | "publish" | "schedule" (create only; edits keep the post's current state)
    string Mode = "draft"
);

public record PagedNewsResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public static class NewsUiHelper
{
    public static readonly List<AppOption> StatusOptions = [
        new("", "All Statuses"),
        new("published", "Published"),
        new("draft", "Draft")
    ];

    public static readonly List<AppOption> FormTypeOptions = [
        new("news", "News (Corporate News)"),
        new("memo", "Internal Memo (IM)"),
        new("condolence", "Condolence Announcement"),
        new("announcement", "Official Announcement")
    ];

    public static readonly List<AppOption> TargetKindOptions = [
        new("all", "All Employees (All)"),
        new("department", "Specific Department"),
        new("role", "Specific Role"),
        new("user", "Specific employee")
    ];

    public static string GetCategoryIcon(string t) => t switch
    {
        "condolence" or "duka" => "heart",
        "memo" => "file-text",
        "announcement" => "megaphone",
        _ => "newspaper"
    };

    public static string GetCategoryLabel(string t) => t switch
    {
        "condolence" or "duka" => "Condolence",
        "memo" => "Internal Memo",
        "announcement" => "Announcement",
        "news" => "News",
        _ => "All"
    };

    public static string StatusLabel(string s) => s switch
    {
        "published" => "Published",
        // Legacy approval states read as drafts: HR/Admin publish directly.
        "draft" or "pending" or "rejected" => "Draft",
        _ => s
    };

    public static string FormatAuditAction(string action) => action switch
    {
        "created" => "Created",
        "submitted" => "Submitted",
        "approved" => "Approved",
        "published" => "Published",
        "rejected" => "Rejected",
        "unpublished" => "Unpublished",
        "pinned" => "Pinned",
        "unpinned" => "Unpinned",
        "acknowledged" => "Acknowledged",
        "edited" => "Edited",
        "reminded_ack" => "Sent acknowledgement reminder",
        _ => action
    };

    public static string FormatDate(DateTime? d) => d is null ? "—" : d.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm");

    public static string PreviewText(string t, string type) => t.Length > 120 ? t[..120] + "…" : t;

    public static int ReadTime(string? body) => Math.Max(1, (int)Math.Ceiling((body?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0) / 200.0));

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    public static string FileIcon(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "file-text",
        ".xls" or ".xlsx" or ".csv" => "file-spreadsheet",
        ".png" or ".jpg" or ".jpeg" => "file-image",
        _ => "file"
    };

    public static int AckPercent(NewsDetailDto d) => d.AckTotal == 0 ? 0 : Math.Clamp((int)Math.Round(100.0 * d.AckCount / d.AckTotal), 0, 100);
}
