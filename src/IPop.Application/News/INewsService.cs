namespace IPop.Application.News;

public interface INewsService
{
    // CRUD + workflow
    Task<NewsResultDto> CreateAsync(Guid authorId, CreateNewsRequest req, CancellationToken ct);
    Task<NewsResultDto> UpdateAsync(Guid actorId, Guid newsId, UpdateNewsRequest req, CancellationToken ct);
    Task<NewsDetailDto?> GetAsync(Guid newsId, Guid viewerId, CancellationToken ct);
    Task<PagedResult<NewsSummaryDto>> ListAsync(Guid viewerId, NewsListFilter filter, CancellationToken ct);
    Task<NewsResultDto> SubmitAsync(Guid actorId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> ApproveAsync(Guid approverId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> RejectAsync(Guid approverId, Guid newsId, string reason, CancellationToken ct);
    Task<NewsResultDto> PublishAsync(Guid actorId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> UnpublishAsync(Guid actorId, Guid newsId, CancellationToken ct);

    // Pin
    Task<NewsResultDto> PinAsync(Guid actorId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> UnpinAsync(Guid actorId, Guid newsId, CancellationToken ct);

    // Mandatory acknowledgement
    Task<NewsResultDto> AcknowledgeAsync(Guid userId, Guid newsId, CancellationToken ct, int? expectedContentVersion = null);
    Task<NewsResultDto> RemindAckAsync(Guid actorId, Guid newsId, CancellationToken ct);
    Task<IReadOnlyList<NewsAuditDto>> GetAuditsAsync(Guid newsId, Guid viewerId, CancellationToken ct);
    Task<NewsAckReportDto?> GetAckReportAsync(Guid actorId, Guid newsId, CancellationToken ct);

    // HR/Admin management (news.publish, or news.write outside the Employee baseline)
    Task<bool> CanManageAsync(Guid userId, CancellationToken ct);
    Task<bool> CanViewAsync(Guid userId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> DeleteAsync(Guid actorId, Guid newsId, CancellationToken ct);

    // Reader state
    Task MarkReadAsync(Guid userId, Guid newsId, bool read, CancellationToken ct);
    Task<bool> SetBookmarkAsync(Guid userId, Guid newsId, bool saved, CancellationToken ct);
    Task<NewsCountsDto> GetCountsAsync(Guid viewerId, CancellationToken ct);
}

public record NewsListFilter(
    string? Type = null,          // filter by type
    string? Status = null,        // filter by status (admin/moderation view)
    bool? PinnedOnly = null,
    bool? RequiresAckOnly = null,
    string? Search = null,        // title/body ilike
    int Page = 1,
    int PageSize = 20,
    bool IncludeScheduled = false,  // if false, scheduled future posts are hidden from reader feed
    string? View = null,          // "unread" | "saved" | "drafts" | "scheduled" (last two HR/Admin)
    string? Sort = null           // "newest" (default) | "oldest" | "mostRead"
);
