namespace IPop.Core.Entities;

public class ChatMembershipAudit
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public required string Action { get; set; }
    public Guid ActorId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime CreatedAt { get; set; }
    public ChatRoom Room { get; set; } = default!;
    public User Actor { get; set; } = default!;
    public User? TargetUser { get; set; }
}
