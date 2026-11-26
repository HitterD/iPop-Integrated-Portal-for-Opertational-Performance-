using IPop.Application.Notifications;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// The recipient inbox query (GET /api/notifications/v1/inbox), split out of NotificationGatewayEndpoints.cs unchanged (file size).
public static partial class NotificationGatewayEndpoints
{
    /// <summary>
    /// Per-user inbox query. The delivery's request carries the event metadata (title, body,
    /// priority, event type); the delivery row carries the per-user state (read/acknowledged,
    /// channel, delivered time). Type is derived from the client's EventType so module filters
    /// (chat/mail/news/project/system) work uniformly even though event types are free-form.
    /// </summary>
    internal static async Task<InboxListResult> ListInboxCore(
        IPopDbContext db, Guid userId, string? type, string? status, int page, int pageSize, CancellationToken ct,
        string? category = null, string? search = null, bool summary = true)
    {
        var p = Math.Max(1, page);
        var size = Math.Clamp(pageSize, 1, 100);

        var q = db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.UserId == userId);

        // Category: NotificationCategory.Of as one SQL CASE (NotificationCategorySql), never every delivery in memory.
        if (!string.IsNullOrWhiteSpace(category) && NotificationCategory.All.Contains(category.Trim().ToLowerInvariant()))
            q = q.Where(NotificationCategorySql.Is(category.Trim().ToLowerInvariant()));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            q = q.Where(d => d.Request.Title.ToLower().Contains(term) || d.Request.Body.ToLower().Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim().ToLowerInvariant();
            // EF can't translate ClassifyEventType — express the same predicate with
            // string operations it supports (StartsWith), mirrored on the module naming
            // convention (chat.mention, mail.new, memo.ack, task.assigned...).
            q = t switch
            {
                "chat" => q.Where(d => d.Request.EventType.StartsWith("chat.")),
                "mail" => q.Where(d => d.Request.EventType.StartsWith("mail.")),
                "news" => q.Where(d => d.Request.EventType.StartsWith("news.") || d.Request.EventType.StartsWith("memo.")),
                "project" => q.Where(d => d.Request.EventType.StartsWith("project.") || d.Request.EventType.StartsWith("task.")),
                "system" => q.Where(d => !d.Request.EventType.StartsWith("chat.") && !d.Request.EventType.StartsWith("mail.") &&
                            !d.Request.EventType.StartsWith("news.") && !d.Request.EventType.StartsWith("memo.") &&
                            !d.Request.EventType.StartsWith("project.") && !d.Request.EventType.StartsWith("task.")),
                _ => q,
            };
        }
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            q = s switch
            {
                "snoozed" => q.Where(d => d.SnoozedUntil != null && d.SnoozedUntil > now),
                "read" => q.Where(d => d.Status == NotificationStatus.Read || d.Status == NotificationStatus.Acknowledged),
                "unread" => q.Where(d => d.Status != NotificationStatus.Read && d.Status != NotificationStatus.Acknowledged && (d.SnoozedUntil == null || d.SnoozedUntil <= now)),
                _ => q,
            };
        }
        else
        {
            q = q.Where(d => d.SnoozedUntil == null || d.SnoozedUntil <= now);
        }

        var total = await q.CountAsync(ct);
        var unread = !summary ? 0 : await db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.UserId == userId &&
                d.Status != NotificationStatus.Read &&
                d.Status != NotificationStatus.Acknowledged &&
                (d.SnoozedUntil == null || d.SnoozedUntil <= now), ct);

        // Newest first: the per-recipient timestamp (DeliveredAt, else UpdatedAt) leads.
        // ClassifyEventType is not translatable to SQL — pull the raw event type and map in memory.
        var rows = await q
            .OrderByDescending(d => d.DeliveredAt ?? d.UpdatedAt)
            .Skip((p - 1) * size).Take(size)
            .Select(d => new
            {
                d.RequestId,
                d.Request.EventType,
                d.Request.Title,
                d.Request.Body,
                d.Request.Priority,
                d.Request.TargetUrl,
                Source = d.Request.Client.ClientId,
                d.DeliveredAt,
                d.UpdatedAt,
                d.Status,
                d.SnoozedUntil,
                d.Request.ActorUserId,
            })
            .ToListAsync(ct);

        // Who caused each row (name only: never NIK or position).
        var actorIds = rows.Where(r => r.ActorUserId != null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var actorNames = actorIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        // Unread per category (snoozed excluded) for the page's sidebar: the list itself is paged. Grouped in SQL.
        var byCategory = !summary ? null : (await db.NotificationDeliveries.AsNoTracking()
            .Where(d => d.UserId == userId && d.Status != NotificationStatus.Read && d.Status != NotificationStatus.Acknowledged
                && (d.SnoozedUntil == null || d.SnoozedUntil <= now))
            .Select(NotificationCategorySql.Of).GroupBy(c => c).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .ToDictionary(g => g.Key, g => g.Count);
        var snoozedCount = !summary ? 0 : await db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.UserId == userId && d.SnoozedUntil != null && d.SnoozedUntil > now, ct);

        var items = rows.Select(r => new InboxItemRow(
            r.RequestId,
            ClassifyEventType(r.EventType),
            r.Title,
            r.Body,
            r.Source,
            r.DeliveredAt ?? r.UpdatedAt,
            r.Status == NotificationStatus.Read || r.Status == NotificationStatus.Acknowledged,
            r.Priority,
            r.TargetUrl,
            r.SnoozedUntil,
            r.ActorUserId,
            r.ActorUserId is { } a ? actorNames.GetValueOrDefault(a) : null,
            r.EventType,
            NotificationCategory.Of(r.EventType, r.TargetUrl))).ToList();

        return new InboxListResult(items, total, unread, p, size, byCategory, snoozedCount);
    }
}
