namespace IPop.Core.Entities;

/// Composite PK: (MessageId, UserId, Emoji)
public class ChatReaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public required string Emoji { get; set; }
    public DateTime CreatedAt { get; set; }

    public ChatMessage Message { get; set; } = default!;
    public User User { get; set; } = default!;
}
