namespace IPop.Core.Entities;

/// A user's relationship to a <see cref="Mail"/>. One row per (mail, user). The sender's own row
/// carries Folder="sent" (or "draft" while unsent); each To/CC/BCC target carries Folder="inbox".
/// Folder + flags are a single table so inbox/archive/trash/starred/read/delivery all collapse into
/// one updatable record, exactly the multi-recipient model the PRD calls for.
public class MailRecipient
{
    public Guid Id { get; set; }
    public Guid MailId { get; set; }
    public Guid UserId { get; set; }

    /// "to" | "cc" | "bcc" | "from" (the sender's own row).
    public required string RecipientType { get; set; }

    /// "inbox" | "sent" | "draft" | "scheduled" | "archive" | "trash". Starred/read/pinned are flags, not folders.
    public required string Folder { get; set; }

    public bool IsStarred { get; set; }
    public bool IsRead { get; set; }
    public bool IsPinned { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Mail Mail { get; set; } = default!;
    public User User { get; set; } = default!;
}
