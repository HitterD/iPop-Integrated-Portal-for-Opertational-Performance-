using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

/// The one access rule for someone else's calendar. Everyone sees when the owner is busy; anything more
/// comes from the owner's grants: to the viewer personally (<see cref="CalendarDelegate"/>), or to a
/// department or chat group the viewer is currently in (<see cref="CalendarGroupShare"/>). The highest wins.
public static class CalendarAccess
{
    /// Owner → the viewer's effective level ("titles" | "view" | "edit" | "full"); owners with no grant are absent.
    public static async Task<Dictionary<Guid, string>> LevelsAsync(IPopDbContext db, Guid viewerId, IReadOnlyCollection<Guid> ownerIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (ownerIds.Count == 0) return result;
        var personal = await db.CalendarDelegates.AsNoTracking()
            .Where(d => d.DelegateId == viewerId && ownerIds.Contains(d.OwnerId))
            .Select(d => new { d.OwnerId, d.Level }).ToListAsync(ct);
        foreach (var p in personal) result[p.OwnerId] = DelegateLevel.Max(result.GetValueOrDefault(p.OwnerId), p.Level)!;

        var dept = await db.Users.AsNoTracking().Where(u => u.Id == viewerId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        var rooms = await db.ChatRoomMembers.AsNoTracking().Where(m => m.UserId == viewerId && m.LeftAt == null).Select(m => m.RoomId).ToListAsync(ct);
        var groups = await db.CalendarGroupShares.AsNoTracking()
            .Where(g => ownerIds.Contains(g.OwnerId)
                && ((g.DepartmentId != null && g.DepartmentId == dept) || (g.RoomId != null && rooms.Contains(g.RoomId.Value))))
            .Select(g => new { g.OwnerId, g.Level }).ToListAsync(ct);
        foreach (var g in groups) result[g.OwnerId] = DelegateLevel.Max(result.GetValueOrDefault(g.OwnerId), g.Level)!;
        return result;
    }

    public static async Task<string?> LevelAsync(IPopDbContext db, Guid viewerId, Guid ownerId, CancellationToken ct) =>
        viewerId == ownerId ? DelegateLevel.Full : (await LevelsAsync(db, viewerId, [ownerId], ct)).GetValueOrDefault(ownerId);

    /// What a viewer at <paramref name="level"/> sees of one event: its visibility as shown, whether the
    /// title/location show, and whether the full detail (description, attendees) may be opened.
    /// Owner and invited people always see everything.
    public static (string Visibility, bool Title, bool Detail) Shown(string eventVisibility, string? level, bool invited, bool self)
    {
        if (self || invited) return (Normalize(eventVisibility), true, true);
        if (eventVisibility == CalendarVisibility.Private)
            return (CalendarVisibility.Private, level == DelegateLevel.Full, level == DelegateLevel.Full);
        return (CalendarVisibility.Busy, DelegateLevel.Rank(level) >= DelegateLevel.Rank(DelegateLevel.Titles), DelegateLevel.SeesDetails(level));
    }

    /// Legacy "public" reads as Normal.
    public static string Normalize(string? v) => v == CalendarVisibility.Private ? CalendarVisibility.Private : CalendarVisibility.Busy;

    public static string Label(string? level) => level switch
    {
        DelegateLevel.Full => "Act on my behalf",
        DelegateLevel.Edit => "Edit my events",
        DelegateLevel.View => "See all details",
        DelegateLevel.Titles => "See event titles",
        _ => "See when I'm busy",
    };

    /// Same ladder from the viewer's side ("You can …").
    public static string ViewerLabel(string? level) => level switch
    {
        DelegateLevel.Full => "You act on their behalf",
        DelegateLevel.Edit => "You can edit their events",
        DelegateLevel.View => "You can see all details",
        DelegateLevel.Titles => "You can see event titles",
        _ => "You can see when they're busy",
    };
}
