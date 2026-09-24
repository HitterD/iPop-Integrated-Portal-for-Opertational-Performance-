namespace IPop.Application.Chat;

public interface IChatService
{
    // Room management
    Task<ChatRoomDto> CreateDirectRoomAsync(Guid creatorId, Guid recipientId, CancellationToken ct);
    Task<ChatRoomDto> CreateGroupRoomAsync(Guid creatorId, CreateGroupRequest req, CancellationToken ct);
    Task<ChatRoomDto?> GetRoomAsync(Guid roomId, Guid userId, CancellationToken ct);
    Task<PagedResult<RoomListDto>> GetRoomsAsync(Guid userId, RoomTab tab, int page, int pageSize, CancellationToken ct);
    Task<ChatRoomDto?> FindExistingDmAsync(Guid userId1, Guid userId2, CancellationToken ct);

    // Messages
    Task<MessageResultDto> SendMessageAsync(Guid senderId, SendMessageRequest req, CancellationToken ct);
    Task<MessageResultDto> EditMessageAsync(Guid userId, Guid messageId, string newContent, CancellationToken ct);
    Task<MessageResultDto> DeleteMessageAsync(Guid userId, Guid messageId, bool isAdmin, CancellationToken ct);
    Task<MessageResultDto> ForwardMessageAsync(Guid userId, Guid messageId, Guid targetRoomId, CancellationToken ct);
    Task<PagedResult<MessageDto>> GetMessagesAsync(Guid roomId, Guid userId, int page, int pageSize, CancellationToken ct);

    // Threads
    Task<MessageResultDto> SendThreadReplyAsync(Guid senderId, Guid parentMessageId, SendMessageRequest req, CancellationToken ct);
    Task<PagedResult<MessageDto>> GetThreadRepliesAsync(Guid parentMessageId, Guid userId, int page, int pageSize, CancellationToken ct);
    // Removed the no-userId overload to prevent authz bypass. Callers must pass the authenticated userId.

    // Reactions
    Task<ReactionResultDto> ToggleReactionAsync(Guid userId, Guid messageId, string emoji, CancellationToken ct);

    // Read receipts
    Task RecordDeliveryAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task RecordReadAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task<IReadOnlyList<SeenByDto>> GetSeenByAsync(Guid messageId, CancellationToken ct);
    Task<SeenListDto> GetSeenDetailAsync(Guid callerId, Guid messageId, CancellationToken ct);

    // Pinned messages
    Task<PinnedResultDto> PinMessageAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task<PinnedResultDto> UnpinMessageAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task<IReadOnlyList<PinnedMessageDto>> GetPinnedMessagesAsync(Guid roomId, CancellationToken ct);

    // Saved messages (personal bookmarks — private to the saving user)
    Task<SavedResultDto> SaveMessageAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task<SavedResultDto> UnsaveMessageAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task<IReadOnlyList<SavedMessageDto>> GetSavedMessagesAsync(Guid userId, CancellationToken ct);

    // Group management
    Task<MemberResultDto> AddMemberAsync(Guid actorId, Guid roomId, Guid userId, CancellationToken ct);
    Task<MemberResultDto> RemoveMemberAsync(Guid actorId, Guid roomId, Guid userId, CancellationToken ct);
    Task<MemberResultDto> ChangeRoleAsync(Guid actorId, Guid roomId, Guid userId, ChatMemberRole newRole, CancellationToken ct);
    Task<MemberResultDto> LeaveGroupAsync(Guid userId, Guid roomId, CancellationToken ct);

    // Group identity & audit
    Task<RoomSettingsResultDto> UpdateGroupIdentityAsync(Guid actorId, Guid roomId, string name, string? description, string? avatarColor, CancellationToken ct);
    Task<PagedResult<MembershipAuditDto>> GetMembershipAuditAsync(Guid callerId, Guid roomId, int page, int pageSize, CancellationToken ct);

    // Search
    Task<PagedResult<RoomListDto>> SearchRoomsAsync(Guid userId, string query, CancellationToken ct);
    Task<PagedResult<MessageDto>> SearchMessagesAsync(Guid userId, Guid roomId, string query, int page, int pageSize, CancellationToken ct);
    /// Pencarian lintas ruang: semua ruang yang diikuti pengguna, satu query.
    Task<PagedResult<MessageDto>> SearchAllMessagesAsync(Guid userId, string query, int page, int pageSize, CancellationToken ct);
    /// Mention belum dibaca: pesan yang menyebut pengguna (@user/@all) setelah tanda
    /// baca terakhir di tiap ruang yang diikutinya.
    Task<PagedResult<MessageDto>> GetUnreadMentionsAsync(Guid userId, int page, int pageSize, CancellationToken ct);

    // Mute & Archive (return result for failure surfacing)
    Task<RoomSettingsResultDto> MuteRoomAsync(Guid userId, Guid roomId, MuteDuration duration, CancellationToken ct);
    Task<RoomSettingsResultDto> UnmuteRoomAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<RoomSettingsResultDto> ArchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<RoomSettingsResultDto> UnarchiveRoomAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<RoomSettingsResultDto> ClearRoomHistoryAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<bool> BlockUserAsync(Guid userId, Guid blockedUserId, CancellationToken ct);
    Task<bool> UnblockUserAsync(Guid userId, Guid blockedUserId, CancellationToken ct);
    Task<IReadOnlyList<Guid>> GetBlockedUserIdsAsync(Guid userId, CancellationToken ct);

    /// Toggle whether read receipts are sent for messages in this room (per-user setting).
    /// For DMs this is the reciprocal control: the other party only sees "read" if you have this enabled.
    Task<RoomSettingsResultDto> SetReadReceiptsAsync(Guid userId, Guid roomId, bool enabled, CancellationToken ct);

    // Room pin / archived list
    Task<RoomSettingsResultDto> PinRoomAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<RoomSettingsResultDto> UnpinRoomAsync(Guid userId, Guid roomId, CancellationToken ct);
    Task<PagedResult<RoomListDto>> GetArchivedRoomsAsync(Guid userId, int page, int pageSize, CancellationToken ct);

    // Emergency broadcast
    Task<MessageResultDto> SendEmergencyBroadcastAsync(Guid senderId, Guid roomId, string content, CancellationToken ct);
}
