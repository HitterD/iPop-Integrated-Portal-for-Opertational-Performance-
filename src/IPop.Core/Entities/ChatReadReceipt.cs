namespace IPop.Core.Entities;

/// Composite PK: (MessageId, UserId)
/// Captures per-recipient delivery and read state for a message.
/// DeliveredAt is set when the message has been pushed to the recipient's active connection(s)
/// (or client has acknowledged receipt). ReadAt is set when the recipient has viewed the message.
public class ChatReadReceipt
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }

    /// When the message was delivered to this user (they received it on their client).
    public DateTime? DeliveredAt { get; set; }

    /// When the user explicitly read/viewed the message.
    public DateTime? ReadAt { get; set; }

    public ChatMessage Message { get; set; } = default!;
    public User User { get; set; } = default!;
}
