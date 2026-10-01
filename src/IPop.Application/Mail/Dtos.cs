namespace IPop.Application.Mail;

// Folders a user's mail can sit in. Starred/read are flags on the same row, not folders.
public enum MailFolder { Inbox, Sent, Draft, Starred, Archive, Trash, Scheduled }

/// Compose payload. Recipients split by type and identified by NIK (the composer picks people by
/// name/NIK, and the directory employee has a stable Nik but no stable client-side Id); the service
/// resolves each NIK to a UserId and builds one MailRecipient row per (mail, user). AttachmentIds
/// refer to pre-uploaded MailAttachment rows (MailId == null pending).
public record ComposeMailRequest(
    string Subject,
    string Body,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? InReplyToId = null,
    Guid? ThreadRootId = null,
    string? Signature = null,
    DateTime? ScheduledAt = null,
    string? Importance = "normal",
    // Labels picked in the composer; applied to the sender's own copy (labels are per user).
    IReadOnlyList<Guid>? LabelIds = null,
    // Forward only: which of the original's attachments to carry over (null = all of them).
    IReadOnlyList<Guid>? ForwardAttachmentIds = null,
    // Drafts only: composer-only state the server stores verbatim (meeting fields, forward source).
    string? DraftExtra = null);

public record SendMailResult(bool Success, Guid? MailId, string? Error);

// One item in a folder listing, flattened for the list view.
public record MailSummaryDto(
    Guid Id,
    Guid SenderId,
    string SenderName,
    string SenderNik,
    string Subject,
    string Snippet,
    DateTime? ReceivedAt,
    bool IsRead,
    bool IsStarred,
    bool HasAttachment,
    int ThreadCount,
    bool IsThreadRoot,
    Guid? ThreadRootId,
    bool IsPinned = false,
    string Importance = "normal",
    DateTime? ScheduledAt = null,
    bool IsMuted = false,
    /// Label yang menempel pada surat ini (Fase 3 — Surat Lanjutan).
    IReadOnlyList<string>? Labels = null);

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

// Per-folder counters for the sidebar badges — one query for the whole set.
public record FolderCountsDto(
    int InboxUnread,
    int InboxTotal,
    int SentTotal,
    int DraftTotal,
    int StarredTotal,
    int ArchiveTotal,
    int TrashTotal,
    int ScheduledTotal = 0);

// Search/filter inputs — all optional, AND-ed together.
public record MailSearchRequest(
    string? Query = null,
    bool? StarredOnly = null,
    bool? UnreadOnly = null,
    string? SenderNik = null,
    /// Filter label (nama) — Surat Lanjutan.
    string? Label = null,
    /// Rentang tanggal kirim (UTC), inklusif batas bawah.
    DateTime? DateFrom = null,
    DateTime? DateTo = null,
    /// Hanya surat yang punya lampiran.
    bool? HasAttachment = null,
    string? Importance = null);

// Full message with resolved recipients for the reading pane.
public record MailRecipientDto(Guid UserId, string Name, string Nik, string Email, string RecipientType);
public record ProjectLink(Guid ProjectId, string Name, int Progress, string Status);

public record MailDetailDto(
    Guid Id,
    Guid SenderId,
    string SenderName,
    string SenderNik,
    string Subject,
    string Body,
    bool IsHtml,
    string? Signature,
    DateTime? SentAt,
    Guid? InReplyToId,
    Guid? ThreadRootId,
    int ThreadCount,
    bool IsStarred,
    bool IsRead,
    string Folder,
    string? PdfExportRequestedBy,
    DateTime? PdfExportRequestedAt,
    IReadOnlyList<MailRecipientDto> Recipients,
    IReadOnlyList<AttachmentDto> Attachments,
    IReadOnlyList<DeliveryReadStateDto> DeliveryState,
    bool IsPinned = false,
    string Importance = "normal",
    DateTime? ScheduledAt = null,
    bool IsMuted = false,
    string? BodyText = null)
{
    public IReadOnlyList<ProjectLink> LinkedProjects { get; init; } = [];
    /// The caller's own labels on this mail (labels are per user).
    public IReadOnlyList<Guid> LabelIds { get; init; } = [];
    /// Drafts only (to their author): the composer's opaque extras, see ComposeMailRequest.DraftExtra.
    public string? DraftExtra { get; init; }
}

// Delivery/read state of one recipient, for the sender's "terkirim/dibaca" view.
public record DeliveryReadStateDto(Guid UserId, string Name, string Nik, string RecipientType, DateTime? DeliveredAt, DateTime? ReadAt);

public record AttachmentDto(Guid Id, string FileName, long FileSize, string MimeType, string? ThumbnailUrl);

// Result for bulk ops — reports how many were affected.
public record BulkResultDto(int Affected, string? Error = null);
