using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// Projects the user is a member of (admins: every active project): name, then description.
public sealed class ProjectSearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Projects, "Projects", "kanban", 20);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "projects.read", ct);

    private sealed record Row(Guid Id, string Name, string? Description, string Status, int Progress, DateTime UpdatedAt);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var isAdmin = await SearchPaging.IsAdminAsync(db, q.UserId, ct);
        var member = db.ProjectMembers.Where(m => m.UserId == q.UserId).Select(m => m.ProjectId);
        var scope = db.Projects.AsNoTracking().Where(p => p.IsActive && (isAdmin || member.Contains(p.Id)));
        IQueryable<Row> Project(IQueryable<Core.Entities.Project> x) =>
            x.OrderByDescending(p => p.UpdatedAt).Select(p => new Row(p.Id, p.Name, p.Description, p.Status, p.Progress, p.UpdatedAt));
        var tiers = new[]
        {
            Project(scope.Where(p => p.Name.ToLower() == s)),
            Project(scope.Where(p => p.Name.ToLower() != s && p.Name.ToLower().Contains(s))),
            Project(scope.Where(p => !p.Name.ToLower().Contains(s) && p.Description != null && p.Description.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r => new SearchHit(r.Id.ToString(), Info.Key, r.Name, $"Project · {r.Status} ({r.Progress}% done)",
            SearchMatch.Classify(r.Name, s) == SearchMatch.Body ? SearchText.Snippet(r.Description, s) : null,
            $"/projects/{r.Id}", "kanban", r.UpdatedAt, SearchMatch.Classify(r.Name, s))).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}

/// Tasks in those projects: title, then description. Soft-deleted tasks are hidden by the global filter;
/// templates never show. Evidence/metadata JSON is not searched.
public sealed class TaskSearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Tasks, "Tasks", "check-square", 30);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "projects.read", ct);

    private sealed record Row(Guid Id, Guid ProjectId, string Title, string? Description, string Status, string? Priority, DateTime UpdatedAt);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var isAdmin = await SearchPaging.IsAdminAsync(db, q.UserId, ct);
        var member = db.ProjectMembers.Where(m => m.UserId == q.UserId).Select(m => m.ProjectId);
        var scope = db.ProjectTasks.AsNoTracking().Where(t => !t.IsTemplate && t.Project.IsActive && (isAdmin || member.Contains(t.ProjectId)));
        IQueryable<Row> Project(IQueryable<Core.Entities.ProjectTask> x) =>
            x.OrderByDescending(t => t.UpdatedAt).Select(t => new Row(t.Id, t.ProjectId, t.Title, t.Description, t.Status, t.Priority, t.UpdatedAt));
        var tiers = new[]
        {
            Project(scope.Where(t => t.Title.ToLower() == s)),
            Project(scope.Where(t => t.Title.ToLower() != s && t.Title.ToLower().Contains(s))),
            Project(scope.Where(t => !t.Title.ToLower().Contains(s) && t.Description != null && t.Description.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r => new SearchHit(r.Id.ToString(), Info.Key, r.Title, $"Task · {r.Status} ({r.Priority ?? "normal"})",
            SearchMatch.Classify(r.Title, s) == SearchMatch.Body ? SearchText.Snippet(r.Description, s) : null,
            $"/projects/{r.ProjectId}?task={r.Id}", "check-square", r.UpdatedAt, SearchMatch.Classify(r.Title, s))).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}
