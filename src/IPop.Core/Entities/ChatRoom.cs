namespace IPop.Core.Entities;

public class ChatRoom
{
    public Guid Id { get; set; }
    public required string Type { get; set; }          // "dm" or "group"
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// Warna avatar grup pilihan admin (hex, mis. "#2e7d5b"); null = palet default dari nama.
    public string? AvatarColor { get; set; }
    /// Foto grup (path relatif di storage chat-files); null = avatar inisial + warna.
    public string? AvatarUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime LastMessageAt { get; set; }


    public ICollection<ChatRoomMember> Members { get; set; } = [];
    public ICollection<ChatMessage> Messages { get; set; } = [];
    public ICollection<ChatPinnedMessage> PinnedMessages { get; set; } = [];
}
