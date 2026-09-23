namespace IPop.Core.Entities;

public class ChatRoomMember
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid UserId { get; set; }
    public required string Role { get; set; }          // "owner", "admin", "member"
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }

    public ChatRoom Room { get; set; } = default!;
    public User User { get; set; } = default!;
}
