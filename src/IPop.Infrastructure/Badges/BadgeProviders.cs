using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Badges;

/// Unread top-level messages across the user's active, non-archived, unmuted rooms — the
/// same criteria ChatService uses for each room's unreadCount, summed. A mute with an
/// expiry that has passed no longer counts as muted.
public sealed class ChatBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "chat";

    public Task<int> CountAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return (from m in db.ChatMessages.AsNoTracking()
         join mem in db.ChatRoomMembers.Where(x => x.UserId == userId && x.LeftAt == null) on m.RoomId equals mem.RoomId
         join r in db.ChatRooms.Where(x => x.IsActive) on m.RoomId equals r.Id
         join s in db.ChatRoomUserSettings.Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
         from s in sj.DefaultIfEmpty()
         where m.ThreadParentId == null && m.SenderId != userId && m.Type != "system"
               && (s == null || !s.IsArchived)
               && (s == null || !s.IsMuted || (s.MuteExpiresAt != null && s.MuteExpiresAt <= now))
               && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
         select m.Id).CountAsync(ct);
    }
}

/// Unread mail in the inbox folder.
public sealed class MailBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "mail";

    public Task<int> CountAsync(Guid userId, CancellationToken ct) =>
        db.MailRecipients.AsNoTracking()
            .CountAsync(r => r.UserId == userId && r.Folder == "inbox" && !r.IsRead && !r.IsMuted, ct);
}

/// Same count as GET /api/notifications/v1/unread-count.
public sealed class NotificationBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "notif";

    public Task<int> CountAsync(Guid userId, CancellationToken ct) =>
        db.NotificationDeliveries.AsNoTracking()
            .CountAsync(d => d.UserId == userId
                && d.Status != NotificationStatus.Read
                && d.Status != NotificationStatus.Acknowledged, ct);
}

/// Mandatory published memos requiring acknowledgement that the user has not acknowledged yet.
public sealed class NewsBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "news";

    public async Task<int> CountAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ackedPostIds = db.NewsAcknowledgements.AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.NewsPostId);

        return await db.NewsPosts.AsNoTracking()
            .Where(p => p.Status == "published"
                && p.RequiresAcknowledgement
                && (p.PublishAt == null || p.PublishAt <= now)
                && !ackedPostIds.Contains(p.Id))
            .CountAsync(ct);
    }
}

/// Unseen missed incoming calls in the last 7 days (opening /calls marks them seen).
public sealed class CallsBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "calls";

    public Task<int> CountAsync(Guid userId, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        return db.CallHistories.AsNoTracking()
            .CountAsync(h => h.PerformerId == userId && h.Direction == "incoming" && h.Result == "missed" && h.SeenAt == null && h.StartedAt >= cutoff, ct);
    }
}


/// Tickets iDesk marked RESOLVED: they wait for the requester to confirm in iDesk.
public sealed class TicketsBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => IPop.Application.Tickets.TicketEvents.BadgeKey;

    public Task<int> CountAsync(Guid userId, CancellationToken ct) =>
        db.Tickets.AsNoTracking().CountAsync(t => t.UserId == userId && t.RemovedAt == null
            && t.Status == IPop.Application.Tickets.TicketStatus.Resolved, ct);
}
