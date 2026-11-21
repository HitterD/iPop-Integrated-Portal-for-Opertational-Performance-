using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notifications;

/// Chat history on /notifications and in the iPop Notifier Inbox: ONE row per conversation per person
/// ("Ursula Rahmawati · 3 new messages"), updated as messages arrive and marked read once the chat is read.
/// The toast itself still comes from the chat push job (desktopToast / Web Push); this row is only the history,
/// so it is never broadcast as "notificationReceived" (that would pop a second popup) — only "notificationsChanged".
public static class ChatHistoryNotifications
{
    public const string EventType = "chat.message";
    public const string ClientSlug = "chat";

    public static string KeyOf(Guid roomId, Guid userId) => $"chat-room-{roomId:N}-{userId:N}";

    /// Title for a conversation row: the name, plus how many are new when more than one.
    public static string TitleOf(string name, int count) => count > 1 ? $"{name} · {count} new messages" : name;

    /// Writes/refreshes the rows for the people who should get this message in their history. Returns the users whose
    /// row changed. People who already read the room (count of unread from others = 0) are skipped.
    public static async Task<List<Guid>> UpsertAsync(IPopDbContext db, Guid roomId, Guid senderId, string name, string preview,
        IReadOnlyCollection<Guid> recipients, DateTime at, CancellationToken ct)
    {
        var changed = new List<Guid>();
        if (recipients.Count == 0) return changed;
        var client = await EnsureClientAsync(db, ct);
        var url = $"/chat/{roomId}";
        var title200 = name.Length > 150 ? name[..150] : name;
        var body = preview.Length > 500 ? preview[..500] : preview;

        // How far each recipient has read: unread = messages from others after that point.
        var lastRead = await db.ChatRoomUserSettings.AsNoTracking()
            .Where(s => s.RoomId == roomId && recipients.Contains(s.UserId))
            .Select(s => new { s.UserId, s.LastReadMessageAt }).ToDictionaryAsync(s => s.UserId, s => s.LastReadMessageAt, ct);

        foreach (var uid in recipients)
        {
            var since = lastRead.GetValueOrDefault(uid);
            var count = await db.ChatMessages.AsNoTracking().CountAsync(m => m.RoomId == roomId && m.SenderId != uid
                && m.ThreadParentId == null && !m.IsDeleted && m.Type != "system" && (since == null || m.CreatedAt > since), ct);
            if (count == 0) continue;   // already read (the chat was open)
            var key = KeyOf(roomId, uid);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var req = await db.NotificationRequests.FirstOrDefaultAsync(r => r.ClientId == client.Id && r.IdempotencyKey == key, ct);
                    var now = DateTime.UtcNow;
                    if (req is null)
                    {
                        req = new NotificationRequest
                        {
                            Id = Guid.NewGuid(), ClientId = client.Id, IdempotencyKey = key, Priority = "normal", EventType = EventType,
                            Title = TitleOf(title200, count), Body = body, TargetUrl = url, ActorUserId = senderId,
                            Status = NotificationStatus.Delivered, AcceptedAt = now, DeliveredAt = now, CreatedAt = now, UpdatedAt = now,
                        };
                        db.NotificationRequests.Add(req);
                    }
                    else
                    {
                        req.Title = TitleOf(title200, count);
                        req.Body = body;
                        req.ActorUserId = senderId;
                        req.DeliveredAt = now;
                        req.UpdatedAt = now;
                    }
                    var d = await db.NotificationDeliveries.FirstOrDefaultAsync(x => x.RequestId == req.Id && x.UserId == uid, ct);
                    if (d is null)
                    {
                        db.NotificationDeliveries.Add(new NotificationDelivery
                        {
                            Id = Guid.NewGuid(), RequestId = req.Id, UserId = uid, Status = NotificationStatus.Delivered, Channel = "signalr",
                            AttemptCount = 1, DeliveredAt = at, CreatedAt = now, UpdatedAt = now,
                        });
                    }
                    else
                    {
                        // A new message brings a read (or deleted-and-back) conversation back to the top, unread.
                        d.Status = NotificationStatus.Delivered;
                        d.ReadAt = null;
                        d.AcknowledgedAt = null;
                        d.DeliveredAt = at;
                        d.UpdatedAt = now;
                    }
                    await db.SaveChangesAsync(ct);
                    changed.Add(uid);
                    break;
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    // Another worker created the row at the same moment: retry as an update.
                    DetachRows(db);
                }
            }
        }
        return changed;
    }

    /// After a read receipt: when nothing from others is unread in the room any more, the room's row becomes read.
    public static async Task<bool> SyncAfterReadAsync(IPopDbContext db, Guid userId, Guid roomId, CancellationToken ct)
    {
        var since = await db.ChatRoomUserSettings.AsNoTracking().Where(s => s.UserId == userId && s.RoomId == roomId)
            .Select(s => s.LastReadMessageAt).FirstOrDefaultAsync(ct);
        var unread = await db.ChatMessages.AsNoTracking().AnyAsync(m => m.RoomId == roomId && m.SenderId != userId
            && m.ThreadParentId == null && !m.IsDeleted && m.Type != "system" && (since == null || m.CreatedAt > since), ct);
        return !unread && await MarkRoomReadAsync(db, userId, roomId, ct);
    }

    /// The person read the conversation: their row (if unread) becomes read. True when something changed.
    public static async Task<bool> MarkRoomReadAsync(IPopDbContext db, Guid userId, Guid roomId, CancellationToken ct)
    {
        var key = KeyOf(roomId, userId);
        var d = await db.NotificationDeliveries
            .Where(x => x.UserId == userId && x.Request.IdempotencyKey == key && x.Request.EventType == EventType
                && x.Status != NotificationStatus.Read && x.Status != NotificationStatus.Acknowledged)
            .FirstOrDefaultAsync(ct);
        if (d is null) return false;
        var now = DateTime.UtcNow;
        d.Status = NotificationStatus.Read;
        d.ReadAt = now;
        d.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// Drops only this helper's rows from the change tracker: the caller (the push worker) still tracks its job.
    public static void DetachRows(IPopDbContext db)
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is NotificationRequest or NotificationDelivery or NotificationGatewayClient).ToList())
            e.State = EntityState.Detached;
    }

    private static async Task<NotificationGatewayClient> EnsureClientAsync(IPopDbContext db, CancellationToken ct)
    {
        var client = await db.NotificationGatewayClients.FirstOrDefaultAsync(c => c.ClientId == ClientSlug, ct);
        if (client is not null) return client;
        client = new NotificationGatewayClient
        {
            Id = Guid.NewGuid(), ClientId = ClientSlug, SecretHash = "internal-no-secret", AllowedEventTypes = "chat.mention,chat.emergency",
            RateLimitPerSecond = 0, MaxRecipients = 10000, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.NotificationGatewayClients.Add(client);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            DetachRows(db);
            client = await db.NotificationGatewayClients.FirstAsync(c => c.ClientId == ClientSlug, ct);
        }
        return client;
    }
}
