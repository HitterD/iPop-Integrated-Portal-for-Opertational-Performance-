namespace IPop.Core.Entities;

/// Mail envelope — the message itself. Per-user view state (inbox/sent/archive/trash/starred/read)
/// lives on <see cref="MailRecipient"/> so folders are per (mail, user), matching the relational
/// MailRecipients table the PRD mandates for multi-recipient management.
public class Mail
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }          // Rich Text / HTML content from the composer RTE
    public bool IsHtml { get; set; } = true;           // body is HTML from the RTE (false = plain text)
    public string? Signature { get; set; }             // signature / paraf snapshot (stub)

    // Threading — direct parent for the reply chain; ThreadRoot telescopes to the first message.
    public Guid? InReplyToId { get; set; }
    public Guid? ThreadRootId { get; set; }
    public int ThreadCount { get; set; }
    public DateTime? LastThreadAt { get; set; }

    public string Status { get; set; } = "draft";      // "draft" | "sent" | "scheduled"
    public string Importance { get; set; } = "normal"; // "high" | "normal" | "low"
    public DateTime? ScheduledAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public bool HasAttachment { get; set; }
    /// Drafts only: JSON of the composer state that has no row yet — To/Cc/Bcc NIKs (recipient
    /// rows are created on send) plus the client's opaque extras (meeting fields, forward source).
    public string? DraftState { get; set; }

    /// Mass mail: true when sent to more than 50 people or through a group. Recipients other than
    /// the sender can then only reply to the sender (no reply all).
    public bool IsMass { get; set; }
    /// Recipients see only the sender and themselves (Bcc-like), not the list.
    public bool HideRecipients { get; set; }
    /// Distinct recipients at send time (excluding the sender).
    public int RecipientCount { get; set; }
    /// JSON of the groups the sender picked: [{ field, kind, id, name, count }] — shown instead of
    /// hundreds of names.
    public string? RecipientGroupsJson { get; set; }
    /// JSON of group members skipped because their account is inactive: [{ nik, name }].
    public string? SkippedJson { get; set; }

    public string? PdfExportRequestedBy { get; set; }  // PDF export stub: records who asked; real export is Wave-2
    public DateTime? PdfExportRequestedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public User Sender { get; set; } = default!;
    public Mail? InReplyTo { get; set; }
    public Mail? ThreadRoot { get; set; }
    public ICollection<MailRecipient> Recipients { get; set; } = [];
    public ICollection<MailAttachment> Attachments { get; set; } = [];
}
