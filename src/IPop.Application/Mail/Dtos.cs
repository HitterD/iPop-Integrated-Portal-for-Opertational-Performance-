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
    string? DraftExtra = null,
    // Group chips (shared DirGroups or the sender's personal groups), expanded at send time.
    IReadOnlyList<MailGroupRef>? Groups = null,
    // Recipients see only the sender and themselves.
    bool HideRecipients = false,
    // The count the user confirmed in the "Send to N people?" dialog; required above
    // MailLimits.ConfirmAbove and must match the server's count.
    int? ConfirmedRecipientCount = null);

/// A group chip in To/Cc/Bcc. Kind: "shared" (admin DirGroup) | "personal" (the sender's MailGroup).
public record MailGroupRef(string Field, string Kind, Guid Id);

public static class MailLimits
{
    public const int MaxRecipients = 1000;
    /// Above this many recipients the sender must confirm, and the mail counts as mass mail.
    public const int ConfirmAbove = 50;
    /// Above this many recipients the send is written to the audit log for admins.
    public const int AuditAbove = 100;
    /// The reading pane gets at most this many recipient names; the rest are paged.
    public const int DetailRecipients = 50;
}

public record SendMailResult(bool Success, Guid? MailId, string? Error, int? RecipientCount = null);

/// Server-side expansion of a compose request: the numbers shown in the confirm dialog.
public record RecipientPreviewDto(
    int Total, int Individuals, int Duplicates, int Limit, bool NeedsConfirm,
    IReadOnlyList<RecipientGroupCountDto> Groups,
    IReadOnlyList<SkippedRecipientDto> Skipped,
    IReadOnlyList<string> Invalid);

public record RecipientGroupCountDto(string Field, string Kind, Guid Id, string Name, int Count);
public record SkippedRecipientDto(string Nik, string Name);

/// Sender's delivery summary for a sent mail.
public record MailStatsDto(int Sent, int Delivered, int Read, IReadOnlyList<SkippedRecipientDto> Skipped);
public record MailReaderDto(string Name, string Nik, DateTime? ReadAt);

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
    /// All recipients the caller may see; Recipients holds at most MailLimits.DetailRecipients of them.
    public int RecipientTotal { get; init; }
    /// Groups the mail was sent to (names + counts; ids only for the sender).
    public IReadOnlyList<RecipientGroupCountDto> RecipientGroups { get; init; } = [];
    public bool IsMass { get; init; }
    public bool HideRecipients { get; init; }
    /// False for mass mail unless the caller sent it: reply goes to the sender only.
    public bool CanReplyAll { get; init; } = true;
    /// False when the sender has no active account (left the company, EPop archive sender):
    /// a reply cannot be delivered to them, so the composer must not address it to them.
    public bool SenderIsActive { get; init; } = true;
}

// Delivery/read state of one recipient, for the sender's "terkirim/dibaca" view.
public record DeliveryReadStateDto(Guid UserId, string Name, string Nik, string RecipientType, DateTime? DeliveredAt, DateTime? ReadAt);

public record AttachmentDto(Guid Id, string FileName, long FileSize, string MimeType, string? ThumbnailUrl);

// Result for bulk ops — reports how many were affected.
public record BulkResultDto(int Affected, string? Error = null);

/// Mail read receipts (spec 2026-10-14 §1–§3).
public static class MailReceipts
{
    /// AppSettings key: UTC instant receipts started. Mail sent before it reports Tracked = false.
    public const string TrackedSinceKey = "mail.receipts.trackedSince";
    /// Users created for EPop archive senders/recipients outside iPop: no receipt can exist for them.
    public const string ExternalNikPrefix = "EPOP-";
}

/// Found = the caller is on the mail; Recorded = this call wrote SeenAt (false when already seen,
/// the sender's own copy, not yet delivered, or sent before tracking started).
public record MailSeenResult(bool Found, bool Recorded, DateTime? SeenAt);

/// GET /api/mail/{id}/receipts. Total/Read/Delivered count people (Bcc only for the sender; external excluded).
/// NamesVisible = false (mass mail or hidden list, non-sender): counts only, People and Groups empty.
public record MailReceiptsDto(
    Guid MailId, bool Tracked, DateTime? TrackedSince, bool IsSender, bool NamesVisible,
    int Total, int Read, int Delivered,
    IReadOnlyList<MailReceiptPersonDto> People,
    IReadOnlyList<MailReceiptGroupDto> Groups,
    MailReceiptExternalDto External);

/// UserKey = opaque (user id "N" format), never a NIK. PhotoId → /api/photos/{PhotoId}; null = initials.
/// Field: "to" | "cc" | "bcc". SeenAt null = not yet (always null when the mail is not tracked).
public record MailReceiptPersonDto(
    string UserKey, string Name, string? Department, string? Site, string? PhotoId,
    string Field, bool ViaGroup, bool IsMe, DateTime? SeenAt, bool Delivered);

/// A group chip the mail went to, with its members' read count. MemberKeys match People[].UserKey.
public record MailReceiptGroupDto(string Field, string Kind, string Name, int Total, int Read, IReadOnlyList<string> MemberKeys);

/// Recipients with no iPop account (EPop archive addresses). Status is always "not_available".
public record MailReceiptExternalDto(int Count, string Status);
