using IPop.Application.Search;
using IPop.Core.Entities;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class DashboardAndSearchEndpoints
{
    public static IEndpointRouteBuilder MapDashboardAndSearch(this IEndpointRouteBuilder app)
    {
        // GET /api/dashboard — aggregated metrics for the current user
        app.MapGet("/api/dashboard", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var now = DateTime.UtcNow;

            // Unread mail
            var mailUnread = await db.MailRecipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId.Value && r.Folder == "inbox" && !r.IsRead && !r.IsMuted, ct);
            var mailTotal = await db.MailRecipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId.Value && r.Folder == "inbox", ct);
            var mailDrafts = await db.MailRecipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId.Value && r.Folder == "draft", ct);

            // Active projects
            var isMemberProjectIds = await db.ProjectMembers.AsNoTracking()
                .Where(pm => pm.UserId == userId.Value)
                .Select(pm => pm.ProjectId)
                .ToListAsync(ct);

            var isAdmin = await db.UserRoles.AsNoTracking()
                .Include(ur => ur.Role)
                .AnyAsync(ur => ur.UserId == userId.Value && ur.Role.Name == "Admin", ct);

            var activeProjectsQuery = db.Projects.AsNoTracking()
                .Where(p => p.IsActive && (isAdmin || isMemberProjectIds.Contains(p.Id)));

            var activeProjectsCount = await activeProjectsQuery.CountAsync(ct);
            var avgProgress = activeProjectsCount > 0 ? (await activeProjectsQuery.AverageAsync(p => (double)p.Progress, ct)) : 0;

            // Memos requiring acknowledgement
            var ackPending = await db.NewsPosts.AsNoTracking()
                .Where(n => n.Status == "published" && n.RequiresAcknowledgement
                    && (n.PublishAt == null || n.PublishAt <= now)
                    && !db.NewsAcknowledgements.Any(a => a.NewsPostId == n.Id && a.UserId == userId.Value))
                .CountAsync(ct);

            // Online employees
            var activeCutoff = now.AddMinutes(-5);
            var onlineEmployees = await db.Users.AsNoTracking()
                .CountAsync(u => u.IsActive && u.PresenceLastSeenAt >= activeCutoff, ct);

            return Results.Ok(new
            {
                mail = new { unread = mailUnread, total = mailTotal, drafts = mailDrafts },
                projects = new { active = activeProjectsCount, avgProgress = Math.Round(avgProgress, 1) },
                memos = new { ackPending },
                presence = new { online = onlineEmployees }
            });
        }).RequireAuthorization();

        // GET /api/search?q= — global search across people, projects, tasks, news, mail
        app.MapGet("/api/search", async (string? q, HttpContext http, ISearchService searchSvc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(Array.Empty<SearchResultItem>());

            var results = await searchSvc.SearchAsync(userId.Value, q.Trim(), ct);
            return Results.Ok(results);
        }).RequireAuthorization();

        return app;
    }

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
