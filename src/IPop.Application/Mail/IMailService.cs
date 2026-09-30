namespace IPop.Application.Mail;

public interface IMailService
{
    // Compose & delivery — To/CC/BCC, threading, attachment link, signature/paraf snapshot.
    Task<SendMailResult> SendAsync(Guid senderId, ComposeMailRequest req, CancellationToken ct);
    Task<SendMailResult> SaveDraftAsync(Guid userId, ComposeMailRequest req, CancellationToken ct);
    Task<SendMailResult> UpdateDraftAsync(Guid userId, Guid mailId, ComposeMailRequest req, CancellationToken ct);
    Task<SendMailResult> DiscardDraftAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> ForwardAsync(Guid senderId, Guid originalMailId, ComposeMailRequest req, CancellationToken ct);

    // Folder queries — per folder, scoped to the requesting user, with search/filter.
    Task<PagedResult<MailSummaryDto>> GetFolderAsync(Guid userId, MailFolder folder, int page, int pageSize, MailSearchRequest? search = null, CancellationToken ct = default);

    // Sidebar badges — all per-folder counters in one call.
    Task<FolderCountsDto> GetFolderCountsAsync(Guid userId, CancellationToken ct);

    // Threading
    Task<PagedResult<MailSummaryDto>> GetThreadAsync(Guid userId, Guid threadRootId, int page, int pageSize, CancellationToken ct);

    // Reading
    Task<MailDetailDto?> GetAsync(Guid userId, Guid mailId, CancellationToken ct);

    // Per-user folder actions — single-item
    Task<SendMailResult> MarkReadAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> MarkUnreadAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> ToggleStarAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> MoveToFolderAsync(Guid userId, Guid mailId, MailFolder folder, CancellationToken ct);
    Task<SendMailResult> DeleteAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<int> EmptyTrashAsync(Guid userId, CancellationToken ct);
    Task<bool> ToggleMuteAsync(Guid userId, Guid mailId, CancellationToken ct);

    // Per-user folder actions — bulk (nice-to-have: bulk archive/trash/star/restore/delete)
    Task<BulkResultDto> BulkMoveAsync(Guid userId, IReadOnlyList<Guid> mailIds, MailFolder folder, CancellationToken ct);
    Task<BulkResultDto> BulkToggleStarAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct);
    Task<BulkResultDto> BulkDeleteAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct);
    Task<BulkResultDto> BulkRestoreAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct);
    Task<BulkResultDto> MarkAllReadAsync(Guid userId, MailFolder folder, CancellationToken ct);
    Task<BulkResultDto> BulkMarkReadAsync(Guid userId, IReadOnlyList<Guid> mailIds, CancellationToken ct);

    // Delivery/read tracking — sender's view of who received/read.
    Task<IReadOnlyList<DeliveryReadStateDto>> GetDeliveryStateAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task RecordDeliveredAsync(Guid userId, Guid mailId, CancellationToken ct);

    // Signature/paraf + export/print stubs.
    Task<SendMailResult> RequestPdfExportAsync(Guid userId, Guid mailId, CancellationToken ct);

    // Scheduling & Pinning
    Task<SendMailResult> CancelScheduleAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> RescheduleAsync(Guid userId, Guid mailId, DateTime scheduledAt, CancellationToken ct);
    Task<SendMailResult> SendScheduledImmediatelyAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<SendMailResult> TogglePinAsync(Guid userId, Guid mailId, CancellationToken ct);
    Task<int> DeliverDueScheduledMailsAsync(CancellationToken ct);
}
