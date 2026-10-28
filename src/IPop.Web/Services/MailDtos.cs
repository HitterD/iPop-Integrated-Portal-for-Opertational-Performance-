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
    bool IsMuted = false,
    /// Label pengguna pada surat ini (Fase 3 — Surat Lanjutan).
    IReadOnlyList<string>? Labels = null);

public record MailCounts(
    int InboxUnread, int InboxTotal, int SentTotal, int DraftTotal,
    int StarredTotal, int ArchiveTotal, int TrashTotal, int ScheduledTotal = 0, int InboxStarred = 0);

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
    bool IsPinned = false, string Importance = "normal", DateTime? ScheduledAt = null,
    bool IsMuted = false, string? BodyText = null)
{
    public IReadOnlyList<ProjectLink> LinkedProjects { get; init; } = [];
    /// The reader's own labels on this mail.
    public IReadOnlyList<Guid> LabelIds { get; init; } = [];
    /// Drafts only: the composer's own extras (meeting fields, forward source), stored verbatim.
    public string? DraftExtra { get; init; }
    /// People the reader may see (excluding the sender); Recipients holds at most 50 of them.
    public int RecipientTotal { get; init; }
    public IReadOnlyList<RecipientGroupCount> RecipientGroups { get; init; } = [];
    /// More than 50 recipients or sent to a group: reply-all is off for everyone but the sender.
    public bool IsMass { get; init; }
    public bool HideRecipients { get; init; }
    public bool CanReplyAll { get; init; } = true;
    /// False when the sender has no active account: replies cannot reach them.
    public bool SenderIsActive { get; init; } = true;
}

/// Error is the server's human message; Code is the machine one (confirm_required, …).
public record SendMailResult(bool Success, Guid? MailId, string? Error, int? RecipientCount = null)
{
    public string? Code { get; init; }
}

/// A group chip in To/Cc/Bcc. Kind: "shared" (admin directory group) | "personal".
public record MailGroupRef(string Field, string Kind, Guid Id);

public static class MailLimits
{
    public const int MaxRecipients = 1000;
    public const int ConfirmAbove = 50;
}

public record RecipientGroupCount(string Field, string Kind, Guid Id, string Name, int Count);
public record SkippedRecipient(string Nik, string Name);
public record RecipientPreview(int Total, int Individuals, int Duplicates, int Limit, bool NeedsConfirm,
    IReadOnlyList<RecipientGroupCount> Groups, IReadOnlyList<SkippedRecipient> Skipped, IReadOnlyList<string> Invalid);
public record MailStats(int Sent, int Delivered, int Read, IReadOnlyList<SkippedRecipient> Skipped);
public record MailReader(string Name, string Nik, DateTime? ReadAt);

public record MailGroupSummary(Guid Id, string Kind, string Name, int Count);
public record ReplyAllInfo(IReadOnlyList<MailRecipient> People, IReadOnlyList<RecipientGroupCount> Groups);
public record MailGroupList(IReadOnlyList<MailGroupSummary> Shared, IReadOnlyList<MailGroupSummary> Personal);
public record MailGroupMember(string Nik, string Name, string? Department, string? Site, bool IsActive);
public record MailGroupDetail(Guid Id, string Kind, string Name, int Count, IReadOnlyList<MailGroupMember> Members);
public record SaveMailGroup(string? Name, IReadOnlyList<string>? Niks);

public record MailSendRequest(
    string Subject, string Body,
    IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc,
    IReadOnlyList<Guid>? AttachmentIds = null,
    Guid? InReplyToId = null, Guid? ThreadRootId = null,
    string? Signature = null,
    DateTime? ScheduledAt = null,
    string? Importance = "normal",
    IReadOnlyList<Guid>? LabelIds = null,
    IReadOnlyList<Guid>? ForwardAttachmentIds = null,
    string? DraftExtra = null,
    IReadOnlyList<MailGroupRef>? Groups = null,
    bool HideRecipients = false,
    int? ConfirmedRecipientCount = null);

public record BatchIds(IReadOnlyList<Guid> MailIds);
public record BatchMove(IReadOnlyList<Guid> MailIds, string Folder);
public record FolderReq(string Folder);
public record MailLabel(Guid Id, string Name, string Color, int Count);

/// GET /api/mail/{id}/receipts (spec 2026-10-14 §1–§3). Mirrors IPop.Application.Mail.MailReceiptsDto.
public record MailReceipts(
    Guid MailId, bool Tracked, DateTime? TrackedSince, bool IsSender, bool NamesVisible,
    int Total, int Read, int Delivered,
    IReadOnlyList<MailReceiptPerson> People,
    IReadOnlyList<MailReceiptGroup> Groups,
    MailReceiptExternal External);

/// UserKey is opaque (never a NIK); PhotoId → /api/photos/{PhotoId}.
public record MailReceiptPerson(
    string UserKey, string Name, string? Department, string? Site, string? PhotoId,
    string Field, bool ViaGroup, bool IsMe, DateTime? SeenAt, bool Delivered);

public record MailReceiptGroup(string Field, string Kind, string Name, int Total, int Read, IReadOnlyList<string> MemberKeys);

public record MailReceiptExternal(int Count, string Status);
