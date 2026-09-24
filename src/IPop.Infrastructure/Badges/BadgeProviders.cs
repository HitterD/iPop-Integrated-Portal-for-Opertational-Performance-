using IPop.Application.Notifications;
using IPop.Application.Realtime;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Badges;

/// Unread top-level messages across the user's active, non-archived rooms — the same
/// criteria ChatService uses for each room's unreadCount, summed.
public sealed class ChatBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "chat";

    public Task<int> CountAsync(Guid userId, CancellationToken ct) =>
        (from m in db.ChatMessages.AsNoTracking()
         join mem in db.ChatRoomMembers.Where(x => x.UserId == userId && x.LeftAt == null) on m.RoomId equals mem.RoomId
         join r in db.ChatRooms.Where(x => x.IsActive) on m.RoomId equals r.Id
         join s in db.ChatRoomUserSettings.Where(x => x.UserId == userId) on m.RoomId equals s.RoomId into sj
         from s in sj.DefaultIfEmpty()
         where m.ThreadParentId == null && m.SenderId != userId && m.Type != "system"
               && (s == null || !s.IsArchived)
               && (s == null || s.LastReadMessageAt == null || m.CreatedAt > s.LastReadMessageAt)
         select m.Id).CountAsync(ct);
}

/// Unread mail in the inbox folder.
public sealed class MailBadgeProvider(IPopDbContext db) : IBadgeProvider
{
    public string Key => "mail";

    public Task<int> CountAsync(Guid userId, CancellationToken ct) =>
        db.MailRecipients.AsNoTracking()
            .CountAsync(r => r.UserId == userId && r.Folder == "inbox" && !r.IsRead, ct);
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
