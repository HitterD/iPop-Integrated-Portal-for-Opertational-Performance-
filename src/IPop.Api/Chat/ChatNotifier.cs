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

    /// True when the first send of this message never reached its fan-out (the process stopped or the connection
    /// broke right after the save): no push intent exists for it. A replayed send then delivers it after all.
    public async Task<bool> FanOutMissingAsync(Guid messageId) =>
        push is not null && !await db.PushJobs.AnyAsync(j => j.Kind == "chat" && j.RefId == messageId, CancellationToken.None);

    public async Task NotifyNewMessageAsync(Guid roomId, MessageDto message, CancellationToken ct)
    {
        // Windows toast for the other members: a durable intent (message reference only), written
        // before the realtime fan-out; recipients, preferences and preview are resolved by the push worker.
        // The message is already committed: an enqueue failure must not turn a sent message into a
        // client-side "failed" (and a duplicate resend), so it is logged instead of thrown.
        // The caller's token is deliberately ignored: once the message is saved, the sender closing
        // the tab or dropping the request must not cancel the toast intent or the room fan-out.
        if (push is not null)
        {
            // A replayed send (client retry) calls this again: an intent that already exists means the first fan-out ran.
            try
            {
                if (!await db.PushJobs.AnyAsync(j => j.Kind == "chat" && j.RefId == message.Id, CancellationToken.None))
                    await push.EnqueueAsync(db, "chat", message.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                log?.LogError("Chat push intent for message {MessageId} was not saved ({Error}).", message.Id, ex.GetType().Name);
            }
        }
        // Thread replies have their own event: clients drop newMessage carrying a ThreadParentId.
        var evt = message.ThreadParentId is null ? "newMessage" : "threadReply";
        // A backplane (Redis) hiccup must not report a saved message as failed either; clients
        // that missed the fan-out catch up on reconnect/refresh.
        try { await hub.Clients.Group(Group(roomId)).SendAsync(evt, message, CancellationToken.None); }
        catch (Exception ex)
        {
            log?.LogError("Realtime fan-out of message {MessageId} failed ({Error}).", message.Id, ex.GetType().Name);
        }
    }

    public Task NotifyMessageEditedAsync(Guid roomId, MessageDto message, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("messageEdited", message, ct);

    public Task NotifyMessageDeletedAsync(Guid roomId, Guid messageId, string replacement, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("messageDeleted", new DeletedDto(messageId, replacement), ct);

    public Task NotifyReactionAsync(Guid roomId, Guid messageId, ReactionDto reaction, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("reactionChanged", reaction, ct);

    public async Task NotifySeenUpdateAsync(Guid roomId, Guid messageId, SeenByDto reader, CancellationToken ct)
    {
        // The sender is never one of their own message's readers (legacy rows may say otherwise).
        var senderId = await db.ChatMessages.Where(m => m.Id == messageId).Select(m => m.SenderId).FirstOrDefaultAsync(ct);
        var totalRead = await db.ChatReadReceipts.CountAsync(r => r.MessageId == messageId && r.ReadAt != null && r.UserId != senderId, ct);
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

    public async Task NotifyRemovedAsync(Guid removedUserId, Guid roomId, CancellationToken ct)
    {
        // Unsubscribe server-side first: a removed member must stop receiving the room's live events
        // whether or not their client honours "removedFromRoom".
        await Task.WhenAll(ChatConnections.Of(removedUserId)
            .Select(c => hub.Groups.RemoveFromGroupAsync(c, Group(roomId), ct)));
        await hub.Clients.User(removedUserId.ToString()).SendAsync("removedFromRoom", roomId, ct);
    }

    public Task NotifyAddedAsync(IEnumerable<Guid> userIds, Guid roomId, CancellationToken ct)
        => hub.Clients.Users(userIds.Select(u => u.ToString()).ToList()).SendAsync("addedToRoom", roomId, ct);

    public Task NotifyRoomUpdatedAsync(Guid roomId, RoomUpdateDto update, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("roomUpdated", update, ct);

    public Task NotifyThreadReplyAsync(Guid roomId, MessageDto reply, CancellationToken ct)
        => hub.Clients.Group(Group(roomId)).SendAsync("threadReply", reply, ct);
}
