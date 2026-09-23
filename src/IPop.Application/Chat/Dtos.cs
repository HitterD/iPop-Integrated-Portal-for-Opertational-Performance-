namespace IPop.Application.Chat;

// Message DTOs
public record SendMessageRequest(Guid RoomId, string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, IReadOnlyList<Guid>? AttachmentIds = null);
public record MessageDto(Guid Id, Guid RoomId, Guid SenderId, string SenderName, string SenderNik,
    string Type, string Content, DateTime CreatedAt, DateTime? EditedAt, bool IsDeleted,
    string? DeletedByType, Guid? ReplyToId, ReplyPreviewDto? ReplyPreview,
    Guid? ThreadParentId,
    int ThreadReplyCount, DateTime? LastThreadReplyAt,
    IReadOnlyList<AttachmentDto> Attachments, IReadOnlyList<ReactionGroupDto> Reactions,
    IReadOnlyList<string> MentionedNiks,
    bool MentionsAll,
    Guid IdempotencyKey,
    /// For messages sent by the current viewer: the highest delivery tier visible to them.
    /// "sent" | "delivered" | "read" | null (for received messages or when not applicable).
    /// Respects per-DM ReadReceiptsEnabled reciprocal toggle.
    string? DeliveryStatus = null,
    /// Metadata teruskan: nama pengirim asli bila pesan ini hasil forward.
    string? ForwardedFromSenderName = null);
public record MessageAckDto(Guid MessageId, DateTime ServerTimestamp, Guid IdempotencyKey);
public record ReplyPreviewDto(string SenderName, string Content);

// Room DTOs
public record RoomListDto(Guid Id, string Type, string? Name, string? RecipientNik,
    string? RecipientName, string? LastMessage, DateTime? LastMessageAt,
    int UnreadCount, bool IsMuted, bool IsArchived, bool IsPinned,
    string? PresenceStatus, bool HasDraft, bool HasUnreadMention, DateTime? MuteExpiresAt,
    /// For the current user: whether read receipts are enabled for this room (reciprocal for DMs).
    bool ReadReceiptsEnabled = true,
    /// Warna avatar grup pilihan admin; null = palet default dari nama.
    string? AvatarColor = null);
public record ChatRoomDto(Guid Id, string Type, string? Name, string? Description, int MemberCount,
    int OnlineCount, IReadOnlyList<MemberDto> Members, string? CurrentUserRole, IReadOnlyList<MentionUserDto> MentionDirectory,
    /// Current user's per-room setting for read receipts (reciprocal for DMs).
    bool ReadReceiptsEnabled = true,
    /// Warna avatar grup pilihan admin; null = palet default dari nama.
    string? AvatarColor = null);
public record MentionUserDto(Guid UserId, string Nik, string Name, bool IsActive);
public record CreateGroupRequest(string Name, IReadOnlyList<Guid> MemberIds);
public record UpdateGroupIdentityRequest(string Name, string? Description, string? AvatarColor = null);

// Attachment DTOs
public record AttachmentDto(Guid Id, string FileName, long FileSize, string MimeType, string? ThumbnailUrl);

// Reaction DTOs
public record ReactionGroupDto(string Emoji, int Count, bool CurrentUserReacted);
public record ReactionDto(Guid MessageId, string Emoji, int Count, Guid UserId, bool Added);

// Seen DTOs
public record SeenByDto(Guid UserId, string UserName, string? AvatarUrl, DateTime? DeliveredAt, DateTime? ReadAt);
public record SeenUpdateDto(Guid MessageId, SeenByDto Reader, int TotalReadCount, int TotalMemberCount);
public record SeenListDto(IReadOnlyList<SeenByDto> Readers, int TotalReaders, int ActiveMemberCount);

// Typing DTOs
public record TypingDto(Guid RoomId, string UserNik, string DisplayName, bool IsTyping);

// Member DTOs
public record MemberDto(Guid UserId, string Nik, string Name, string Role, string? PresenceStatus);
public record MemberChangeDto(Guid RoomId, string Action, string ActorName, string TargetName, Guid? TargetUserId);

// Pinned DTOs
public record PinnedMessageDto(Guid Id, Guid MessageId, string SenderName, string Content, DateTime PinnedAt);
/// Simpanan pribadi per pengguna: membawa RoomId agar daftar bisa dikelompokkan per ruang.
public record SavedMessageDto(Guid Id, Guid RoomId, Guid MessageId, string SenderName, string Content, DateTime SavedAt);
public record SavedResultDto(bool Success, string? Error);
public record PinnedChangeDto(Guid RoomId, Guid MessageId, bool IsPinned, string ActorName);

// Membership Audit
public record MembershipAuditDto(Guid Id, string Action, string ActorName, string? TargetName, string? OldValue, string? NewValue, DateTime CreatedAt);

// Result wrappers
public record MessageResultDto(bool Success, MessageDto? Message, string? Error);
public record MemberResultDto(bool Success, string? Error);
public record PinnedResultDto(bool Success, string? Error, Guid? RoomId = null);
public record ReactionResultDto(bool Success, ReactionDto? Reaction, string? Error);
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public record RoomSettingsResultDto(bool Success, string? Error);

// Hub-specific DTOs
public record SendMessageHubRequest(Guid RoomId, string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, IReadOnlyList<Guid>? AttachmentIds = null);
public record DeletedDto(Guid MessageId, string Replacement);
public record RoomUpdateDto(Guid RoomId, string? Name, DateTime LastMessageAt);
