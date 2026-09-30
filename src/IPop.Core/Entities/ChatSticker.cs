namespace IPop.Core.Entities;

/// A custom image sticker: 512×512 WebP (256×256 before Oct 2026) on the NAS (or chat-files/stickers when there is no service account).
/// Sent as a chat message with Type "sticker" and Content "custom:{Id:N}".
public class ChatSticker
{
    public Guid Id { get; set; }
    public Guid CreatedById { get; set; }
    public required string StorageServer { get; set; }
    public required string StoragePath { get; set; }
    public long SizeBytes { get; set; }
    /// Promoted by an admin into the "Resmi" pack everyone sees.
    public bool IsOfficial { get; set; }
    public DateTime CreatedAt { get; set; }

    public User CreatedBy { get; set; } = default!;
}

/// A sticker in a user's own collection ("Stiker saya"): the ones they made plus the ones they saved.
public class UserSticker
{
    public Guid UserId { get; set; }
    public Guid StickerId { get; set; }
    public DateTime AddedAt { get; set; }

    public User User { get; set; } = default!;
    public ChatSticker Sticker { get; set; } = default!;
}
