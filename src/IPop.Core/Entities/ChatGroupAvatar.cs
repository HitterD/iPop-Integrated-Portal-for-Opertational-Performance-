namespace IPop.Core.Entities;

/// A group chat photo (256 px JPEG, ~15 KB). Kept in the database like profile photos so it is in
/// every backup; served at /api/chat-avatars/{Name}, the URL stored in ChatRoom.AvatarUrl.
public class ChatGroupAvatar
{
    public required string Name { get; set; }   // "{guid:N}.jpg"
    public required byte[] Content { get; set; }
    public DateTime CreatedAt { get; set; }
}
