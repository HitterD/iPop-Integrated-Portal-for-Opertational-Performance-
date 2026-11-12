using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Chat;

public sealed partial class ChatService
{
    /// Unread counts stop here; the UI shows "99+" from 100. Counting a room's full unread history
    /// costs one index entry per message, so an old room nobody opened grew slower every day.
    public const int UnreadCap = 100;

    /// Read mark for a room never opened. Npgsql sends DateTime.MinValue (UTC) as -infinity.
    private static readonly DateTime NeverRead = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    /// TotalCount for a page read with Take(pageSize + 1): exact when this is the last page, otherwise
    /// "at least one more" — clients only compare it with what they hold to show "load older".
    internal static int PageTotal(int page, int pageSize, int fetched)
        => (page - 1) * pageSize + Math.Min(fetched, pageSize) + (fetched > pageSize ? 1 : 0);

    public sealed record SearchHit(Guid Id, DateTime CreatedAt);

    public sealed record RoomStat(Guid RoomId, Guid? LastId, int Unread);

    private Task<List<RoomStat>> RoomStatsAsync(IReadOnlyCollection<Guid> roomIds, Guid userId, CancellationToken ct)
        => RoomStatsAsync(db, roomIds, userId, ct);

    /// Per room: the newest top-level message and the unread count from others (capped), both as
    /// correlated LIMIT subqueries so Postgres walks (RoomId, CreatedAt) backwards and stops early.
    /// Same unread rule everywhere: top level, not mine, not system, newer than my read mark.
    public static async Task<List<RoomStat>> RoomStatsAsync(IPopDbContext db, IReadOnlyCollection<Guid> roomIds, Guid userId, CancellationToken ct)
    {
        if (roomIds.Count == 0) return [];
        var ids = roomIds.ToList();
        return await db.ChatRooms.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new
            {
                r.Id,
                LastId = db.ChatMessages
                    .Where(m => m.RoomId == r.Id && m.ThreadParentId == null)
                    .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                    .Select(m => (Guid?)m.Id)
                    .FirstOrDefault(),
                // "CreatedAt > mark" must stay a range on the index: as a NOT EXISTS filter a fully read
                // room was walked end to end looking for 100 matches that never come.
                Unread = db.ChatMessages
                    .Where(m => m.RoomId == r.Id && m.ThreadParentId == null && m.SenderId != userId && m.Type != "system"
                        && m.CreatedAt > (db.ChatRoomUserSettings
                            .Where(s => s.UserId == userId && s.RoomId == r.Id)
                            .Select(s => s.LastReadMessageAt)
                            .FirstOrDefault() ?? NeverRead))
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(UnreadCap)
                    .Count(),
            })
            .Select(x => new RoomStat(x.Id, x.LastId, x.Unread))
            .ToListAsync(ct);
    }
}
