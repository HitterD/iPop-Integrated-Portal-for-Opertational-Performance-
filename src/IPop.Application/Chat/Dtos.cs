namespace IPop.Application.Chat;

// Message DTOs
public record SendMessageRequest(Guid RoomId, string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, IReadOnlyList<Guid>? AttachmentIds = null,
    /// Sender removed the link preview card before sending (default false = preview shown).
    bool LinkPreviewOff = false);
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
    string? ForwardedFromSenderName = null,
    /// Group messages sent by the viewer: members (excluding the sender) who have read it,
    /// out of ReadTotal active members. Null outside groups and for received messages.
    int? ReadCount = null,
    int? ReadTotal = null,
    IReadOnlyList<SeenByDto>? ReadPreview = null,
    /// True when the sender removed the link preview card; clients then show no preview.
    bool LinkPreviewOff = false);
public record MessageAckDto(Guid MessageId, DateTime ServerTimestamp, Guid IdempotencyKey);
/// SendMessage's hub return value on every exit path. RoomId + IdempotencyKey echo the request so
/// the client settles exactly that message; Error is a stable code (e.g. "rate_limited").
public record SendMessageResultDto(bool Success, Guid RoomId, Guid IdempotencyKey, Guid? MessageId = null,
    DateTime? ServerTimestamp = null, string? Error = null);
public record ReplyPreviewDto(string SenderName, string Content);

// Room DTOs
public record RoomListDto(Guid Id, string Type, string? Name, string? RecipientNik,
    string? RecipientName, string? LastMessage, DateTime? LastMessageAt,
    int UnreadCount, bool IsMuted, bool IsArchived, bool IsPinned,
    string? PresenceStatus, bool HasDraft, bool HasUnreadMention, DateTime? MuteExpiresAt,
    /// For the current user: whether read receipts are enabled for this room (reciprocal for DMs).
    bool ReadReceiptsEnabled = true,
    /// Warna avatar grup pilihan admin; null = palet default dari nama.
    string? AvatarColor = null,
    /// URL foto grup (relatif ke API); null = avatar inisial.
    string? AvatarUrl = null);
public record ChatRoomDto(Guid Id, string Type, string? Name, string? Description, int MemberCount,
    int OnlineCount, IReadOnlyList<MemberDto> Members, string? CurrentUserRole, IReadOnlyList<MentionUserDto> MentionDirectory,
    /// Current user's per-room setting for read receipts (reciprocal for DMs).
    bool ReadReceiptsEnabled = true,
    /// Warna avatar grup pilihan admin; null = palet default dari nama.
    string? AvatarColor = null,
    /// URL foto grup (relatif ke API); null = avatar inisial.
    string? AvatarUrl = null);
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
/// Preview = up to 3 most recent readers, for the avatar stack under each own group bubble.
public record MessageReadCountDto(Guid MessageId, int ReadCount, int ReadTotal, IReadOnlyList<SeenByDto>? Preview = null);
public record SeenUpdateDto(Guid MessageId, SeenByDto Reader, int TotalReadCount, int TotalMemberCount);
/// Pending = active members (sender excluded) who have not read the message yet.
public record SeenListDto(IReadOnlyList<SeenByDto> Readers, int TotalReaders, int ActiveMemberCount,
    IReadOnlyList<SeenByDto>? Pending = null);

// Typing DTOs
public record TypingDto(Guid RoomId, string UserNik, string DisplayName, bool IsTyping);

// Member DTOs
public record MemberDto(Guid UserId, string Nik, string Name, string Role, string? PresenceStatus,
    /// Last heartbeat (UTC), for "Last seen …" while offline.
    DateTime? LastSeenAt = null);
public record MemberChangeDto(Guid RoomId, string Action, string ActorName, string TargetName, Guid? TargetUserId);

// Pinned DTOs
/// PinnedByUserId lets the client show Unpin only to whoever may unpin (pinner or group owner/admin).
public record PinnedMessageDto(Guid Id, Guid MessageId, string SenderName, string Content, DateTime PinnedAt, Guid? PinnedByUserId = null);
/// Simpanan pribadi per pengguna: membawa RoomId agar daftar bisa dikelompokkan per ruang.
public record SavedMessageDto(Guid Id, Guid RoomId, Guid MessageId, string SenderName, string Content, DateTime SavedAt);
public record SavedResultDto(bool Success, string? Error);
/// SystemMessage is the persisted "X pinned/unpinned a message" row, carried on the pin event
/// (not "newMessage") so clients show it live without unread counts, banners, sounds or push.
public record PinnedChangeDto(Guid RoomId, Guid MessageId, bool IsPinned, string ActorName, MessageDto? SystemMessage = null);

// Membership Audit
public record MembershipAuditDto(Guid Id, string Action, string ActorName, string? TargetName, string? OldValue, string? NewValue, DateTime CreatedAt);

// Result wrappers
/// Replayed = the idempotency key matched a message that was already saved (client retry):
/// return it to the caller, but never fan it out or notify again.
public record MessageResultDto(bool Success, MessageDto? Message, string? Error, bool Replayed = false);
public record MemberResultDto(bool Success, string? Error);
/// Changed is false for a no-op (already pinned / already unpinned, or a concurrent unpin won the
/// race); only a real change carries a SystemMessage and is broadcast.
public record PinnedResultDto(bool Success, string? Error, Guid? RoomId = null, bool Changed = false, MessageDto? SystemMessage = null);
public record ReactionResultDto(bool Success, ReactionDto? Reaction, string? Error);
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public record RoomSettingsResultDto(bool Success, string? Error);
/// A group deleted for everyone: who to tell, and the stored files nothing else references any more.
public record DeleteRoomResultDto(bool Success, string? Error, IReadOnlyList<Guid> MemberIds, IReadOnlyList<StoredChatFile> OrphanFiles);
public record StoredChatFile(string Server, string Path);

// Hub-specific DTOs
public record SendMessageHubRequest(Guid RoomId, string Content, Guid IdempotencyKey, Guid? ReplyToId, string? Type, IReadOnlyList<Guid>? AttachmentIds = null,
    bool LinkPreviewOff = false);
public record DeletedDto(Guid MessageId, string Replacement);
public record RoomUpdateDto(Guid RoomId, string? Name, DateTime LastMessageAt);
