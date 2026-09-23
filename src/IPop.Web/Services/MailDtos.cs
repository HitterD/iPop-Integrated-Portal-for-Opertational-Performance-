namespace IPop.Web.Services;

// Mirror of IPop.Application.Mail DTOs — the Web project decodes the API surface directly.
public enum MailFolder { Inbox, Sent, Draft, Starred, Archive, Trash, Scheduled }

public record MailPaged<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public record MailSummary(
    Guid Id, Guid SenderId, string SenderName, string SenderNik,
    string Subject, string Snippet, DateTime? ReceivedAt,
    bool IsRead, bool IsStarred, bool HasAttachment,
    int ThreadCount, bool IsThreadRoot, Guid? ThreadRootId,
    bool IsPinned = false, string Importance = "normal", DateTime? ScheduledAt = null,
    /// Label pengguna pada surat ini (Fase 3 — Surat Lanjutan).
    IReadOnlyList<string>? Labels = null);

public record MailCounts(
    int InboxUnread, int InboxTotal, int SentTotal, int DraftTotal,
    int StarredTotal, int ArchiveTotal, int TrashTotal, int ScheduledTotal = 0);

public record MailRecipient(Guid UserId, string Name, string Nik, string Email, string RecipientType);

public record MailAttachment(Guid Id, string FileName, long FileSize, string MimeType, string? ThumbnailUrl);

public record MailDeliveryState(Guid UserId, string Name, string Nik, string RecipientType, DateTime? DeliveredAt, DateTime? ReadAt);

public record ProjectLink(Guid ProjectId, string Name, int Progress, string Status);

public record MailDetail(
    Guid Id, Guid SenderId, string SenderName, string SenderNik,
    string Subject, string Body, bool IsHtml, string? Signature,
    DateTime? SentAt, Guid? InReplyToId, Guid? ThreadRootId,
    int ThreadCount, bool IsStarred, bool IsRead, string Folder,
    string? PdfExportRequestedBy, DateTime? PdfExportRequestedAt,
    IReadOnlyList<MailRecipient> Recipients,
    IReadOnlyList<MailAttachment> Attachments,
    IReadOnlyList<MailDeliveryState> DeliveryState,
    bool IsPinned = false, string Importance = "normal", DateTime? ScheduledAt = null)
{
    public IReadOnlyList<ProjectLink> LinkedProjects { get; init; } = [];
}

public record SendMailResult(bool Success, Guid? MailId, string? Error);

public record MailSendRequest(
    string Subject, string Body,
    IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc,
    IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? InReplyToId = null, Guid? ThreadRootId = null,
    string? Signature = null,
    DateTime? ScheduledAt = null,
    string? Importance = "normal");

public record BatchIds(IReadOnlyList<Guid> MailIds);
public record BatchMove(IReadOnlyList<Guid> MailIds, string Folder);
public record FolderReq(string Folder);
public record MailLabel(Guid Id, string Name, string Color, int Count);
