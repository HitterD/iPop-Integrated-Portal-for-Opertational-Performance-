namespace IPop.Core.Entities;

public class ChatPinnedMessage
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid MessageId { get; set; }
    public Guid PinnedByUserId { get; set; }
    public DateTime PinnedAt { get; set; }

    public ChatRoom Room { get; set; } = default!;
    public ChatMessage Message { get; set; } = default!;
    public User PinnedByUser { get; set; } = default!;
}
