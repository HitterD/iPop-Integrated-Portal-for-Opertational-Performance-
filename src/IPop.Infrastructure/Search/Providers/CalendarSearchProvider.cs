using IPop.Application.Search;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// Calendar & meetings. Searchable: my own events, meetings I'm invited to (not declined/cancelled), and other
/// people's events only where their grant to me shows titles (details only with "See all details" or more).
/// Busy-only access gives nothing to search; Private events show only to an "Act on my behalf" assistant.
public sealed class CalendarSearchProvider(IPopDbContext db) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Calendar, "Calendar", "calendar", 80);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);

    private sealed record Cand(string Id, string Title, string? Where, string? Details, DateTime StartAt, string Url, string Subtitle);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var me = q.UserId;
        var cands = new List<Cand>();

        // 1) Mine: every field.
        var mine = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.OwnerId == me && (e.Title.ToLower().Contains(s) || (e.Location != null && e.Location.ToLower().Contains(s)) || (e.Notes != null && e.Notes.ToLower().Contains(s))))
            .OrderByDescending(e => e.StartAt)
            .Select(e => new { e.Id, e.Title, e.Location, e.Notes, e.StartAt, e.MeetingId }).ToListAsync(ct);
        cands.AddRange(mine.Select(e => new Cand($"event:{e.Id}", e.Title, e.Location, e.Notes, e.StartAt,
            $"/calendar?event={e.Id}&date={e.StartAt.ToLocalTime():yyyy-MM-dd}", e.MeetingId is null ? "My event" : "My meeting")));

        // 2) Meetings I'm invited to (organiser's row carries the detail page).
        var invited = await db.MeetingAttendees.AsNoTracking()
            .Where(a => a.UserId == me && a.RsvpStatus != "decline" && a.Meeting.Status != MeetingStatus.Cancelled && a.Meeting.OrganizerId != me
                && (a.Meeting.Title.ToLower().Contains(s) || (a.Meeting.Location != null && a.Meeting.Location.ToLower().Contains(s)) || (a.Meeting.Agenda != null && a.Meeting.Agenda.ToLower().Contains(s))))
            .OrderByDescending(a => a.Meeting.StartAt)
            .Select(a => new
            {
                a.MeetingId, a.Meeting.Title, a.Meeting.Location, a.Meeting.Agenda, a.Meeting.StartAt, Organizer = a.Meeting.Organizer.Name,
                EventId = db.CalendarEvents.Where(e => e.MeetingId == a.MeetingId).OrderBy(e => e.CreatedAt).Select(e => (Guid?)e.Id).FirstOrDefault(),
            }).ToListAsync(ct);
        cands.AddRange(invited.Select(m => new Cand($"meeting:{m.MeetingId}", m.Title, m.Location, m.Agenda, m.StartAt,
            m.EventId is { } eid ? $"/calendar?event={eid}&date={m.StartAt.ToLocalTime():yyyy-MM-dd}" : $"/calendar?date={m.StartAt.ToLocalTime():yyyy-MM-dd}",
            $"Meeting · {m.Organizer}")));

        // 3) Other people's calendars, by the level they gave me (personally, or via my department / chat groups).
        var levels = await LevelsAsync(me, ct);
        var titleOwners = levels.Where(l => DelegateLevel.Rank(l.Value) >= DelegateLevel.Rank(DelegateLevel.Titles)).Select(l => l.Key).ToList();
        if (titleOwners.Count > 0)
        {
            var detailOwners = levels.Where(l => DelegateLevel.SeesDetails(l.Value)).Select(l => l.Key).ToList();
            var fullOwners = levels.Where(l => l.Value == DelegateLevel.Full).Select(l => l.Key).ToList();
            var shared = await db.CalendarEvents.AsNoTracking()
                .Where(e => titleOwners.Contains(e.OwnerId)
                    && (e.Visibility != CalendarVisibility.Private || fullOwners.Contains(e.OwnerId))
                    && (e.Title.ToLower().Contains(s) || (e.Location != null && e.Location.ToLower().Contains(s))
                        || (detailOwners.Contains(e.OwnerId) && e.Notes != null && e.Notes.ToLower().Contains(s))))
                .OrderByDescending(e => e.StartAt)
                .Select(e => new { e.Id, e.Title, e.Location, e.Notes, e.StartAt, e.OwnerId, Owner = e.Owner.Name, Detail = detailOwners.Contains(e.OwnerId) })
                .ToListAsync(ct);
            cands.AddRange(shared.Select(e => new Cand($"shared:{e.Id}", e.Title, e.Location, e.Detail ? e.Notes : null, e.StartAt,
                e.Detail ? $"/calendar?event={e.Id}&date={e.StartAt.ToLocalTime():yyyy-MM-dd}" : $"/calendar/person/{e.OwnerId}?date={e.StartAt.ToLocalTime():yyyy-MM-dd}",
                $"{e.Owner}'s calendar")));
        }

        var ranked = cands.DistinctBy(c => c.Id)
            .OrderBy(c => SearchMatch.Rank(SearchMatch.Classify(c.Title, s))).ThenByDescending(c => c.StartAt);
        var (items, total) = SearchPaging.Page(ranked, q.Skip, q.PageSize);
        var hits = items.Select(c =>
        {
            var match = SearchMatch.Classify(c.Title, s);
            var body = match == SearchMatch.Body ? SearchText.Snippet(c.Where is not null && c.Where.Contains(s, StringComparison.OrdinalIgnoreCase) ? c.Where : c.Details, s) : null;
            return new SearchHit(c.Id, Info.Key, c.Title, $"{c.Subtitle} · {c.StartAt.ToLocalTime():dd MMM yyyy HH:mm}", body, c.Url, "calendar", c.StartAt, match);
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }

    /// Mirrors CalendarAccess.LevelsAsync (API layer): personal delegate grants and department/room group shares, highest wins.
    private async Task<Dictionary<Guid, string>> LevelsAsync(Guid viewerId, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var personal = await db.CalendarDelegates.AsNoTracking().Where(d => d.DelegateId == viewerId)
            .Select(d => new { d.OwnerId, d.Level }).ToListAsync(ct);
        foreach (var p in personal) result[p.OwnerId] = DelegateLevel.Max(result.GetValueOrDefault(p.OwnerId), p.Level)!;
        var dept = await db.Users.AsNoTracking().Where(u => u.Id == viewerId).Select(u => u.DepartmentId).FirstOrDefaultAsync(ct);
        var rooms = await db.ChatRoomMembers.AsNoTracking().Where(m => m.UserId == viewerId && m.LeftAt == null).Select(m => m.RoomId).ToListAsync(ct);
        var groups = await db.CalendarGroupShares.AsNoTracking()
            .Where(g => (g.DepartmentId != null && g.DepartmentId == dept) || (g.RoomId != null && rooms.Contains(g.RoomId.Value)))
            .Select(g => new { g.OwnerId, g.Level }).ToListAsync(ct);
        foreach (var g in groups) result[g.OwnerId] = DelegateLevel.Max(result.GetValueOrDefault(g.OwnerId), g.Level)!;
        result.Remove(viewerId);
        return result;
    }
}
