namespace IPop.Core.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid SenderId { get; set; }
    public required string Type { get; set; }          // "text", "sticker", "system", "emergency"
    public required string Content { get; set; }
    public Guid IdempotencyKey { get; set; }
    public Guid? ReplyToId { get; set; }
    public Guid? ThreadParentId { get; set; }
    public int ThreadReplyCount { get; set; }
    public DateTime? LastThreadReplyAt { get; set; }
    public bool IsDeleted { get; set; }
    public string? DeletedByType { get; set; }         // "self", "admin", null
    // Metadata teruskan: pesan hasil forward membawa asal-usulnya (ruang + pengirim
    // asli) sehingga label "Diteruskan" tidak bergantung pada prefix teks.
    public Guid? ForwardedFromMessageId { get; set; }
    public string? ForwardedFromSenderName { get; set; }
    /// The sender dismissed the link preview card before sending.
    public bool LinkPreviewOff { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EditedAt { get; set; }

    public ChatRoom Room { get; set; } = default!;
    public User Sender { get; set; } = default!;
    public ChatMessage? ReplyTo { get; set; }
    public ChatMessage? ThreadParent { get; set; }
    public ICollection<ChatAttachment> Attachments { get; set; } = [];
    public ICollection<ChatReaction> Reactions { get; set; } = [];
    public ICollection<ChatMention> Mentions { get; set; } = [];
    public ICollection<ChatReadReceipt> ReadReceipts { get; set; } = [];
    public ICollection<ChatMessageEdit> EditHistory { get; set; } = [];
}
