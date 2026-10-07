using IPop.Application.Rbac;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Infrastructure.Search.Providers;

/// Tickets: the user's own iDesk tickets from the local mirror (the same source /tickets lists), by number,
/// title, then description. Tickets removed in iDesk are hidden.
public sealed class TicketSearchProvider(IPopDbContext db, IPermissionEvaluator? perms = null) : ISearchModuleProvider
{
    public SearchModuleInfo Info { get; } = new(SearchModules.Tickets, "Tickets", "life-buoy", 90);

    public Task<bool> CanSearchAsync(Guid userId, CancellationToken ct) => SearchPaging.AllowedAsync(perms, userId, "tickets.read", ct);

    private sealed record Row(Guid Id, string? Number, string Title, string? Description, string Status, string ModuleName, string Category, DateTime UpdatedAt);

    public async Task<SearchPage> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var s = q.Lower;
        var scope = db.Tickets.AsNoTracking().Where(t => t.UserId == q.UserId && t.RemovedAt == null);
        IQueryable<Row> Project(IQueryable<Core.Entities.Ticket> x) =>
            x.OrderByDescending(t => t.UpdatedAt).Select(t => new Row(t.Id, t.TicketNumber, t.Title, t.Description, t.Status, t.ModuleName, t.Category, t.UpdatedAt));
        var tiers = new[]
        {
            Project(scope.Where(t => t.Title.ToLower() == s || (t.TicketNumber != null && t.TicketNumber.ToLower() == s))),
            Project(scope.Where(t => t.Title.ToLower() != s && (t.TicketNumber == null || t.TicketNumber.ToLower() != s)
                && (t.Title.ToLower().Contains(s) || (t.TicketNumber != null && t.TicketNumber.ToLower().Contains(s))))),
            Project(scope.Where(t => !t.Title.ToLower().Contains(s) && (t.TicketNumber == null || !t.TicketNumber.ToLower().Contains(s))
                && t.Description != null && t.Description.ToLower().Contains(s))),
        };
        var (rows, total) = await SearchPaging.PageAsync(tiers, q.Skip, q.PageSize, ct);
        var hits = rows.Select(r =>
        {
            var titleMatch = SearchMatch.Classify(r.Title, s);
            var numberMatch = r.Number is not null && r.Number.Contains(s, StringComparison.OrdinalIgnoreCase);
            var match = titleMatch != SearchMatch.Body ? titleMatch : numberMatch ? SearchMatch.Title : SearchMatch.Body;
            return new SearchHit(r.Id.ToString(), Info.Key, r.Title, $"{r.Number ?? "Pending"} · {r.ModuleName} · {r.Status}",
                match == SearchMatch.Body ? SearchText.Snippet(r.Description, s) : null,
                $"/tickets/{r.Id}", "life-buoy", r.UpdatedAt, match);
        }).ToList();
        return SearchPage.Ready(Info.Key, hits, total, q.Page, q.PageSize);
    }
}
