using IPop.Application.Dashboard;
using IPop.Application.Search;
using IPop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IPop.Api.Endpoints;

public static class DashboardAndSearchEndpoints
{
    public static IEndpointRouteBuilder MapDashboardAndSearch(this IEndpointRouteBuilder app)
    {
        // GET /api/dashboard — everything the v6 Dashboard renders, in one round trip
        app.MapGet("/api/dashboard", async (HttpContext http, IPopDbContext db, IDashboardService dashboard, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var dto = await dashboard.GetAsync(userId.Value, ct);
            return dto is null ? Results.Unauthorized() : Results.Ok(dto);
        }).RequireAuthorization();

        // GET /api/dashboard/online — only the "Team online" card, for presence-driven refreshes
        app.MapGet("/api/dashboard/online", async (HttpContext http, IPopDbContext db, IDashboardService dashboard, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await dashboard.OnlineAsync(userId.Value, ct));
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

        // GET /api/search/modules — the modules this user may search, in display order
        app.MapGet("/api/search/modules", async (HttpContext http, ISearchService searchSvc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            return Results.Ok(await searchSvc.ModulesAsync(userId.Value, ct));
        }).RequireAuthorization();

        // GET /api/search/modules/{module}?q=&page=&pageSize= — one module, one page; access is enforced
        // inside the provider. Failures come back as status "error" (never as an empty list).
        app.MapGet("/api/search/modules/{module}", async (string module, string? q, int? page, int? pageSize,
            HttpContext http, ISearchService searchSvc, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            var result = await searchSvc.SearchModuleAsync(userId.Value, module, q ?? "", page ?? 1, pageSize ?? SearchModules.DefaultPageSize, ct);
            return result is null ? Results.NotFound(new { code = "unknown_module" }) : Results.Ok(result);
        }).RequireAuthorization();

        // GET /api/search/history — recent queries for current user
        app.MapGet("/api/search/history", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var history = await db.SearchHistories
                .AsNoTracking()
                .Where(h => h.UserId == userId.Value)
                .OrderByDescending(h => h.SearchedAt)
                .Take(20)
                .Select(h => new SearchHistoryDto(h.Id, h.Query, h.SearchedAt))
                .ToListAsync(ct);

            return Results.Ok(history);
        }).RequireAuthorization();

        // POST /api/search/history — record query
        app.MapPost("/api/search/history", async (SearchHistoryRecordRequest req, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Trim().Length < 2) return Results.BadRequest();

            var cleanQuery = req.Query.Trim();
            if (cleanQuery.Length > 200) cleanQuery = cleanQuery[..200];

            var existing = await db.SearchHistories
                .FirstOrDefaultAsync(h => h.UserId == userId.Value && h.Query.ToLower() == cleanQuery.ToLower(), ct);

            if (existing is not null)
            {
                existing.Query = cleanQuery;
                existing.SearchedAt = DateTime.UtcNow;
            }
            else
            {
                db.SearchHistories.Add(new IPop.Core.Entities.SearchHistory
                {
                    Id = Guid.NewGuid(),
                    UserId = userId.Value,
                    Query = cleanQuery,
                    SearchedAt = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        }).RequireAuthorization();

        // DELETE /api/search/history/{id} — delete single query
        app.MapDelete("/api/search/history/{id:guid}", async (Guid id, HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            var item = await db.SearchHistories
                .FirstOrDefaultAsync(h => h.Id == id && h.UserId == userId.Value, ct);

            if (item is not null)
            {
                db.SearchHistories.Remove(item);
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).RequireAuthorization();

        // DELETE /api/search/history — clear all
        app.MapDelete("/api/search/history", async (HttpContext http, IPopDbContext db, CancellationToken ct) =>
        {
            var userId = await GetUserIdAsync(http, db, ct);
            if (userId is null) return Results.Unauthorized();

            await db.SearchHistories
                .Where(h => h.UserId == userId.Value)
                .ExecuteDeleteAsync(ct);

            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    public record SearchHistoryDto(Guid Id, string Query, DateTime SearchedAt);
    public record SearchHistoryRecordRequest(string Query);

    private static async Task<Guid?> GetUserIdAsync(HttpContext http, IPopDbContext db, CancellationToken ct)
    {
        // The access token already carries the user id (sub); only tokens without it fall back to the NIK lookup.
        if (IPop.Api.Auth.CurrentUser.IdFromToken(http.User) is { } fromToken) return fromToken;
        var nik = http.User.FindFirst("nik")?.Value;
        if (string.IsNullOrEmpty(nik)) return null;
        return await db.Users.AsNoTracking().Where(u => u.Nik == nik).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
