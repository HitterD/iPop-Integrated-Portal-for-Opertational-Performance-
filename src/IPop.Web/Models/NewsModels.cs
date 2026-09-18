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
    DateTime UpdatedAt
);

public record NewsTargetDto(string Kind, string? Value);

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
    DateTime UpdatedAt
);

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
    IReadOnlyList<NewsTargetInput> Targets
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
        new("", "Semua Status"),
        new("published", "Published"),
        new("pending", "Pending Approval"),
        new("draft", "Draft"),
        new("rejected", "Rejected")
    ];

    public static readonly List<AppOption> FormTypeOptions = [
        new("news", "News (Berita Korporat)"),
        new("memo", "Internal Memo (IM / Nota Dinas)"),
        new("condolence", "Berita Duka Cita"),
        new("announcement", "Pengumuman Resmi (Announcement)")
    ];

    public static readonly List<AppOption> TargetKindOptions = [
        new("all", "Semua Karyawan (All)"),
        new("department", "Departemen Tertentu"),
        new("role", "Jabatan / Role Tertentu"),
        new("user", "Karyawan Spesifik (NIK)")
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
        "condolence" or "duka" => "Berita Duka",
        "memo" => "Internal Memo",
        "announcement" => "Pengumuman",
        "news" => "News",
        _ => "Semua"
    };

    public static string StatusLabel(string s) => s switch
    {
        "published" => "Published",
        "pending" => "Pending Review",
        "draft" => "Draft",
        "rejected" => "Rejected",
        _ => s
    };

    public static string FormatAuditAction(string action) => action switch
    {
        "created" => "Dibuat",
        "submitted" => "Diajukan",
        "approved" => "Disetujui",
        "published" => "Dipublikasikan",
        "rejected" => "Ditolak",
        "unpublished" => "Ditarik",
        "pinned" => "Disematkan",
        "unpinned" => "Lepas Sematan",
        "acknowledged" => "Dikonfirmasi Baca",
        _ => action
    };

    public static string FormatDate(DateTime? d) => d is null ? "—" : d.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm");

    public static string PreviewText(string t, string type) => t.Length > 120 ? t[..120] + "…" : t;

    public static int ReadTime(string? body) => Math.Max(1, (int)Math.Ceiling((body?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0) / 200.0));

    public static int AckPercent(NewsDetailDto d) => d.AckTotal == 0 ? 0 : Math.Clamp((int)Math.Round(100.0 * d.AckCount / d.AckTotal), 0, 100);
}
