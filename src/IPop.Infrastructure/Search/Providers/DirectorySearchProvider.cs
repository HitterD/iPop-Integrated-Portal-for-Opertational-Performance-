using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// People: active accounts by name (exact › contains) and email. Carries the NIK and free/busy hint the
/// palette's Calendar/Invite actions need, as the legacy search did.
public sealed class DirectorySearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Directory, "People", "user", 10);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "directory.read", ct);

    private sealed record Row(Guid Id, string Nik, string Name, string? Email, string? Department, DateTime UpdatedAt);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var users = db.Users.AsNoTracking().Where(u => u.IsActive);
        IQueryable<Row> Project(IQueryable<Core.Entities.User> x) =>
            x.OrderBy(u => u.Name).Select(u => new Row(u.Id, u.Nik, u.Name, u.Email, u.Department != null ? u.Department.Name : null, u.UpdatedAt));
        var tiers = new[]
        {
            Project(users.Where(u => u.Name.ToLower() == s)),
            Project(users.Where(u => u.Name.ToLower() != s && u.Name.ToLower().Contains(s))),
            Project(users.Where(u => !u.Name.ToLower().Contains(s) && u.Email != null && u.Email.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);

        // Busy right now (personal events or meetings they didn't decline) for the "Free now" hint.
        var ids = rows.Select(r => r.Id).ToList();
        var now = DateTime.UtcNow;
        var busyUntil = new Dictionary<Guid, DateTime>();
        if (ids.Count > 0)
        {
            var events = await db.CalendarEvents.AsNoTracking()
                .Where(e => ids.Contains(e.OwnerId) && e.MeetingId == null && !e.AllDay && e.StartAt <= now && e.EndAt > now)
                .Select(e => new { UserId = e.OwnerId, e.EndAt }).ToListAsync(ct);
            var meetings = await db.MeetingAttendees.AsNoTracking()
                .Where(a => ids.Contains(a.UserId) && a.RsvpStatus != "decline" && a.Meeting.Status != "cancelled" && a.Meeting.StartAt <= now && a.Meeting.EndAt > now)
                .Select(a => new { a.UserId, a.Meeting.EndAt }).ToListAsync(ct);
            foreach (var g in events.Concat(meetings).GroupBy(b => b.UserId)) busyUntil[g.Key] = g.Max(b => b.EndAt);
        }

        var hits = rows.Select(r => new SearchHit(
            r.Id.ToString(), Info.Key, r.Name, r.Department ?? "Employee",
            r.Email is not null && r.Email.Contains(s, StringComparison.OrdinalIgnoreCase) ? r.Email : null,
            $"/directory/person/{r.Id}", "user", r.UpdatedAt, SearchMatch.Classify(r.Name, s),
            AvatarColor: SearchPaging.AvatarColorFor(r.Nik), Initials: SearchPaging.Initials(r.Name), Nik: r.Nik,
            Status: busyUntil.TryGetValue(r.Id, out var until) ? $"busy:{until:o}" : "free")).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}
