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
