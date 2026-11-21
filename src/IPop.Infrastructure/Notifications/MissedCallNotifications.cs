using IPop.Application.Calls;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notifications;

/// "Missed call from Dia Kartika" on /notifications and in the Notifier Inbox: one row per missed call per person,
/// written when the call's history says "missed" (any end path calls <see cref="SyncAsync"/>; the key makes repeats no-ops).
/// The ring itself was the alert, so the row is only history (notificationsChanged, never notificationReceived).
public static class MissedCallNotifications
{
    public const string EventType = "call.missed";
    public const string ClientSlug = "calls";

    public static string KeyOf(Guid callId, Guid userId) => $"call-missed-{callId:N}-{userId:N}";

    /// Returns the users who got a new row.
    public static async Task<List<Guid>> SyncAsync(IPopDbContext db, CallRoomDto room, CancellationToken ct)
    {
        var added = new List<Guid>();
        if (room.Status is not (CallStatus.Missed or CallStatus.Ended or CallStatus.Declined)) return added;
        var missed = await db.CallHistories.AsNoTracking()
            .Where(h => h.CallId == room.Id && h.Direction == "incoming" && h.Result == "missed")
            .Select(h => h.PerformerId).Distinct().ToListAsync(ct);
        if (missed.Count == 0) return added;

        var client = await db.NotificationGatewayClients.FirstOrDefaultAsync(c => c.ClientId == ClientSlug, ct);
        if (client is null)
        {
            client = new NotificationGatewayClient
            {
                Id = Guid.NewGuid(), ClientId = ClientSlug, SecretHash = "internal-no-secret", AllowedEventTypes = EventType,
                RateLimitPerSecond = 0, MaxRecipients = 10000, IsActive = true, CreatedAt = DateTime.UtcNow,
            };
            db.NotificationGatewayClients.Add(client);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                ChatHistoryNotifications.DetachRows(db);
                client = await db.NotificationGatewayClients.FirstAsync(c => c.ClientId == ClientSlug, ct);
            }
        }

        var caller = room.Participants.FirstOrDefault(p => p.UserId == room.CreatorId)?.Name ?? "someone";
        var group = CallKind.IsLiveKit(room.Kind) && !string.IsNullOrWhiteSpace(room.Title);
        foreach (var uid in missed)
        {
            var key = KeyOf(room.Id, uid);
            if (await db.NotificationRequests.AsNoTracking().AnyAsync(r => r.ClientId == client.Id && r.IdempotencyKey == key, ct)) continue;
            var now = DateTime.UtcNow;
            var req = new NotificationRequest
            {
                Id = Guid.NewGuid(), ClientId = client.Id, IdempotencyKey = key, Priority = "normal", EventType = EventType,
                Title = group ? $"Missed group call · {room.Title}" : $"Missed call from {caller}",
                Body = group ? $"{caller} called the group." : "Voice call",
                TargetUrl = "/calls?tab=recents", ActorUserId = room.CreatorId,
                Status = NotificationStatus.Delivered, AcceptedAt = now, DeliveredAt = now, CreatedAt = now, UpdatedAt = now,
            };
            db.NotificationRequests.Add(req);
            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(), RequestId = req.Id, UserId = uid, Status = NotificationStatus.Delivered, Channel = "signalr",
                AttemptCount = 1, DeliveredAt = room.EndedAt ?? now, CreatedAt = now, UpdatedAt = now,
            });
            try { await db.SaveChangesAsync(ct); added.Add(uid); }
            catch (DbUpdateException) { ChatHistoryNotifications.DetachRows(db); }
        }
        return added;
    }

    /// Recents was opened (missed calls seen): the matching rows are read too. Null callId = every missed call.
    public static async Task<int> MarkSeenAsync(IPopDbContext db, Guid userId, Guid? callId, CancellationToken ct)
    {
        var q = db.NotificationDeliveries.Where(d => d.UserId == userId && d.Request.EventType == EventType
            && d.Status != NotificationStatus.Read && d.Status != NotificationStatus.Acknowledged);
        if (callId is { } id)
        {
            var key = KeyOf(id, userId);
            q = q.Where(d => d.Request.IdempotencyKey == key);
        }
        var rows = await q.ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var d in rows) { d.Status = NotificationStatus.Read; d.ReadAt = now; d.UpdatedAt = now; }
        if (rows.Count > 0) await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
