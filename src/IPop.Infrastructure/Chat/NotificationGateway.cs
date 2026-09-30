using IPop.Application.Chat;
using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IPop.Infrastructure.Chat;

/// <summary>
/// Chat mention → Intelligent Notification Gateway bridge.
/// Writes a NotificationRequest + Delivery row (EventType "chat.mention") so the
/// admin console, DLQ, and per-user inbox see it. Also broadcasts via SignalR
/// for the live badge/inbox update.
/// </summary>
public sealed class NotificationGateway(
    ILogger<NotificationGateway> logger,
    IPopDbContext db,
    INotificationRealtimeBroadcaster broadcaster,
    IPop.Infrastructure.Notifications.Push.PushQueue? push = null) : INotificationGateway
{
    private const string ChatClientId = "chat"; // well-known internal client for chat-originated notifications

    public async Task SendMentionNotificationAsync(Guid recipientId, string senderName, string roomName, string preview, bool isHighPriority, CancellationToken ct, Guid? messageId = null)
    {
        logger.LogInformation("Notification: Mention for {RecipientId} from {Sender} in {Room}: {Preview}", recipientId, senderName, roomName, preview);

        var now = DateTime.UtcNow;

        // Ensure a lightweight "chat" gateway client row exists for attribution in admin views.
        var client = await db.NotificationGatewayClients.FirstOrDefaultAsync(c => c.ClientId == ChatClientId, ct);
        if (client is null)
        {
            client = new NotificationGatewayClient
            {
                Id = Guid.NewGuid(),
                ClientId = ChatClientId,
                SecretHash = "internal-no-secret", // not used for auth; internal path
                AllowedIps = null,
                AllowedEventTypes = "chat.mention,chat.emergency",
                RateLimitPerSecond = 0,
                MaxRecipients = 10000,
                IsActive = true,
                CreatedAt = now
            };
            db.NotificationGatewayClients.Add(client);
            await db.SaveChangesAsync(ct);
        }

        var room = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roomName, ct);
        var targetUrl = room != null ? $"/chat/{room.Id}" : "/chat";
        // ?jump= makes the chat page scroll to the message itself, not just open the room.
        if (room != null && messageId is { } mid) targetUrl += $"?jump={mid}";

        var request = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            IdempotencyKey = $"chat-mention-{Guid.NewGuid():N}",
            Priority = isHighPriority ? "high" : "normal",
            EventType = "chat.mention",
            Title = $"Mention in {roomName}",
            Body = preview,
            TargetUrl = targetUrl,
            Status = "delivered",
            AcceptedAt = now,
            DeliveredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.NotificationRequests.Add(request);

        var delivery = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            UserId = recipientId,
            Status = "delivered",
            Channel = "signalr",
            AttemptCount = 1,
            DeliveredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.NotificationDeliveries.Add(delivery);

        await db.SaveChangesAsync(ct);

        // Push to the recipient's live connections so badge + inbox react immediately.
        try
        {
            var state = new NotificationDeliveryState(
                request.Id,
                recipientId,
                delivery.Status,
                delivery.Channel,
                delivery.AttemptCount,
                delivery.DeliveredAt,
                delivery.ReadAt,
                delivery.AcknowledgedAt,
                request.Title,
                request.Body,
                request.EventType,
                request.Priority,
                targetUrl);

            await broadcaster.BroadcastAsync(new[] { state }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to broadcast mention notification (non-fatal)");
        }

        // Windows toast. Room mute/archive was checked by the caller; quiet hours and
        // a muted "chat" type still apply unless the mention is high priority and allowed.
        if (push is not null)
        {
            var (priority, body, requestId) = (request.Priority, request.Body, request.Id);
            var title = $"{senderName} mentioned you · {roomName}";
            var silenced = await IPop.Infrastructure.Notifications.Push.PushDispatcher.SilencedAsync(
                db, [recipientId], "chat.mention", priority, now, ct);
            if (silenced.Count == 0)
                push.Enqueue((p, token) => p.NotifyRequestAsync([recipientId], "chat.mention", priority, title, body, targetUrl, requestId, token));
        }
    }

    public async Task SendEmergencyNotificationAsync(IReadOnlyList<Guid> recipientIds, string senderName, string roomName, string content, CancellationToken ct)
    {
        logger.LogWarning("EMERGENCY Notification: {Sender} in {Room} to {Count} recipients: {Content}", senderName, roomName, recipientIds.Count, content);

        var now = DateTime.UtcNow;

        var client = await db.NotificationGatewayClients.FirstOrDefaultAsync(c => c.ClientId == ChatClientId, ct);
        if (client is null)
        {
            client = new NotificationGatewayClient
            {
                Id = Guid.NewGuid(),
                ClientId = ChatClientId,
                SecretHash = "internal-no-secret",
                AllowedIps = null,
                AllowedEventTypes = "chat.mention,chat.emergency",
                RateLimitPerSecond = 0,
                MaxRecipients = 10000,
                IsActive = true,
                CreatedAt = now
            };
            db.NotificationGatewayClients.Add(client);
            await db.SaveChangesAsync(ct);
        }

        var room = await db.ChatRooms.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roomName, ct);
        var targetUrl = room != null ? $"/chat/{room.Id}" : "/chat";

        var request = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            IdempotencyKey = $"chat-emergency-{Guid.NewGuid():N}",
            Priority = "high",
            EventType = "chat.emergency",
            Title = $"EMERGENCY in {roomName}",
            Body = content,
            TargetUrl = targetUrl,
            Status = "delivered",
            AcceptedAt = now,
            DeliveredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.NotificationRequests.Add(request);

        var deliveries = recipientIds.Select(uid => new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            RequestId = request.Id,
            UserId = uid,
            Status = "delivered",
            Channel = "signalr",
            AttemptCount = 1,
            DeliveredAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }).ToList();

        db.NotificationDeliveries.AddRange(deliveries);
        await db.SaveChangesAsync(ct);

        try
        {
            var states = deliveries.Select(d => new NotificationDeliveryState(
                request.Id, d.UserId, d.Status, d.Channel, d.AttemptCount, d.DeliveredAt, d.ReadAt, d.AcknowledgedAt,
                request.Title, request.Body, request.EventType, request.Priority, targetUrl)).ToList();

            await broadcaster.BroadcastAsync(states, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to broadcast emergency (non-fatal)");
        }

        // Emergency toasts bypass quiet hours and mutes by design.
        if (push is not null)
        {
            var (title, body, requestId, ids) = (request.Title, request.Body, request.Id, recipientIds.ToList());
            push.Enqueue((p, token) => p.NotifyRequestAsync(ids, "chat.emergency", "high", title, body, targetUrl, requestId, token));
        }
    }
}
