using System.Collections.Concurrent;
using IPop.Application.Chat;
using IPop.Application.Rbac;
using IPop.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Chat;

/// <summary>
/// Chat real-time hub. Group name pattern: "chat:{roomId}".
/// Handles messaging, typing indicators, delivery/read receipts.
/// </summary>
public class ChatHub(IChatService chatService, IChatNotifier notifier, IPopDbContext db, IPermissionEvaluator perms) : Hub
{
    // In-memory sliding-window rate limiter: userId → (count, windowStart)
    private static readonly ConcurrentDictionary<string, (int Count, DateTime WindowStart)> RateLimits = new();

    // Typing state: roomId → { userNik → lastTypingBroadcast }
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, DateTime>> TypingState = new();

    // ──────────────────────────────────────────────────────────────────────────
    // Connection lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId is null) { Context.Abort(); return; }

        // Auto-join all rooms the user is currently a member of
        var roomIds = await db.ChatRoomMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId.Value && m.LeftAt == null)
            .Select(m => m.RoomId)
            .ToListAsync(Context.ConnectionAborted);

        await Task.WhenAll(roomIds.Select(roomId =>
            Groups.AddToGroupAsync(Context.ConnectionId, $"chat:{roomId}", Context.ConnectionAborted)));

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR handles group cleanup automatically on disconnect
        await base.OnDisconnectedAsync(exception);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Room subscription (JoinRoom / LeaveRoom)
    // ──────────────────────────────────────────────────────────────────────────

    public async Task JoinRoom(Guid roomId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Verify membership before subscribing
        var isMember = await db.ChatRoomMembers
            .AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId.Value && m.LeftAt == null, Context.ConnectionAborted);

        if (!isMember) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"chat:{roomId}", Context.ConnectionAborted);
    }

    public Task LeaveRoom(Guid roomId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"chat:{roomId}", Context.ConnectionAborted);

    // ──────────────────────────────────────────────────────────────────────────
    // Messaging (Task 8.2)
    // ──────────────────────────────────────────────────────────────────────────

    public async Task SendMessage(SendMessageHubRequest request)
    {
        var userId = GetUserId();
        var nik = GetNik();
        if (userId is null || nik is null) return;
        if (!await perms.IsAllowedAsync(userId.Value, "chat.send", null, Context.ConnectionAborted))
        {
            await Clients.Caller.SendAsync("error", new { code = "forbidden" });
            return;
        }

        // Rate limit: 30 messages per 60-second sliding window (Req 30.6)
        if (!CheckRateLimit(nik))
        {
            await Clients.Caller.SendAsync("error", new { code = "rate_limited", message = "30 messages per minute exceeded." });
            return;
        }

        if (request.Content is null)
        {
            await Clients.Caller.SendAsync("error", new { code = "content_required" });
            return;
        }
        if (request.Content.Length > 4000)
        {
            await Clients.Caller.SendAsync("error", new { code = "payload_too_large", message = "Message exceeds 4000 characters." });
            return;
        }

        var req = new SendMessageRequest(request.RoomId, request.Content, request.IdempotencyKey, request.ReplyToId, request.Type, request.AttachmentIds);
        var result = await chatService.SendMessageAsync(userId.Value, req, Context.ConnectionAborted);

        if (!result.Success)
        {
            await Clients.Caller.SendAsync("error", new { code = result.Error });
            return;
        }

        // ACK to sender with server timestamp
        var ack = new MessageAckDto(result.Message!.Id, result.Message.CreatedAt, request.IdempotencyKey);
        await Clients.Caller.SendAsync("messageAck", ack);

        // Broadcast to room group via IChatNotifier (which routes through IHubContext)
        await notifier.NotifyNewMessageAsync(request.RoomId, result.Message, Context.ConnectionAborted);
    }

    public async Task EditMessage(Guid messageId, string newContent)
    {
        var userId = GetUserId();
        if (userId is null) return;
        if (!await perms.IsAllowedAsync(userId.Value, "chat.send", null, Context.ConnectionAborted))
        {
            await Clients.Caller.SendAsync("error", new { code = "forbidden" });
            return;
        }
        if (newContent is null)
        {
            await Clients.Caller.SendAsync("error", new { code = "content_required" });
            return;
        }

        var result = await chatService.EditMessageAsync(userId.Value, messageId, newContent, Context.ConnectionAborted);
        if (!result.Success)
        {
            await Clients.Caller.SendAsync("error", new { code = result.Error });
            return;
        }

        // Broadcast edit to room group
        await notifier.NotifyMessageEditedAsync(result.Message!.RoomId, result.Message, Context.ConnectionAborted);
    }

    public async Task DeleteMessage(Guid messageId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        // Look up message to determine room and actor's role
        var message = await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, Context.ConnectionAborted);
        if (message is null)
        {
            await Clients.Caller.SendAsync("error", new { code = "message_not_found" });
            return;
        }

        var memberRole = await db.ChatRoomMembers.AsNoTracking()
            .Where(m => m.RoomId == message.RoomId && m.UserId == userId.Value && m.LeftAt == null)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        if (memberRole is null)
        {
            await Clients.Caller.SendAsync("error", new { code = "forbidden" });
            return;
        }

        var isAdmin = memberRole is "owner" or "admin";

        var result = await chatService.DeleteMessageAsync(userId.Value, messageId, isAdmin, Context.ConnectionAborted);
        if (!result.Success)
        {
            await Clients.Caller.SendAsync("error", new { code = result.Error });
            return;
        }

        var replacement = result.Message!.DeletedByType == "admin"
            ? "This message was deleted by an admin"
            : "This message was deleted";

        await notifier.NotifyMessageDeletedAsync(result.Message.RoomId, messageId, replacement, Context.ConnectionAborted);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Status / Receipts (Task 8.2)
    // ──────────────────────────────────────────────────────────────────────────

    public async Task DeliveryConfirm(Guid messageId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        await chatService.RecordDeliveryAsync(userId.Value, messageId, Context.ConnectionAborted);

        // Notify original sender of delivery
        var senderId = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => m.SenderId)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        if (senderId != Guid.Empty)
            await notifier.NotifyDeliveryAsync(senderId, messageId, Context.ConnectionAborted);
    }

    public Task MarkDelivered(Guid messageId) => DeliveryConfirm(messageId);

    public async Task MarkRead(Guid messageId)
    {
        var userId = GetUserId();
        if (userId is null) return;

        await chatService.RecordReadAsync(userId.Value, messageId, Context.ConnectionAborted);

        // Build reader info for seen update notification
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, Context.ConnectionAborted);
        var roomId = await db.ChatMessages.AsNoTracking()
            .Where(m => m.Id == messageId)
            .Select(m => m.RoomId)
            .FirstOrDefaultAsync(Context.ConnectionAborted);

        if (user is not null && roomId != Guid.Empty)
        {
            var receipt = await db.ChatReadReceipts.AsNoTracking()
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId.Value, Context.ConnectionAborted);
            var reader = new SeenByDto(userId.Value, user.Name, user.PhotoPath, receipt?.DeliveredAt, receipt?.ReadAt);
            await notifier.NotifySeenUpdateAsync(roomId, messageId, reader, Context.ConnectionAborted);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Typing indicators (Task 8.3)
    // ──────────────────────────────────────────────────────────────────────────

    public async Task StartTyping(Guid roomId)
    {
        var userId = GetUserId();
        var nik = GetNik();
        if (userId is null || nik is null) return;
        if (!await IsMemberAsync(userId.Value, roomId)) return;

        // Debounce: max one broadcast per 3s per user per room (Req 21.3)
        var roomTyping = TypingState.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, DateTime>());
        var now = DateTime.UtcNow;

        // Opportunistic eviction: drop stale typing entries >60s
        foreach (var kv in roomTyping)
            if ((now - kv.Value).TotalSeconds > 60)
                roomTyping.TryRemove(kv.Key, out _);

        if (roomTyping.TryGetValue(nik, out var lastBroadcast) && (now - lastBroadcast).TotalSeconds < 3)
            return;

        roomTyping[nik] = now;

        var displayName = await GetDisplayNameAsync(nik);
        await notifier.NotifyTypingAsync(roomId, nik, displayName, true, Context.ConnectionId, Context.ConnectionAborted);
    }

    public async Task StopTyping(Guid roomId)
    {
        var userId = GetUserId();
        var nik = GetNik();
        if (userId is null || nik is null) return;
        if (!await IsMemberAsync(userId.Value, roomId)) return;

        if (TypingState.TryGetValue(roomId, out var roomTyping))
        {
            roomTyping.TryRemove(nik, out _);
            if (roomTyping.IsEmpty)
                TypingState.TryRemove(roomId, out _);
        }

        var displayName = await GetDisplayNameAsync(nik);
        await notifier.NotifyTypingAsync(roomId, nik, displayName, false, Context.ConnectionId, Context.ConnectionAborted);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private Task<bool> IsMemberAsync(Guid userId, Guid roomId) =>
        db.ChatRoomMembers.AsNoTracking()
            .AnyAsync(m => m.RoomId == roomId && m.UserId == userId && m.LeftAt == null, Context.ConnectionAborted);

    private Guid? GetUserId()
    {
        var sub = Context.User?.FindFirst("sub")?.Value
            ?? Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    private string? GetNik() => Context.User?.FindFirst("nik")?.Value;

    private async Task<string> GetDisplayNameAsync(string nik)
    {
        var name = await db.Users.AsNoTracking()
            .Where(u => u.Nik == nik)
            .Select(u => u.Name)
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        return name ?? nik;
    }

    /// <summary>
    /// Sliding-window rate limiter: 30 messages per 60-second window per user.
    /// Atomic via AddOrUpdate + opportunistic eviction of stale entries.
    /// </summary>
    private static bool CheckRateLimit(string nik)
    {
        var now = DateTime.UtcNow;
        var allowed = true;

        RateLimits.AddOrUpdate(nik,
            _ => (1, now),
            (_, prev) =>
            {
                if ((now - prev.WindowStart).TotalSeconds > 60)
                    return (1, now);
                if (prev.Count >= 30) { allowed = false; return prev; }
                return (prev.Count + 1, prev.WindowStart);
            });

        // Opportunistic eviction: if this check created contention, piggyback cleanup
        // of entries idle >10 min (cheap — only when dictionary is large)
        if (RateLimits.Count > 500)
        {
            foreach (var kv in RateLimits)
                if ((now - kv.Value.WindowStart).TotalMinutes > 10)
                    RateLimits.TryRemove(kv.Key, out _);
        }

        return allowed;
    }
}

