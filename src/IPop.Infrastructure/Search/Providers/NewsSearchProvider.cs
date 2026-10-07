using IPop.Application.News;
using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.News;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// News & memos: live posts the viewer's audience targeting reaches (as the feed). News managers also find
/// unpublished posts; authors find their own drafts. Drafts carry the Draft label.
public sealed class NewsSearchProvider(IPopDbContext db, INewsService? news = null, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.News, "News & Memos", "newspaper", 70);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "news.read", ct);

    private sealed record Row(Guid Id, string Title, string Body, string BodyFormat, string Type, string Status, DateTime? PublishedAt, DateTime UpdatedAt);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var now = DateTime.UtcNow;
        var canManage = news is not null ? await news.CanManageAsync(q.UserId, ct) : await SearchPaging.IsAdminAsync(db, q.UserId, ct);
        IQueryable<Core.Entities.NewsPost> scope;
        if (canManage) scope = db.NewsPosts.AsNoTracking();
        else
        {
            var visible = await NewsService.VisiblePostIdsAsync(db, q.UserId, ct);
            scope = db.NewsPosts.AsNoTracking().Where(n =>
                (n.Status == "published" && (n.PublishAt == null || n.PublishAt <= now) && visible.Contains(n.Id))
                || (n.Status != "published" && n.AuthorId == q.UserId));
        }
        IQueryable<Row> Project(IQueryable<Core.Entities.NewsPost> x) =>
            x.OrderByDescending(n => n.PublishedAt ?? n.UpdatedAt)
             .Select(n => new Row(n.Id, n.Title, n.Body, n.BodyFormat, n.Type, n.Status, n.PublishedAt, n.UpdatedAt));
        var tiers = new[]
        {
            Project(scope.Where(n => n.Title.ToLower() == s)),
            Project(scope.Where(n => n.Title.ToLower() != s && n.Title.ToLower().Contains(s))),
            Project(scope.Where(n => !n.Title.ToLower().Contains(s) && n.Body.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r =>
        {
            var match = SearchMatch.Classify(r.Title, s);
            var draft = r.Status != "published";
            return new SearchHit(r.Id.ToString(), Info.Key, r.Title,
                draft ? $"{Label(r.Type)} · {r.Status}" : $"{Label(r.Type)} · {r.PublishedAt:dd MMM yyyy}",
                match == SearchMatch.Body ? SearchText.Snippet(NewsHtml.ToPlainText(r.Body, r.BodyFormat), s) : null,
                $"/news/{r.Id}", "newspaper", r.PublishedAt ?? r.UpdatedAt, match, Draft: draft);
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }

    private static string Label(string type) => type switch
    {
        "memo" => "Memo", "announcement" => "Announcement", "condolence" => "Condolence", _ => "News",
    };
}
