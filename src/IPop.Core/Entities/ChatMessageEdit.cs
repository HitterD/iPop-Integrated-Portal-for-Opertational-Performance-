namespace IPop.Core.Entities;

public class ChatMessageEdit
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public required string PreviousContent { get; set; }
    public DateTime EditedAt { get; set; }

    public ChatMessage Message { get; set; } = default!;
}
