using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search;

public class SearchService(IPopDbContext db) : ISearchService
{
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(Guid userId, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        var q = query.Trim().ToLowerInvariant();
        var results = new List<SearchResultItem>();

        // 1. People / Users
        var users = await db.Users.AsNoTracking()
            .Where(u => u.IsActive && (u.Name.ToLower().Contains(q) || u.Nik.Contains(q) || (u.Email != null && u.Email.ToLower().Contains(q))))
            .Take(5)
            .ToListAsync(ct);

        foreach (var u in users)
        {
            var initials = string.Concat(u.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0])).ToUpperInvariant();
            if (initials.Length > 2) initials = initials[..2];
            results.Add(new SearchResultItem(
                Group: "People",
                Title: u.Name,
                Subtitle: $"{u.Nik} · {u.Position ?? u.Department?.Name ?? "Employee"}",
                Url: $"/directory?nik={u.Nik}",
                Icon: "user",
                AvatarColor: AvatarColorFor(u.Nik),
                Initials: initials));
        }

        // 2. Projects & Tasks
        var isMemberProjectIds = await db.ProjectMembers.AsNoTracking()
            .Where(pm => pm.UserId == userId)
            .Select(pm => pm.ProjectId)
            .ToListAsync(ct);

        var isAdmin = await db.UserRoles.AsNoTracking()
            .Include(ur => ur.Role)
            .AnyAsync(ur => ur.UserId == userId && ur.Role.Name == "Admin", ct);

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
                Url: $"/projects/{t.ProjectId}",
                Icon: "check-square"));
        }

        // 3. News & Memos
        var news = await db.NewsPosts.AsNoTracking()
            .Where(n => n.Status == "published" && n.Title.ToLower().Contains(q))
            .Take(4)
            .ToListAsync(ct);

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
        var mails = await db.MailRecipients.AsNoTracking()
            .Where(r => r.UserId == userId && r.Folder != "trash" && r.Mail.Subject.ToLower().Contains(q))
            .Take(4)
            .Select(r => new { r.MailId, r.Mail.Subject, r.Mail.Sender.Name, r.Mail.SentAt })
            .ToListAsync(ct);

        foreach (var m in mails)
        {
            results.Add(new SearchResultItem(
                Group: "Mail",
                Title: m.Subject,
                Subtitle: $"From {m.Name} · {m.SentAt:dd MMM}",
                Url: $"/mail",
                Icon: "mail"));
        }

        return results;
    }

    private static readonly string[] Palette = ["#e06c75", "#98c379", "#e5c07b", "#61afef", "#c678dd", "#56b6c2"];
    private static string AvatarColorFor(string nik) => Palette[Math.Abs(nik.GetHashCode()) % Palette.Length];
}
