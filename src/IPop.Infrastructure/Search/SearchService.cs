using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search;

/// Global search. The per-module API (ModulesAsync / SearchModuleAsync) runs one provider per request so
/// every module answers on its own and a slow or failing source never holds the others; the legacy
/// SearchAsync keeps the old one-shot shape for GET /api/search.
public class SearchService(IPopDbContext db, IEnumerable<ISearchModuleProvider>? providers = null) : ISearchService
{
    private readonly IReadOnlyList<ISearchModuleProvider> _providers = (providers ?? []).OrderBy(p => p.Info.Order).ToList();

    public async Task<IReadOnlyList<SearchModuleInfo>> ModulesAsync(Guid userId, CancellationToken ct = default)
    {
        var list = new List<SearchModuleInfo>();
        // Sequential on purpose: providers share this request's DbContext.
        foreach (var p in _providers)
            if (await p.CanSearchAsync(userId, ct)) list.Add(p.Info);
        return list;
    }

    public async Task<SearchPage?> SearchModuleAsync(Guid userId, string module, string query, int page, int pageSize, CancellationToken ct = default)
    {
        var provider = _providers.FirstOrDefault(p => p.Info.Key.Equals(module?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null) return null;
        var (p, size) = SearchText.Clamp(page, pageSize);
        var text = (query ?? "").Trim();
        if (text.Length < SearchModules.MinQueryLength) return SearchPage.Empty(provider.Info.Key, p, size);
        if (!await provider.CanSearchAsync(userId, ct)) return SearchPage.Forbidden(provider.Info.Key, p, size);
        try
        {
            return await provider.SearchAsync(new SearchQuery(userId, text, p, size), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // The reason stays in the server log; the client gets an honest "error" rather than an empty list.
            return SearchPage.Error(provider.Info.Key, "This module could not be searched right now.", p, size);
        }
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(Guid userId, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        var q = query.Trim().ToLowerInvariant();
        var results = new List<SearchResultItem>();

        // 1. People / Users
        var users = await db.Users.AsNoTracking().Include(u => u.Department)
            .Where(u => u.IsActive && (u.Name.ToLower().Contains(q) || (u.Email != null && u.Email.ToLower().Contains(q))))
            .Take(5)
            .ToListAsync(ct);

        // Busy right now (personal events or meetings they didn't decline), for the "Free now" hint.
        var ids = users.Select(u => u.Id).ToList();
        var nowUtc = DateTime.UtcNow;
        var busyEvents = await db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.OwnerId) && e.MeetingId == null && !e.AllDay && e.StartAt <= nowUtc && e.EndAt > nowUtc)
            .Select(e => new { UserId = e.OwnerId, e.EndAt }).ToListAsync(ct);
        var busyMeetings = await db.MeetingAttendees.AsNoTracking()
            .Where(a => ids.Contains(a.UserId) && a.RsvpStatus != "decline" && a.Meeting.Status != "cancelled" && a.Meeting.StartAt <= nowUtc && a.Meeting.EndAt > nowUtc)
            .Select(a => new { a.UserId, a.Meeting.EndAt }).ToListAsync(ct);
        var busyUntil = busyEvents.Concat(busyMeetings).GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.Max(b => b.EndAt));

        foreach (var u in users)
        {
            results.Add(new SearchResultItem(
                Group: "People",
                Title: u.Name,
                Subtitle: u.Department?.Name ?? "Employee",
                Url: $"/directory/person/{u.Id}",
                Icon: "user",
                AvatarColor: SearchPaging.AvatarColorFor(u.Nik),
                Initials: SearchPaging.Initials(u.Name),
                Nik: u.Nik,
                Status: busyUntil.TryGetValue(u.Id, out var until) ? $"busy:{until:o}" : "free"));
        }

        // 2. Projects & Tasks
        var isMemberProjectIds = await db.ProjectMembers.AsNoTracking()
            .Where(pm => pm.UserId == userId)
            .Select(pm => pm.ProjectId)
            .ToListAsync(ct);

        var isAdmin = await SearchPaging.IsAdminAsync(db, userId, ct);

        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.IsActive && (isAdmin || isMemberProjectIds.Contains(p.Id)) && p.Name.ToLower().Contains(q))
            .Take(4)
            .ToListAsync(ct);

        foreach (var p in projects)
        {
            results.Add(new SearchResultItem(
                Group: "Projects",
                Title: p.Name,
                Subtitle: $"Project · {p.Status} ({p.Progress}% done)",
                Url: $"/projects/{p.Id}",
                Icon: "kanban"));
        }

        var tasks = await db.ProjectTasks.AsNoTracking()
            .Where(t => (isAdmin || isMemberProjectIds.Contains(t.ProjectId)) && t.Title.ToLower().Contains(q))
            .Take(4)
            .ToListAsync(ct);

        foreach (var t in tasks)
        {
            results.Add(new SearchResultItem(
                Group: "Tasks",
                Title: t.Title,
                Subtitle: $"Task · {t.Status} ({t.Priority ?? "normal"})",
                Url: $"/projects/{t.ProjectId}?task={t.Id}",
                Icon: "check-square"));
        }

        // 3. News & Memos — only live posts the viewer's audience targeting reaches (same as the feed).
        var now = DateTime.UtcNow;
        var newsQuery = db.NewsPosts.AsNoTracking()
            .Where(n => n.Status == "published" && (n.PublishAt == null || n.PublishAt <= now) && n.Title.ToLower().Contains(q));
        if (!isAdmin)
        {
            var visibleNews = await IPop.Infrastructure.News.NewsService.VisiblePostIdsAsync(db, userId, ct);
            newsQuery = newsQuery.Where(n => visibleNews.Contains(n.Id));
        }
        var news = await newsQuery.Take(4).ToListAsync(ct);

        foreach (var n in news)
        {
            results.Add(new SearchResultItem(
                Group: "News & Memos",
                Title: n.Title,
                Subtitle: $"{n.Type} · {n.PublishedAt:dd MMM yyyy}",
                Url: $"/news?post={n.Id}",
                Icon: "newspaper"));
        }

        // 4. Mail
        // Newest of my rows first, stopping at four hits: a common subject word matches a third of all mail,
        // so starting from the matches (1.7M rows) is the slow side.
        var mails = await db.MailRecipients.AsNoTracking()
            .Where(r => r.UserId == userId && r.Folder != "trash" && r.Mail.Subject.ToLower().Contains(q))
            .OrderByDescending(r => r.CreatedAt)
            .Take(4)
            .Select(r => new { r.MailId, r.Mail.Subject, r.Mail.Sender.Name, r.Mail.SentAt })
            .ToListAsync(ct);

        foreach (var m in mails)
        {
            results.Add(new SearchResultItem(
                Group: "Mail",
                Title: m.Subject,
                Subtitle: $"From {m.Name} · {m.SentAt:dd MMM}",
                Url: $"/mail?mailId={m.MailId}",
                Icon: "mail"));
        }

        return results;
    }
}
