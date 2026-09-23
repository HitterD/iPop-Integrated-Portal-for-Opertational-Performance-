namespace IPop.Core.Entities;

public class ChatMention
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public bool IsAllMention { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? NotifiedAt { get; set; }
    public int NotifyAttempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public bool SuppressNotification { get; set; }
    public ChatMessage Message { get; set; } = default!;
    public User User { get; set; } = default!;
}
