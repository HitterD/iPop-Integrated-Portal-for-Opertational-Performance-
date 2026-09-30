namespace IPop.Application.Chat;

public interface IChatNotifier
{
    Task NotifyNewMessageAsync(Guid roomId, MessageDto message, CancellationToken ct);
    Task NotifyMessageEditedAsync(Guid roomId, MessageDto message, CancellationToken ct);
    Task NotifyMessageDeletedAsync(Guid roomId, Guid messageId, string replacement, CancellationToken ct);
    Task NotifyReactionAsync(Guid roomId, Guid messageId, ReactionDto reaction, CancellationToken ct);
    Task NotifySeenUpdateAsync(Guid roomId, Guid messageId, SeenByDto reader, CancellationToken ct);
    Task NotifyTypingAsync(Guid roomId, string userNik, string displayName, bool isTyping, string? excludingConnectionId, CancellationToken ct);
    Task NotifyMemberChangeAsync(Guid roomId, MemberChangeDto change, CancellationToken ct);
    Task NotifyPinnedChangeAsync(Guid roomId, PinnedChangeDto change, CancellationToken ct);
    Task NotifyDeliveryAsync(Guid userId, Guid messageId, CancellationToken ct);
    Task NotifyRemovedAsync(Guid removedUserId, Guid roomId, CancellationToken ct);
    /// Tells newly added members' live connections to subscribe to the room, so its
    /// messages reach them before their next reconnect.
    Task NotifyAddedAsync(IEnumerable<Guid> userIds, Guid roomId, CancellationToken ct);
    Task NotifyRoomUpdatedAsync(Guid roomId, RoomUpdateDto update, CancellationToken ct);
    Task NotifyThreadReplyAsync(Guid roomId, MessageDto reply, CancellationToken ct);
}
