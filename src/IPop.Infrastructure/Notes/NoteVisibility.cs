using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Notes;

/// Which of some Notes pages a person can open right now, answered straight from the database for callers that only have the
/// DbContext (the calendar feed). Same rule as <see cref="NoteService"/>: the page is live (not in Trash, no trashed ancestor), and the
/// person owns it or an ancestor, or it (or an ancestor) is shared with them or with a chat room they are in.
public static class NoteVisibility
{
    private const int MaxDepth = 32;

    public static async Task<HashSet<Guid>> OpenableAsync(IPopDbContext db, Guid userId, IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        var result = new HashSet<Guid>();
        if (pageIds.Count == 0) return result;

        // Walk up from the asked pages, loading each page's parent chain once (breadth first, so one query per level).
        var nodes = new Dictionary<Guid, (Guid? Parent, Guid Owner, DateTime? Deleted)>();
        var frontier = pageIds.Distinct().ToList();
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var rows = await db.NotePages.AsNoTracking().Where(p => frontier.Contains(p.Id))
                .Select(p => new { p.Id, p.ParentId, p.OwnerId, p.DeletedAt }).ToListAsync(ct);
            foreach (var r in rows) nodes[r.Id] = (r.ParentId, r.OwnerId, r.DeletedAt);
            frontier = rows.Where(r => r.ParentId is not null && !nodes.ContainsKey(r.ParentId.Value)).Select(r => r.ParentId!.Value).Distinct().ToList();
        }

        var rooms = await db.ChatRoomMembers.AsNoTracking().Where(m => m.UserId == userId && m.LeftAt == null).Select(m => m.RoomId).ToListAsync(ct);
        var allIds = nodes.Keys.ToList();
        var shared = (await db.NoteShares.AsNoTracking()
            .Where(s => allIds.Contains(s.PageId) && (s.UserId == userId || (s.RoomId != null && rooms.Contains(s.RoomId.Value))))
            .Select(s => s.PageId).ToListAsync(ct)).ToHashSet();

        foreach (var id in pageIds)
        {
            var opens = false; var live = true;
            Guid? cur = id;
            for (var depth = 0; cur is { } c && depth < MaxDepth; depth++)
            {
                if (!nodes.TryGetValue(c, out var n)) { live = false; break; }
                if (n.Deleted is not null) { live = false; break; }
                if (n.Owner == userId || shared.Contains(c)) opens = true;
                cur = n.Parent;
            }
            if (live && opens) result.Add(id);
        }
        return result;
    }
}
