using IPop.Application.Chat;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Chat;

/// <summary>
/// Bridges IChatNotifier to SignalR IHubContext&lt;ChatHub&gt; — same pattern as PresenceBroadcaster.
/// All notifications route through SignalR groups (pattern: "chat:{roomId}") or user connections.
/// Cross-node delivery is handled by the Redis backplane configured on the SignalR pipeline.
/// </summary>
public sealed class ChatNotifier(IHubContext<ChatHub> hub, IPopDbContext db,
    IPop.Infrastructure.Notifications.Push.PushQueue? push = null, ILogger<ChatNotifier>? log = null) : IChatNotifier
{
    private static string Group(Guid roomId) => $"chat:{roomId}";

    public async Task NotifyNewMessageAsync(Guid roomId, MessageDto message, CancellationToken ct)
    {
        // Windows toast for the other members: a durable intent (message reference only), written
        // before the realtime fan-out; recipients, preferences and preview are resolved by the push worker.
        // The message is already committed: an enqueue failure must not turn a sent message into a
        // client-side "failed" (and a duplicate resend), so it is logged instead of thrown.
        if (push is not null)
        {
            try { await push.EnqueueAsync(db, "chat", message.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogError("Chat push intent for message {MessageId} was not saved ({Error}).", message.Id, ex.GetType().Name);
            }
        }
        await hub.Clients.Group(Group(roomId)).SendAsync("newMessage", message, ct);
    }

    public Task NotifyMessageEditedAsync(Guid roomId, MessageDto message, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("messageEdited", message, ct);

    public Task NotifyMessageDeletedAsync(Guid roomId, Guid messageId, string replacement, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("messageDeleted", new DeletedDto(messageId, replacement), ct);

    public Task NotifyReactionAsync(Guid roomId, Guid messageId, ReactionDto reaction, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("reactionChanged", reaction, ct);

    public async Task NotifySeenUpdateAsync(Guid roomId, Guid messageId, SeenByDto reader, CancellationToken ct)
    {
        var totalRead = await db.ChatReadReceipts.CountAsync(r => r.MessageId == messageId && r.ReadAt != null, ct);
        var totalMembers = await db.ChatRoomMembers.CountAsync(m => m.RoomId == roomId && m.LeftAt == null, ct);
        var dto = new SeenUpdateDto(messageId, reader, totalRead, totalMembers);
        await hub.Clients.Group(Group(roomId)).SendAsync("seenUpdate", dto, ct);
    }

    public Task NotifyTypingAsync(Guid roomId, string userNik, string displayName, bool isTyping, string? excludingConnectionId, CancellationToken ct)
    {
        // Exclude the typing caller so their own name never echoes back — "is typing" is only for others.
        var clients = excludingConnectionId is null
            ? hub.Clients.Group(Group(roomId))
            : hub.Clients.GroupExcept(Group(roomId), excludingConnectionId);
        return clients.SendAsync(
            isTyping ? "typingStarted" : "typingStopped",
            new TypingDto(roomId, userNik, displayName, isTyping),
            ct);
    }

    public Task NotifyMemberChangeAsync(Guid roomId, MemberChangeDto change, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("memberChanged", change, ct);

    public Task NotifyPinnedChangeAsync(Guid roomId, PinnedChangeDto change, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("pinnedChanged", change, ct);

    public Task NotifyDeliveryAsync(Guid userId, Guid messageId, CancellationToken ct)
        => hub.Clients.User(userId.ToString()).SendAsync("deliveryUpdate", messageId, ct);

    public Task NotifyRemovedAsync(Guid removedUserId, Guid roomId, CancellationToken ct)
        => hub.Clients.User(removedUserId.ToString()).SendAsync("removedFromRoom", roomId, ct);

    public Task NotifyAddedAsync(IEnumerable<Guid> userIds, Guid roomId, CancellationToken ct)
        => hub.Clients.Users(userIds.Select(u => u.ToString()).ToList()).SendAsync("addedToRoom", roomId, ct);

    public Task NotifyRoomUpdatedAsync(Guid roomId, RoomUpdateDto update, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("roomUpdated", update, ct);

    public Task NotifyThreadReplyAsync(Guid roomId, MessageDto reply, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("threadReply", reply, ct);
}
