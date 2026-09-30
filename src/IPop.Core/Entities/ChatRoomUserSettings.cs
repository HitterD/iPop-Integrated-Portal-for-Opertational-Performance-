namespace IPop.Core.Entities;

/// Composite PK: (UserId, RoomId)
public class ChatRoomUserSettings
{
    public Guid UserId { get; set; }
    public Guid RoomId { get; set; }
    public bool IsMuted { get; set; }
    public DateTime? MuteExpiresAt { get; set; }
    public bool IsArchived { get; set; }
    public bool IsPinned { get; set; }
    public int PinOrder { get; set; }
    public DateTime? LastReadMessageAt { get; set; }

    /// Per-room (especially DM) preference: whether to send read receipts to the other party.
    /// For DMs this is reciprocal — receipts are only shown to the sender if the recipient has this enabled.
    /// Default true to preserve existing behavior.
    public bool ReadReceiptsEnabled { get; set; } = true;
    public DateTime? ClearedAt { get; set; }

    /// "Hapus chat" for me: the room stays out of my list until a message newer than this arrives.
    public DateTime? DeletedAt { get; set; }

    public User User { get; set; } = default!;
    public ChatRoom Room { get; set; } = default!;
}
