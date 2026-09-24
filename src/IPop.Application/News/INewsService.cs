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
    Task<NewsResultDto> AcknowledgeAsync(Guid userId, Guid newsId, CancellationToken ct);
    Task<NewsResultDto> RemindAckAsync(Guid actorId, Guid newsId, CancellationToken ct);
    Task<IReadOnlyList<NewsAuditDto>> GetAuditsAsync(Guid newsId, Guid viewerId, CancellationToken ct);
}

public record NewsListFilter(
    string? Type = null,          // filter by type
    string? Status = null,        // filter by status (admin/moderation view)
    bool? PinnedOnly = null,
    bool? RequiresAckOnly = null,
    string? Search = null,        // title/body ilike
    int Page = 1,
    int PageSize = 20,
    bool IncludeScheduled = false  // if false, scheduled future posts are hidden from reader feed
);
